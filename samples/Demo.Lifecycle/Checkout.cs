using WorkThrough;
using WorkThrough.Storage;

namespace Demo.Lifecycle;

public sealed class Checkout(SqliteJobStore business)
{
    public Task<Guid> AcceptAsync(OrderPlaced order, CancellationToken ct) => business.InTransactionAsync(async (connection, transaction, jobs, token) =>
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO sample_orders(id,email) VALUES (@id,@email) ON CONFLICT(id) DO NOTHING";
        var id = insert.CreateParameter();
        id.ParameterName = "@id";
        id.Value = order.OrderId;
        insert.Parameters.Add(id);
        var email = insert.CreateParameter();
        email.ParameterName = "@email";
        email.Value = order.CustomerEmail;
        insert.Parameters.Add(email);
        await insert.ExecuteNonQueryAsync(token);

        // Both the business write and this delivery request commit in the business database.
        return await jobs.SendAsync(OrderDefinitions.Notify, order,
            new(CorrelationId: OrderDefinitions.Correlation(order.OrderId), IdempotencyKey: OrderDefinitions.AcceptanceKey(order.OrderId)), token);
    }, ct);

    public Task<bool> InitializeAsync(CancellationToken ct) => business.InTransactionAsync(async (connection, transaction, _, token) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS sample_orders(id TEXT PRIMARY KEY,email TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(token);

        return true;
    }, ct);
}

[ManualProcessorRegistration]
public sealed class OrderNotification : IProcessor<OrderPlaced>
{
    public Task<JobResult> ProcessAsync(OrderPlaced input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // A real notification API must enforce context.IdempotencyKey for repeat attempts.
        // No email is sent by this sample.
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(JobResult.From(new { input.OrderId, notificationPrepared = true }));
    }
}
