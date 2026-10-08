using WorkThrough.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class SqliteConfigurationTests
{
    [Theory]
    [InlineData(true, "wal")]
    [InlineData(false, "delete")]
    public async Task Journal_configuration_preserves_full_commit_durability(bool useWal, string expectedJournal)
    {
        await using var database = await StoreHarness.CreateAsync("sqlite", initialize: false);
        var store = new SqliteJobStore(database.ConnectionString, new(UseWriteAheadLogging: useWal));
        await store.InitializeAsync();
        await store.InTransactionAsync(async (connection, transaction, _, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA journal_mode";
            Assert.Equal(expectedJournal, await command.ExecuteScalarAsync(ct));
            command.CommandText = "PRAGMA synchronous";
            Assert.Equal(2L, await command.ExecuteScalarAsync(ct));

            return true;
        });
    }
}
