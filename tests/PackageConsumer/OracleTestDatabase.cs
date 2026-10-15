using Oracle.ManagedDataAccess.Client;

// Disposable database infrastructure shared by package and independent-process proofs.
internal sealed class OracleTestDatabase(string admin, string schema, string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public static async Task<OracleTestDatabase> CreateAsync(string admin)
    {
        var schema = "WT_PROOF_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var password = "Test_" + Guid.NewGuid().ToString("N");
        await using var connection = new OracleConnection(admin);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE USER {schema} IDENTIFIED BY {password} DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"GRANT CREATE SESSION, CREATE TABLE TO {schema}";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"GRANT EXECUTE ON SYS.DBMS_LOCK TO {schema}";
        await command.ExecuteNonQueryAsync();
        var cs = new OracleConnectionStringBuilder(admin)
        {
            UserID = schema,
            Password = password,
            MinPoolSize = 0,
            MaxPoolSize = 8
        }.ConnectionString;

        return new(admin, schema, cs);
    }

    public async ValueTask DisposeAsync()
    {
        OracleConnection.ClearPool(new OracleConnection(ConnectionString));
        await using var connection = new OracleConnection(admin);
        await connection.OpenAsync();
        for (var attempt = 0; ; attempt++)
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP USER {schema} CASCADE";
            try
            {
                await drop.ExecuteNonQueryAsync();

                return;
            }
            catch (OracleException error) when (error.Number == 1940 && attempt < 20)
            {
                await using var sessions = connection.CreateCommand();
                sessions.BindByName = true;
                sessions.CommandText = "SELECT sid,serial# FROM v$session WHERE username=:owner";
                sessions.Parameters.Add("owner", OracleDbType.Varchar2).Value = schema;
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
                        // This exact fixture session closed while it was being enumerated.
                    }
                }

                await Task.Delay(250);
            }
        }
    }
}
