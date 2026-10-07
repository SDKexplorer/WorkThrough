using WorkThrough;

namespace Demo.Contracts;

[JobContract("orders.validate")]
public sealed record ValidateOrder(string OrderId, decimal Amount);

[JobContract("orders.dispatch")]
public sealed record DispatchOrder(string OrderId);

[JobContract("orders.reject")]
public sealed record RejectOrder(string OrderId);
