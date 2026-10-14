using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using WorkThrough.Storage;

namespace WorkThrough.Tests;

internal sealed class StoreHarness : IAsyncDisposable
{
    private string? schema;
    private string? adminConnection;
    private string? database;
    private string? oraclePassword;
    private string? mySqlDatabase;
    public string? OracleSchema => Store is OracleJobStore ? schema : null;
    public string? ReadOnlyConnectionString { get; private set; }
    /// <summary>A SQL Server database with READ_COMMITTED_SNAPSHOT on, where settlements and unrestricted claims skip the application lock.</summary>
    public const string SqlServerVersionedReads = "sqlserver-rcsi";

    public required Func<RelationalJobStore> NewStore { get; init; }
    public required string ConnectionString { get; init; }
    public RelationalJobStore Store => NewStore();

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
            command.CommandText = System.Text.RegularExpressions.Regex.Replace(sql, "@([A-Za-z][A-Za-z0-9_]*)", ":fixture_$1");
        }

        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = command is OracleCommand ? "fixture_" + name : name;
            parameter.Value = value;
            if (parameter is OracleParameter oracleParameter && value is string { Length: > 2000 })
            {
                oracleParameter.OracleDbType = OracleDbType.Clob;
            }

            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    public DbConnection CreateConnection() => Store switch
    {
        OracleJobStore => new OracleConnection(ConnectionString),
        SqlServerJobStore => new SqlConnection(ConnectionString),
        PostgresJobStore => new NpgsqlConnection(ConnectionString),
        MySqlJobStore => new MySqlConnection(ConnectionString),
        _ => throw new InvalidOperationException("Unknown test provider.")
    };

    public WorkThroughBuilder Register(WorkThroughBuilder builder) => Store switch
    {
        OracleJobStore => builder.UseOracle(ConnectionString),
        SqlServerJobStore => builder.UseSqlServer(ConnectionString),
        PostgresJobStore => builder.UsePostgreSql(ConnectionString),
        MySqlJobStore => builder.UseMySql(ConnectionString),
        _ => throw new InvalidOperationException("Unknown test provider.")
    };

    public static async Task<StoreHarness> CreateAsync(string provider, bool initialize = true)
    {
        StoreHarness harness;
        if (provider == "oracle")
        {
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_ORACLE")
                ?? throw new InvalidOperationException("Oracle tests require a disposable PDB administrator connection.");
            var schema = "WT_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
            var password = "Test_" + Guid.NewGuid().ToString("N");
            await using var connection = new OracleConnection(admin);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE USER {schema} IDENTIFIED BY {password} DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"GRANT CREATE SESSION, CREATE TABLE, CREATE TRIGGER TO {schema}";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"GRANT EXECUTE ON SYS.DBMS_LOCK TO {schema}";
            await command.ExecuteNonQueryAsync();
            // Bound each fixture pool; cleanup clears and disconnects only this exact disposable user.
            var cs = new OracleConnectionStringBuilder(admin) { UserID = schema, Password = password, MinPoolSize = 0, MaxPoolSize = 16 }.ConnectionString;
            harness = new()
            {
                schema = schema,
                oraclePassword = password,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new OracleJobStore(cs)
            };
        }
        else if (provider is "sqlserver" or SqlServerVersionedReads)
        {
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_SQLSERVER")
                ?? throw new InvalidOperationException("SQL Server tests require a disposable server connection with database creation privileges.");
            var database = "dj_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new SqlConnection(admin);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET ALLOW_SNAPSHOT_ISOLATION ON;" +
                (provider == SqlServerVersionedReads ? $" ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON;" : "");
            await command.ExecuteNonQueryAsync();
            var cs = new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ToString();
            harness = new()
            {
                database = database,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new SqlServerJobStore(cs)
            };
        }
        else if (provider == "postgres")
        {
            var admin = Environment.GetEnvironmentVariable("DURABLEJOBS_POSTGRES")
                ?? throw new InvalidOperationException("The tests require DURABLEJOBS_POSTGRES: a disposable PostgreSQL database where they create schemas.");
            var schema = "dj_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
            await command.ExecuteNonQueryAsync();
            var cs = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema }.ToString();
            harness = new()
            {
                schema = schema,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new PostgresJobStore(cs)
            };
        }
        else if (provider is "mysql" or "mariadb")
        {
            var variable = provider == "mysql" ? "DURABLEJOBS_MYSQL" : "DURABLEJOBS_MARIADB";
            var admin = Environment.GetEnvironmentVariable(variable)
                ?? throw new InvalidOperationException($"{provider} tests require {variable}: a disposable server connection with database creation privileges.");
            var name = "dj_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new MySqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new MySqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
            // Bound each fixture pool, as for Oracle, so parallel tests stay within the server's connection limit.
            var cs = new MySqlConnectionStringBuilder(admin) { Database = name, MaximumPoolSize = 16 }.ConnectionString;
            harness = new()
            {
                mySqlDatabase = name,
                adminConnection = admin,
                ConnectionString = cs,
                NewStore = () => new MySqlJobStore(cs)
            };
        }
        else
        {
            throw new ArgumentException("Unknown test provider.", nameof(provider));
        }

        try
        {
            if (initialize)
            {
                await harness.Store.InitializeAsync();
            }

            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async Task ProvisionReadOnlyOracleAsync()
    {
        if (oraclePassword is null || ReadOnlyConnectionString is not null)
        {
            return;
        }

        var login = schema + "_R";
        await using var connection = new OracleConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE USER {login} IDENTIFIED BY {oraclePassword}";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"GRANT CREATE SESSION TO {login}";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"BEGIN FOR t IN (SELECT table_name FROM all_tables WHERE owner='{schema}') LOOP EXECUTE IMMEDIATE 'GRANT SELECT ON {schema}.' || t.table_name || ' TO {login}'; END LOOP; END;";
        await command.ExecuteNonQueryAsync();
        ReadOnlyConnectionString = new OracleConnectionStringBuilder(ConnectionString) { UserID = login, Pooling = false }.ConnectionString;
    }

    public async Task ProvisionReadOnlySqlServerAsync()
    {
        if (database is null || ReadOnlyConnectionString is not null)
        {
            return;
        }

        var login = database + "_reader";
        var password = "Test_" + Guid.NewGuid().ToString("N") + "!";
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF; USE [{database}]; CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT TO [{login}];";
        await command.ExecuteNonQueryAsync();
        ReadOnlyConnectionString = new SqlConnectionStringBuilder(ConnectionString)
        {
            UserID = login,
            Password = password,
            Pooling = false
        }.ToString();
    }

    /// <summary>A login that may only read this fixture's database. MySQL limits user names to 32 characters.</summary>
    public async Task ProvisionReadOnlyMySqlAsync()
    {
        if (mySqlDatabase is null || ReadOnlyConnectionString is not null)
        {
            return;
        }

        var login = MySqlReader;
        var password = "Test_" + Guid.NewGuid().ToString("N");
        await using var connection = new MySqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new MySqlCommand($"CREATE USER '{login}'@'%' IDENTIFIED BY '{password}'; GRANT SELECT ON {mySqlDatabase}.* TO '{login}'@'%';",
            connection);
        await command.ExecuteNonQueryAsync();
        ReadOnlyConnectionString = new MySqlConnectionStringBuilder(ConnectionString) { UserID = login, Password = password, Pooling = false }.ConnectionString;
    }

    private string MySqlReader => "r_" + mySqlDatabase![^24..];

    public async ValueTask DisposeAsync()
    {
        if (database is not null)
        {
            SqlConnection.ClearPool(new SqlConnection(ConnectionString));
            await using var connection = new SqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];";
            if (ReadOnlyConnectionString is not null)
            {
                command.CommandText += $"DROP LOGIN [{database}_reader];";
            }

            await command.ExecuteNonQueryAsync();
        }

        if (oraclePassword is not null)
        {
            OracleConnection.ClearPool(new OracleConnection(ConnectionString));
            await using var connection = new OracleConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            if (ReadOnlyConnectionString is not null)
            {
                await DropOracleUserAsync(connection, schema + "_R");
            }

            await DropOracleUserAsync(connection, schema!);
        }
        else if (schema is not null)
        {
            // Every test's search path creates a separate pool. Release only this test's idle connections.
            await using var pooledConnection = new NpgsqlConnection(ConnectionString);
            NpgsqlConnection.ClearPool(pooledConnection);
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }

        if (mySqlDatabase is not null)
        {
            await MySqlConnection.ClearPoolAsync(new MySqlConnection(ConnectionString));
            await using var connection = new MySqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new MySqlCommand($"DROP DATABASE {mySqlDatabase}" +
                (ReadOnlyConnectionString is null ? "" : $"; DROP USER '{MySqlReader}'@'%'"), connection);
            await command.ExecuteNonQueryAsync();
        }
    }
    private static async Task DropOracleUserAsync(OracleConnection connection, string user)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP USER {user} CASCADE";

        // Sessions killed by fault tests can take several seconds to leave v$session on a loaded instance.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                await command.ExecuteNonQueryAsync();

                return;
            }
            catch (OracleException error) when (error.Number == 1940 && DateTime.UtcNow < deadline)
            {
                // Oracle may retain pooled sessions after ClearPool. Disconnect only this fixture's exact user.
                await using var sessions = connection.CreateCommand();
                sessions.BindByName = true;
                sessions.CommandText = "SELECT sid,serial# FROM v$session WHERE username=:owner";
                sessions.Parameters.Add("owner", OracleDbType.Varchar2).Value = user;
                var identities = new List<(int Id, int Serial)>();
                await using (var reader = await sessions.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        identities.Add((reader.GetInt32(0), reader.GetInt32(1)));
                    }
                }

                foreach (var (id, serial) in identities)
                {
                    await using var disconnect = connection.CreateCommand();
                    disconnect.CommandText = $"ALTER SYSTEM DISCONNECT SESSION '{id},{serial}' IMMEDIATE";
                    try
                    {
                        await disconnect.ExecuteNonQueryAsync();
                    }
                    catch (OracleException gone) when (gone.Number is 30 or 31)
                    {
                        // A pooled session may finish closing between enumeration and disconnection.
                    }
                }

                await Task.Delay(250);
            }
        }
    }

}
