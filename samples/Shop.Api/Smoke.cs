using WorkThrough;

namespace Shop.Api;

/// <summary>Places a small and a review-sized order, approves the second, and waits until both checkouts succeed.</summary>
internal static class Smoke
{
    public const string Argument = "--smoke";
    public const string Url = "http://127.0.0.1:0";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task RunAsync(WebApplication app)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        await app.StartAsync(deadline.Token);
        try
        {
            var orders = app.Services.GetRequiredService<Orders>();
            var small = await orders.PlaceAsync(new("tenant-a", "ada", 40m), deadline.Token);
            var large = await orders.PlaceAsync(new("tenant-b", "grace", CheckoutFlow.ReviewThreshold + 100m), deadline.Token);
            var queries = app.Services.GetRequiredService<IJobQueries>();
            var smallRun = await RunAsync(orders, small, deadline.Token);
            var largeRun = await RunAsync(orders, large, deadline.Token);
            if (!await orders.ApproveAsync(large, "smoke", deadline.Token))
            {
                throw new InvalidOperationException("The review approval was not accepted.");
            }

            await StatusAsync(queries, smallRun, FlowStatus.Succeeded, deadline.Token);
            await StatusAsync(queries, largeRun, FlowStatus.Succeeded, deadline.Token);
            Console.WriteLine("PASS: orders committed with their work, one checkout waited for review approval, and both completed.");
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> RunAsync(Orders orders, string orderId, CancellationToken ct)
    {
        while (true)
        {
            if (await orders.RunAsync(orderId, ct) is { } run)
            {
                return run;
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static async Task StatusAsync(IJobQueries queries, Guid run, FlowStatus expected, CancellationToken ct)
    {
        while ((await queries.GetWorkflowAsync(run, ct))?.Status != expected)
        {
            await Task.Delay(PollInterval, ct);
        }
    }
}
