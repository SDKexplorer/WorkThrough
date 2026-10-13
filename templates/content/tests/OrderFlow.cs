using WorkThrough;

namespace WorkThroughTests;

// An example flow so the tests run as generated. In your solution these definitions live in a shared project; reference it and
// delete this file.
public sealed record PlaceOrder(string OrderId, decimal Amount);
public sealed record Charge(string OrderId, decimal Amount);
public sealed record Receipt(string OrderId, string PaymentId);
public sealed record Ship(string OrderId, string PaymentId);
public sealed record Apologise(string OrderId);

public static class OrderJobs
{
    public static JobDefinition<Charge> Charge { get; } = new("orders.charge");
    public static JobDefinition<Ship> Ship { get; } = new("orders.ship");
    public static JobDefinition<Apologise> Apologise { get; } = new("orders.apologise");
}

public static class OrderFlow
{
    // Charge, then ship with the payment's ID. If the charge fails, apologise instead.
    public static FlowDefinition Definition { get; } = new FlowBuilder("orders.place", 1)
        .Step("charge", OrderJobs.Charge.Identity)
            .BindInput((Charge charge) => charge.OrderId, (PlaceOrder order) => order.OrderId)
            .BindInput((Charge charge) => charge.Amount, (PlaceOrder order) => order.Amount)
        .Step("ship", OrderJobs.Ship.Identity).After("charge")
            .BindInput((Ship ship) => ship.OrderId, (PlaceOrder order) => order.OrderId)
            .BindInput((Ship ship) => ship.PaymentId, (Receipt receipt) => receipt.PaymentId, "charge")
        .Step("apologise", OrderJobs.Apologise.Identity).OnFailureOf("charge")
            .BindInput((Apologise apology) => apology.OrderId, (PlaceOrder order) => order.OrderId)
        .Build();
}
