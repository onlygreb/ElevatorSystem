using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace ElevatorSystem.Persistence.Tests;

/// <summary>What a host gets from <c>AddElevatorPersistence</c>: configuration, validation, health and retention.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PersistenceRegistrationTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AddElevatorPersistence_BindsTheSectionAndResolvesWorkingPostgresPorts()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var provider = Build(new()
        {
            ["ConnectionStrings:engine-db"] = connectionString,
            ["Persistence:ConnectionStringName"] = "engine-db",
            ["Persistence:ProcessedCommandRetention"] = "2.00:00:00",
            ["Persistence:LeaderLockId"] = "42",
        });

        await provider.GetRequiredService<ElevatorDatabaseMigrator>().MigrateAsync(TestContext.Current.CancellationToken);
        var stateStore = provider.GetRequiredService<IElevatorStateStore>();
        await stateStore.SaveAsync(new Elevator(Building.Default, TimeProvider.System, new Floor(4)).Snapshot(), TestContext.Current.CancellationToken);

        stateStore.ShouldBeOfType<PostgresElevatorStateStore>();
        provider.GetRequiredService<IProcessedCommandStore>().ShouldBeOfType<PostgresProcessedCommandStore>();
        provider.GetRequiredService<ILeaderElector>().ShouldBeOfType<PostgresLeaderElector>();
        (await stateStore.LoadAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull().CurrentFloor.ShouldBe(new Floor(4));
        var options = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        options.ProcessedCommandRetention.ShouldBe(TimeSpan.FromDays(2));
        options.LeaderLockId.ShouldBe(42);
    }

    [Theory]
    [InlineData("ConnectionStringName", " ")]
    [InlineData("ProcessedCommandRetention", "00:30:00")]
    [InlineData("ProcessedCommandPurgeInterval", "00:00:00")]
    [InlineData("LeaderAcquireRetryInterval", "-00:00:01")]
    [InlineData("LeaderCheckInterval", "00:00:00.010")]
    [InlineData("LeaderCheckInterval", "00:05:00")]
    public async Task AddElevatorPersistence_InvalidSetting_FailsValidation(string setting, string value)
    {
        await using var provider = Build(new() { [$"Persistence:{setting}"] = value });

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value);

        failure.Message.ShouldContain($"Persistence:{setting}");
    }

    [Fact]
    public async Task AddElevatorPersistence_MissingConnectionString_FailsWithItsName()
    {
        await using var provider = Build(new() { ["Persistence:ConnectionStringName"] = "nowhere" });

        var failure = Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IElevatorStateStore>());

        failure.Message.ShouldContain("ConnectionStrings:nowhere");
    }

    [Fact]
    public async Task HealthCheck_DatabaseAnswers_IsHealthy()
    {
        await using var provider = Build(new() { ["ConnectionStrings:elevatordb"] = await postgres.CreateEmptyDatabaseAsync() });

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Entries[ElevatorDatabaseHealthCheck.Name].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries[ElevatorDatabaseHealthCheck.Name].Tags.ShouldContain("ready");
    }

    [Fact]
    public async Task HealthCheck_DatabaseUnreachable_IsDegradedNotUnhealthy()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString) { Port = 1, Timeout = 1 }.ConnectionString;
        await using var provider = Build(new() { ["ConnectionStrings:elevatordb"] = unreachable });

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Entries[ElevatorDatabaseHealthCheck.Name].Status.ShouldBe(HealthStatus.Degraded, "the car keeps operating without its database (ADR-0007)");
    }

    [Fact]
    public async Task RetentionService_OnStart_PurgesExpiredCommandIds()
    {
        var clock = new FakeTimeProvider();
        await using var provider = Build(new() { ["ConnectionStrings:elevatordb"] = await postgres.CreateEmptyDatabaseAsync() }, clock);
        await provider.GetRequiredService<ElevatorDatabaseMigrator>().MigrateAsync(TestContext.Current.CancellationToken);
        var commands = provider.GetRequiredService<IProcessedCommandStore>();
        await commands.TryMarkProcessedAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(8));
        var recent = Guid.CreateVersion7();
        await commands.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken);
        var service = provider.GetServices<IHostedService>().OfType<ProcessedCommandRetentionService>().Single();

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(async () => await CountProcessedAsync(provider) == 1);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        (await commands.TryMarkProcessedAsync(recent, TestContext.Current.CancellationToken)).ShouldBeFalse("only the expired ID was purged");
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddElevatorPersistence();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task<int> CountProcessedAsync(ServiceProvider provider)
    {
        await using var context = await provider.GetRequiredService<IDbContextFactory<ElevatorDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await context.ProcessedCommands.CountAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (!await condition())
        {
            TimeProvider.System.GetElapsedTime(deadline).ShouldBeLessThan(Patience, "the condition never became true");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }
}
