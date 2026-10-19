using Npgsql;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// The connections to the elevator database: a pooled data source for the stores, and dedicated,
/// unpooled connections for leader leases (a pooled connection could carry the advisory lock back
/// into the pool).
/// </summary>
public sealed class ElevatorDatabase : IAsyncDisposable, IDisposable
{
    private readonly string _leaseConnectionString;

    public ElevatorDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        DataSource = NpgsqlDataSource.Create(connectionString);
        _leaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            // Breaking a timed-out lease check at once is the point: never wait on a dead server.
            CancellationTimeout = -1,
            ApplicationName = LeaseApplicationName,
        }.ConnectionString;
    }

    /// <summary>The <c>application_name</c> of lease connections, so operators can spot the leader in <c>pg_stat_activity</c>.</summary>
    public const string LeaseApplicationName = "ElevatorSystem leader lease";

    public NpgsqlDataSource DataSource { get; }

    internal NpgsqlConnection CreateLeaseConnection() => new(_leaseConnectionString);

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    public void Dispose() => DataSource.Dispose();
}
