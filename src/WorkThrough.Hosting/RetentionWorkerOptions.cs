namespace WorkThrough;

public sealed class RetentionWorkerOptions
{
    public string Owner { get; set; } = $"retention:{Guid.NewGuid():N}";
    public OperationActor Actor { get; set; } = new("workthrough-maintenance", "retention-service");
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan BatchBudget { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    private const int MaximumOwnerLength = 200;
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumBatchBudget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumBatchBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (PollInterval < MinimumPollInterval || BatchBudget < MinimumBatchBudget || BatchBudget > MaximumBatchBudget
            || LeaseDuration < MinimumLease || LeaseDuration > MaximumLease || LeaseDuration <= BatchBudget
            || string.IsNullOrWhiteSpace(Owner) || Owner.Length > MaximumOwnerLength || Actor is null
            || string.IsNullOrWhiteSpace(Actor.Issuer) || string.IsNullOrWhiteSpace(Actor.Subject))
        {
            throw new ArgumentException("Invalid retention host identity, interval, budget or lease duration.");
        }
    }
}
