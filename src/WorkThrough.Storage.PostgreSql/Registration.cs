namespace WorkThrough.Storage;

public static class PostgresRegistration
{
    public static WorkThroughBuilder UsePostgreSql(this WorkThroughBuilder builder, string connectionString) =>
        builder.UseStore(new PostgresJobStore(connectionString));
}
