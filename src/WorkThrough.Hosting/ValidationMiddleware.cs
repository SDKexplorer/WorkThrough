using System.Text.Json;

namespace WorkThrough;

internal sealed class ValidationMiddleware<TInput>(IEnumerable<IJobValidator<TInput>> validators) : IJobMiddleware
{
    public async Task<JobResult> InvokeAsync(JobInvocation invocation, JobContinuation next, CancellationToken ct)
    {
        if (invocation.Registration.RequestType == typeof(TInput))
        {
            var input = invocation.Job.Payload.Deserialize<TInput>(JobJson.Options) ?? throw new JsonException("Null processor input.");
            foreach (var validator in validators)
            {
                await validator.ValidateAsync(input, invocation.Execution, ct);
            }
        }

        return await next();
    }
}
