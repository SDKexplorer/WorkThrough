using System.Data.Common;
using Microsoft.Data.Sqlite;
using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ReadRetryTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("wt-read-retry-").FullName;

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

    private async Task<ConflictingStore> CreateStoreAsync(int conflicts, bool failure = false)
    {
        var store = new ConflictingStore($"Data Source={Path.Combine(directory, "jobs.db")}", conflicts, failure);
        await store.InitializeAsync();

        return store;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class SnapshotConflictException() : DbException("Simulated snapshot conflict.");

    private sealed class UnrelatedFailureException() : DbException("Simulated provider failure.");

    /// <summary>SQLite storage whose read snapshots fail a configured number of times before succeeding.</summary>
    private sealed class ConflictingStore(string connectionString, int conflicts, bool failure) : RelationalJobStore
    {
        public int ReadsStarted { get; private set; }

        protected override DbConnection CreateConnection() => new SqliteConnection(connectionString);

        protected override string ClockSql => "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";

        protected override Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: false));

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

            return Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: true));
        }

        protected override bool IsReadSnapshotConflict(DbException error) => error is SnapshotConflictException;
    }
}
