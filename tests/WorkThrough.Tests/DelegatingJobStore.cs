namespace WorkThrough.Tests;

internal abstract class DelegatingJobStore(IJobStore inner) : IJobStore
{
    public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);
    public virtual Task<Guid> EnqueueAsync(JobRecord job, CancellationToken ct = default) => inner.EnqueueAsync(job, ct);
    public Task SeedLimitsAsync(IReadOnlyList<ConcurrencyLimit> limits, CancellationToken ct = default) => inner.SeedLimitsAsync(limits, ct);
    public Task SetLimitAsync(ConcurrencyLimit limit, CancellationToken ct = default) => inner.SetLimitAsync(limit, ct);
    public virtual Task<JobRecord?> ClaimAsync(ClaimRequest request, CancellationToken ct = default) => inner.ClaimAsync(request, ct);
    public virtual Task<bool> RenewAsync(Guid id, Guid token, TimeSpan duration, CancellationToken ct = default) => inner.RenewAsync(id, token, duration, ct);
    public Task<bool> CompleteAsync(Guid id, Guid token, JobResult result, CancellationToken ct = default) => inner.CompleteAsync(id, token, result, ct);
    public Task<bool> FailAsync(Guid id, Guid token, string error, TimeSpan delay, CancellationToken ct = default) => inner.FailAsync(id, token, error, delay, ct);
    public Task<bool> ReleaseAsync(Guid id, Guid token, string reason, CancellationToken ct = default) => inner.ReleaseAsync(id, token, reason, ct);
    public Task<bool> RetryAsync(Guid id, CancellationToken ct = default) => inner.RetryAsync(id, ct);
    public Task<bool> CancelAsync(Guid id, CancellationToken ct = default) => inner.CancelAsync(id, ct);
    public Task<Guid> StartWorkflowAsync(FlowDefinition definition, System.Text.Json.JsonElement input, string? correlation, CancellationToken ct = default) => inner.StartWorkflowAsync(definition, input, correlation, ct);
    public Task RecordHandlerAsync(Guid id, Guid token, string handler, CancellationToken ct = default) => inner.RecordHandlerAsync(id, token, handler, ct);
}
