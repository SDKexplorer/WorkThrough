using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using WorkThrough.Storage;

namespace WorkThrough.Exercises;

internal sealed class ExerciseDatabase(string provider, string connectionString, string? schema, string directory) : IAsyncDisposable
{
    public string Provider { get; } = provider;
    public string ConnectionString { get; } = connectionString;
    public string DirectoryPath { get; } = directory;
    public RelationalJobStore Store { get; } = OpenStore(provider, connectionString);
    public static RelationalJobStore OpenStore(string provider, string connection) =>
        provider == "postgres" ? new PostgresJobStore(connection) : new SqliteJobStore(connection);

    public static async Task<ExerciseDatabase> CreateAsync(string provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workthrough-exercises", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? schema = null;
        string connection;
        if (provider == "postgres")
        {
            schema = "wt_exercise_" + Guid.NewGuid().ToString("N");
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES") ?? throw new InvalidOperationException("A disposable PostgreSQL connection is required.");
            await using var database = new NpgsqlConnection(admin);
            await database.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", database);
            await command.ExecuteNonQueryAsync();
            connection = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema }.ToString();
        }
        else
        {
            connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "jobs.db"), DefaultTimeout = 30 }.ToString();
        }

        var result = new ExerciseDatabase(provider, connection, schema, directory);
        await result.Store.InitializeAsync();

        return result;
    }

    public DbConnection OpenConnection() => Provider == "postgres" ? new NpgsqlConnection(ConnectionString) : new SqliteConnection(ConnectionString);

    public async Task<string> VersionAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Provider == "postgres" ? "SELECT version()" : "SELECT sqlite_version()";

        var version = (string)(await command.ExecuteScalarAsync())!;
        if (Provider == "sqlite")
        {
            command.CommandText = "PRAGMA journal_mode";
            var journal = await command.ExecuteScalarAsync();
            command.CommandText = "PRAGMA synchronous";
            version += $"; journal_mode={journal}; synchronous={await command.ExecuteScalarAsync()}";
        }

        return version;
    }

    public async ValueTask DisposeAsync()
    {
        if (schema is not null)
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(DirectoryPath, true);
    }
}
