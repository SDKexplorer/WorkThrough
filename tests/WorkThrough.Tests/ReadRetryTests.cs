using System.Data.Common;
using Npgsql;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ReadRetryTests : IAsyncLifetime
{
    private StoreHarness harness = null!;

    public async Task InitializeAsync() => harness = await StoreHarness.CreateAsync("postgres");

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Snapshot_conflicts_are_retried_with_a_fresh_snapshot()
    {
        var store = await CreateStoreAsync(conflicts: 2);
        var id = await new JobClient(store).EnqueueAsync(new Alpha("retried"));

        Assert.Equal(id, (await store.GetAsync(id))!.Id);
        Assert.Equal(3, store.ReadsStarted);
    }

    [Fact]
    public async Task Persistent_snapshot_conflicts_fail_the_read()
    {
        var store = await CreateStoreAsync(conflicts: int.MaxValue);
        var id = await new JobClient(store).EnqueueAsync(new Alpha("conflicted"));

        await Assert.ThrowsAsync<SnapshotConflictException>(() => store.GetAsync(id));
        Assert.Equal(3, store.ReadsStarted);
    }

    [Fact]
    public async Task Other_read_failures_are_not_retried()
    {
        var store = await CreateStoreAsync(conflicts: 0, failure: true);

        await Assert.ThrowsAsync<UnrelatedFailureException>(() => store.GetAsync(Guid.NewGuid()));
        Assert.Equal(1, store.ReadsStarted);
    }

    private Task<ConflictingStore> CreateStoreAsync(int conflicts, bool failure = false) =>
        Task.FromResult(new ConflictingStore(harness.ConnectionString, conflicts, failure));

    private sealed class SnapshotConflictException() : DbException("Simulated snapshot conflict.");

    private sealed class UnrelatedFailureException() : DbException("Simulated provider failure.");

    /// <summary>PostgreSQL storage, already initialized by the harness, whose read snapshots fail a configured number of times before succeeding.</summary>
    private sealed class ConflictingStore(string connectionString, int conflicts, bool failure) : RelationalJobStore
    {
        public int ReadsStarted { get; private set; }

        protected override DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

        protected override string ClockSql => "SELECT CAST(FLOOR(EXTRACT(EPOCH FROM clock_timestamp()) * 1000) AS BIGINT)";

        protected override async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
            await connection.BeginTransactionAsync(cancellationToken);

        protected override Task<DbTransaction> BeginReadAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            ReadsStarted++;
            if (failure)
            {
                throw new UnrelatedFailureException();
            }

            if (ReadsStarted <= conflicts)
            {
                throw new SnapshotConflictException();
            }

            return connection.BeginTransactionAsync(cancellationToken).AsTask();
        }

        protected override bool IsReadSnapshotConflict(DbException error) => error is SnapshotConflictException;
    }
}
