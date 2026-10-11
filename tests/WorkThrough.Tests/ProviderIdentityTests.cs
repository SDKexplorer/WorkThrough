using System.Data.Common;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ProviderIdentityTests
{
    private static readonly JobDefinition<string> Definition = new("tests.identity-boundaries");
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    [Theory, MemberData(nameof(Providers))]
    public async Task Ordinal_unicode_identities_and_nullable_receipts_do_not_collapse(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        if (provider == "oracle")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.EnqueueAsync(Definition, "empty correlation", new(CorrelationId: "")));
            Assert.Empty(await harness.Store.ListAsync(new()));
        }

        var correlation = provider == "oracle" ? "correlation" : "";
        var first = await client.EnqueueAsync(Definition, "ă😀 ", new(Queue: "Queue", CorrelationId: correlation, IdempotencyKey: "café"));
        var second = await client.EnqueueAsync(Definition, "", new(Queue: "queue", CorrelationId: null, IdempotencyKey: "cafe\u0301"));
        var third = await client.EnqueueAsync(Definition, "third", new(IdempotencyKey: "CAFÉ"));
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
        Assert.Equal("ă😀 ", (await harness.Store.GetAsync(first))!.Payload.GetString());
        Assert.Equal(correlation, (await harness.Store.GetAsync(first))!.CorrelationId);
        Assert.Null((await harness.Store.GetAsync(second))!.CorrelationId);
        Assert.Equal("", (await harness.Store.GetAsync(second))!.Payload.GetString());
        Assert.Equal(first, await client.EnqueueAsync(Definition, "ă😀 ", new(Queue: "Queue", CorrelationId: correlation, IdempotencyKey: "café")));
        var lower = (await harness.Store.ClaimAsync(new("worker", [Definition.Identity], ["queue"], TimeSpan.FromMinutes(1))))!;
        Assert.Equal(second, lower.Id);
        Assert.Null(await harness.Store.ClaimAsync(new("worker", [Definition.Identity], ["queue"], TimeSpan.FromMinutes(1))));
        Assert.Equal(first, (await harness.Store.ClaimAsync(new("worker", [Definition.Identity], ["Queue"], TimeSpan.FromMinutes(1))))!.Id);
        Assert.NotEqual(await client.EnqueueAsync(Definition, "no receipt"), await client.EnqueueAsync(Definition, "no receipt"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Trailing_space_identity_is_preserved_or_explicitly_rejected_without_partial_acceptance(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var client = new JobClient(harness.Store);
        var plain = await client.EnqueueAsync(Definition, "plain", new(IdempotencyKey: "receipt"));
        if (provider == "sqlserver")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.EnqueueAsync(Definition, "spaced", new(IdempotencyKey: "receipt ")));
            await Assert.ThrowsAsync<ArgumentException>(() => client.EnqueueAsync(Definition, "spaced", new(CorrelationId: "group ")));
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Store.ListAsync(new(CorrelationId: "group ")));
            await Assert.ThrowsAnyAsync<DbException>(() => client.EnqueueAsync(Definition, "oversized", new(Queue: new string('q', 513), IdempotencyKey: "oversized")));
            Assert.Single(await harness.Store.ListAsync(new()));
            Assert.NotEqual(plain, await client.EnqueueAsync(Definition, "repaired", new(IdempotencyKey: "oversized")));
        }
        else
        {
            Assert.NotEqual(plain, await client.EnqueueAsync(Definition, "spaced", new(IdempotencyKey: "receipt ")));
        }

        var queue = new string('q', 512);
        var id = await client.EnqueueAsync(Definition, "boundary", new(Queue: queue));
        Assert.Equal(id, (await harness.Store.ClaimAsync(new("worker", [Definition.Identity], [queue], TimeSpan.FromMinutes(1))))!.Id);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Unknown_schema_is_rejected_and_reinitializing_keeps_the_store_identity(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        var identity = (await harness.Store.GetExecutionContextAsync()).StoreId;
        await harness.NewStore().InitializeAsync();
        Assert.Equal(identity, (await harness.Store.GetExecutionContextAsync()).StoreId);
        await using var connection = harness.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dj_schema(version) VALUES (999)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "UPDATE dj_compatibility SET reader_minimum=999";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.NewStore().InitializeAsync());
    }
}
