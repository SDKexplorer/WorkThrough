using System.Data.Common;
using WorkThrough;
using WorkThrough.Storage;

namespace Shop.Api;

public sealed record PlaceOrder(string TenantId, string Customer, decimal Amount);

/// <summary>Order intake: the business row and the work it causes commit in one transaction, or neither does.</summary>
public sealed class Orders(RelationalJobStore store, IExecutionQueries executions, WorkflowClient flows)
{
    private const int MaximumTextLength = 200;

    // Business tables live beside the job tables here; a separate business database would use SendAsync and the outbox.
    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS shop_orders (id VARCHAR(36) PRIMARY KEY, tenant_id VARCHAR(200) NOT NULL,
          customer VARCHAR(200) NOT NULL, amount DECIMAL(18,2) NOT NULL)
        """;

    public Task InitializeAsync(CancellationToken ct) => store.InTransactionAsync(async (connection, transaction, _, token) =>
        await Execute(connection, transaction, CreateTable).ExecuteNonQueryAsync(token), ct);

    public async Task<string> PlaceAsync(PlaceOrder request, CancellationToken ct)
    {
        Validate(request);
        var order = new OrderPlaced(Guid.NewGuid().ToString("N"), request.TenantId, request.Customer, request.Amount);
        await store.InTransactionAsync(async (connection, transaction, jobs, token) =>
        {
            await using var insert = Execute(connection, transaction,
                "INSERT INTO shop_orders(id,tenant_id,customer,amount) VALUES (@id,@tenant,@customer,@amount)",
                ("id", order.OrderId), ("tenant", order.TenantId), ("customer", order.Customer), ("amount", order.Amount));
            await insert.ExecuteNonQueryAsync(token);

            return await jobs.EnqueueAsync(ShopJobs.StartCheckout, order,
                new(CorrelationId: CheckoutFlow.Correlation(order.OrderId), TenantId: order.TenantId), token);
        }, ct);

        return order.OrderId;
    }

    /// <summary>Resumes a checkout waiting in manual review. Repeating the same approval is accepted once.</summary>
    public async Task<bool> ApproveAsync(string orderId, string reviewer, CancellationToken ct)
    {
        if (await RunAsync(orderId, ct) is not { } run)
        {
            return false;
        }

        return await flows.SignalAsync(run, CheckoutFlow.ReviewApproved, new ReviewApproved(orderId, reviewer), $"approve:{orderId}", ct);
    }

    /// <summary>Looks back as far as a review can still be waiting; older checkouts can no longer be approved.</summary>
    public async Task<Guid?> RunAsync(string orderId, CancellationToken ct)
    {
        var steps = await executions.SearchExecutionsAsync(new(CorrelationId: CheckoutFlow.Correlation(orderId),
            From: DateTimeOffset.UtcNow - CheckoutFlow.ReviewTimeout), ct);

        return steps.Items.Select(step => step.WorkflowRunId).FirstOrDefault(run => run is not null);
    }

    private static void Validate(PlaceOrder request)
    {
        AdmissionKeys.Validate(request.TenantId ?? throw new ArgumentException("A tenant is required."), KeyScope.Tenant, nameof(request));
        if (string.IsNullOrWhiteSpace(request.Customer) || request.Customer.Length > MaximumTextLength)
        {
            throw new ArgumentException($"Customer must contain 1–{MaximumTextLength} characters.", nameof(request));
        }
    }

    private static DbCommand Execute(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
