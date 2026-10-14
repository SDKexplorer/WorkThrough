# Message brokers

WorkThrough is not a message bus, but it publishes to one and consumes from one durably. Three packages cover RabbitMQ, Kafka and Azure Service Bus; `WorkThrough.Messaging` holds what they share.

## Publishing

```csharp
builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .UseRabbitMq(new ConnectionFactory { Uri = new(rabbitUri) }, rabbit => rabbit.Publish())
    .AddWorker();

await jobs.Publish("orders", new OrderShipped(order.Id), key: "eu");
```

`Publish` enqueues a publish job. It commits like any job, so it can share a transaction with business data: `InTransactionAsync`, or `db.Enqueue(Messaging.PublishJob, PublishMessage.Of("orders", shipped))` with [EF Core](entity-framework.md). The worker publishes it and retries until the broker confirms, so a broker outage delays messages instead of losing them. The job's ID is the message ID on every retry.

| Broker | Destination | Key | Confirmation |
|---|---|---|---|
| RabbitMQ | Exchange (`""` for the default exchange) | Routing key | Publisher confirms; an unroutable message fails the job (`mandatory`) |
| Kafka | Topic | Message key | Idempotent producer, acknowledged by every in-sync replica; the ID travels in the `message-id` header |
| Azure Service Bus | Queue or topic | Partition key | Accepted by Service Bus; the job ID is the `MessageId`, which duplicate detection uses |

Delivery is at least once: a crash after the broker confirms but before the job records success publishes again with the same message ID.

## Consuming

```csharp
builder.Services.AddWorkThrough()
    .UsePostgreSql(connectionString)
    .UseKafka(kafka => kafka.Consume(new ConsumerConfig { BootstrapServers = servers, GroupId = "billing" }, "orders", Billing.Invoice))
    .AddWorker();
```

Each message becomes a job at the mapped address, with the message body as its input, and the message is acknowledged only after the job has committed. The job then runs with WorkThrough's retries, limits, tenants and console.

- **Deduplication:** the job's idempotency key comes from the message: its message ID on RabbitMQ and Service Bus, the `message-id` header on Kafka, or otherwise the topic, partition and offset. A redelivered message returns the job already stored. RabbitMQ messages without a message ID cannot be deduplicated; set one on producers.
- **Bad messages are kept:** a body that is not JSON is stored as a JSON string, so its job is rejected and shows in the console, where an operator can correct its input. No message is dropped for its content.
- **Failures:** when the job cannot be stored, the message goes back to the broker: RabbitMQ requeues it, Kafka reads the offset again, Service Bus abandons it.
- **Topology:** queues, topics, exchanges and subscriptions must exist. The packages do not declare them.

## What is verified

RabbitMQ 4.1, Kafka 3.9 (KRaft) and the Azure Service Bus emulator: a message published through a job arrives as a job, a message delivered twice with one ID becomes one job, and on RabbitMQ an unroutable publish fails its job. CI runs them beside MySQL and MariaDB. Azure Service Bus itself, as opposed to its emulator, is not yet verified.
