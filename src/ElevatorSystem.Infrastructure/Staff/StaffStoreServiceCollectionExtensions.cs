using ElevatorSystem.Application.Audit;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class StaffStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StaffDbContext"/> (a factory, and a scoped instance for Identity), the
    /// <see cref="IAuditStore"/> over it and the <see cref="StaffDatabaseInitializer"/>. The caller chooses the
    /// provider in <paramref name="configure"/>: <see cref="StaffDbContext.ConfigureNpgsql"/> in production.
    /// </summary>
    public static IServiceCollection AddStaffStore(this IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDbContextFactory<StaffDbContext>(configure);
        services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<StaffDbContext>>().CreateDbContext());
        services.AddSingleton<IAuditStore, EfAuditStore>();
        services.AddSingleton<StaffDatabaseInitializer>();
        return services;
    }
}
