using WorkThrough;

namespace WorkThroughWeb;

// The address "signups.welcome-email" is what the database stores. Rename or move these types freely; keep the address.
[JobContract("signups.welcome-email")]
public sealed record WelcomeEmail(string Address);

public sealed class WelcomeEmailJob(ILogger<WelcomeEmailJob> logger) : IJob<WelcomeEmail>
{
    public Task<JobResult> ExecuteAsync(WelcomeEmail input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // An attempt can repeat after a crash. Pass context.IdempotencyKey to the mail service so it sends once.
        logger.LogInformation("Sending a welcome email to {Address}, attempt {Attempt}", input.Address, context.Attempt);

        return Task.FromResult(JobResult.Done());
    }
}
