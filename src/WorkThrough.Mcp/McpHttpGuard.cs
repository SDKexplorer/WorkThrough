using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace WorkThrough;

internal sealed class McpHttpGuard(WorkThroughMcpOptions options) : IDisposable
{
    private readonly SemaphoreSlim concurrency = new(options.ConcurrentRequests);
    private readonly PartitionedRateLimiter<ClaimsPrincipal> requests = PartitionedRateLimiter.Create<ClaimsPrincipal, string>(user =>
        RateLimitPartition.GetFixedWindowLimiter(user.FindFirst("iss")!.Value + "\n" + user.FindFirst("sub")!.Value,
            _ => new() { PermitLimit = options.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps && !options.AllowLoopbackHttp)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        // Bearer credentials are the only input. Reject cross-origin browser calls unless they match the configured public origin.
        var origin = context.Request.Headers.Origin;
        if (origin.Count > 0 && (origin.Count != 1 || origin[0] != options.Resource.GetLeftPart(UriPartial.Authority)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            return;
        }

        using var rate = requests.AttemptAcquire(context.User);
        if (!rate.IsAcquired || !await concurrency.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";

            return;
        }

        var originalBody = context.Request.Body;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[16 * 1024 + 1];
            var length = await originalBody.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, deadline.Token);
            if (length == buffer.Length)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

                return;
            }

            if (length > 0 && !HasBoundedRequestId(buffer.AsMemory(0, length)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;

                return;
            }

            await using var body = new MemoryStream(buffer, 0, length, writable: false);
            context.Request.Body = body;
            await next(context);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            }
        }
        finally
        {
            context.Request.Body = originalBody;
            concurrency.Release();
        }
    }

    public void Dispose()
    {
        concurrency.Dispose();
        requests.Dispose();
    }

    private static bool HasBoundedRequestId(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.ValueKind == JsonValueKind.Object && (!root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || id.GetString()!.Length <= 128);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
