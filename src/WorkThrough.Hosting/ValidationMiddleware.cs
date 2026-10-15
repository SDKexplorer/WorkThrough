using System.Text.Json;

namespace WorkThrough;

internal sealed class ValidationMiddleware<TInput>(IEnumerable<IJobValidator<TInput>> validators) : IJobMiddleware
{
    public async Task<JobResult> InvokeAsync(JobInvocation invocation, JobContinuation next, CancellationToken ct)
    {
        if (invocation.Registration.RequestType == typeof(TInput))
        {
            var input = JobJson.Deserialize<TInput>(invocation.Job.Payload) ?? throw new JsonException("Null processor input.");
            foreach (var validator in validators)
            {
                await validator.ValidateAsync(input, invocation.Execution, ct);
            }
        }

        return await next();
    }
}
