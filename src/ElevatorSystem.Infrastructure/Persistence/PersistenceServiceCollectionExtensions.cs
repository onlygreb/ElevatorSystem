using ElevatorSystem.Application.Ports;
using ElevatorSystem.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL implementations of <see cref="IElevatorStateStore"/>,
    /// <see cref="IProcessedCommandStore"/> and <see cref="ILeaderElector"/>, the
    /// <see cref="ElevatorDatabaseMigrator"/>, the processed-command retention job and the
    /// <c>elevator-database</c> health check (tagged <c>ready</c>, <c>Degraded</c> on failure).
    /// Settings come from the <c>Persistence</c> section, validated at start-up, and the connection
    /// string from <c>ConnectionStrings:{Persistence:ConnectionStringName}</c>.
    /// </summary>
    public static IServiceCollection AddElevatorPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PersistenceOptions>()
            .BindConfiguration(PersistenceOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PersistenceOptions>, PersistenceOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(provider =>
        {
            var name = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value.ConnectionStringName;
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString(name);
            return string.IsNullOrWhiteSpace(connectionString)
                ? throw new InvalidOperationException($"The connection string 'ConnectionStrings:{name}' for the elevator database is missing.")
                : new ElevatorDatabase(connectionString);
        });
        services.AddDbContextFactory<ElevatorDbContext>((provider, builder) =>
            ElevatorDbContext.Configure(builder, provider.GetRequiredService<ElevatorDatabase>().DataSource));

        services.TryAddSingleton<ElevatorDatabaseMigrator>();
        services.TryAddSingleton<PostgresElevatorStateStore>();
        services.TryAddSingleton<PostgresProcessedCommandStore>();
        services.TryAddSingleton<PostgresLeaderElector>();

        // Plain Add, not TryAdd: calling this replaces any in-memory implementation registered earlier.
        services.AddSingleton<IElevatorStateStore>(provider => provider.GetRequiredService<PostgresElevatorStateStore>());
        services.AddSingleton<IProcessedCommandStore>(provider => provider.GetRequiredService<PostgresProcessedCommandStore>());
        services.AddSingleton<ILeaderElector>(provider => provider.GetRequiredService<PostgresLeaderElector>());

        services.AddHostedService<ProcessedCommandRetentionService>();
        services.AddHealthChecks()
            .AddCheck<ElevatorDatabaseHealthCheck>(ElevatorDatabaseHealthCheck.Name, HealthStatus.Degraded, ["ready"]);

        return services;
    }
}
