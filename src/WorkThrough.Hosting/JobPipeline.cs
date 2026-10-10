using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

internal static class JobPipeline
{
    public static Task<JobResult> InvokeAsync(IServiceProvider services, JobInvocation invocation, object processor, CancellationToken ct)
    {
        JobContinuation next = () => invocation.Registration.InvokeAsync(processor, invocation.Job.Payload, invocation.Execution, ct);
        foreach (var middleware in services.GetServices<IJobMiddleware>().Reverse())
        {
            var inner = next;
            next = async () =>
            {
                var gate = new object();
                var calls = 0;
                Task<JobResult>? continuation = null;
                JobResult result;
                var returnedEarly = false;
                var closed = false;
                try
                {
                    result = await middleware.InvokeAsync(invocation, () =>
                    {
                        lock (gate)
                        {
                            if (closed || ++calls != 1)
                            {
                                throw new InvalidOperationException("Middleware called its continuation outside its scope or more than once.");
                            }

                            return continuation = inner();
                        }
                    }, ct);
                    returnedEarly = continuation is not null && !continuation.IsCompleted;
                }
                finally
                {
                    lock (gate)
                    {
                        closed = true;
                    }

                    // Never dispose the scope or release local capacity while a launched processor still runs.
                    if (continuation is not null)
                    {
                        await continuation;
                    }
                }

                if (calls != 1 || continuation is null || returnedEarly)
                {
                    throw new InvalidOperationException("Middleware must call and await its continuation exactly once.");
                }

                ct.ThrowIfCancellationRequested();

                return result;
            };
        }

        return next();
    }
}
