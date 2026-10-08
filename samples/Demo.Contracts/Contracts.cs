using WorkThrough;

namespace Demo.Contracts;

[JobContract(OrderJobs.ValidateKey)]
public sealed record ValidateOrder(string OrderId, decimal Amount);

[JobContract(OrderJobs.DispatchKey)]
public sealed record DispatchOrder(string OrderId);

[JobContract(OrderJobs.RejectKey)]
public sealed record RejectOrder(string OrderId);

public sealed record OrderRequest(string OrderId, decimal Amount);
public sealed record OrderValidation(string OrderId, bool Approved);
public sealed record OrderOutcome(string OrderId, string Status, string? Reason = null);
