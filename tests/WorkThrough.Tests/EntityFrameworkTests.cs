using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace WorkThrough.Tests;

public sealed class ShopOrder
{
    public required string Id { get; init; }
    public required string Customer { get; set; }
}

public sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    public DbSet<ShopOrder> Orders => Set<ShopOrder>();

    protected override void OnModelCreating(ModelBuilder model) => model.Entity<ShopOrder>(order =>
    {
        order.ToTable("ef_orders");
        order.Property(o => o.Id).HasMaxLength(36);
        order.Property(o => o.Customer).HasMaxLength(200);
    });
}

/// <summary>Jobs committed with EF Core SaveChanges, in the DbContext's own transaction.</summary>
public sealed class EntityFrameworkTests
{
    public static IEnumerable<object[]> Providers() => StorageConformanceTests.Providers();

    private static readonly JobDefinition<Alpha> Confirm = new("tests.ef.confirm");

    private static async Task<OrdersContext> ContextAsync(StoreHarness harness, string provider)
    {
        var options = new DbContextOptionsBuilder<OrdersContext>();
        _ = provider switch
        {
            "sqlite" => options.UseSqlite(harness.ConnectionString),
            "postgres" => options.UseNpgsql(harness.ConnectionString),
            "sqlserver" or StoreHarness.SqlServerVersionedReads => options.UseSqlServer(harness.ConnectionString),
            "oracle" => options.UseOracle(harness.ConnectionString),
            _ => options.UseMySql(harness.ConnectionString, ServerVersion.AutoDetect(harness.ConnectionString))
        };
        var context = new OrdersContext(options.UseWorkThrough(harness.Store).Options);
        try
        {
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        catch (Exception error) when (error is System.Data.Common.DbException)
        {
            // Another context in this test already created the table.
        }

        return context;
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task SaveChanges_commits_the_entities_and_their_jobs_together(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using var context = await ContextAsync(harness, provider);

        context.Orders.Add(new() { Id = "order-1", Customer = "ada" });
        var job = context.Enqueue(Confirm, new Alpha("order-1"), Confirm.DefaultOptions with { IdempotencyKey = "confirm:order-1" });
        Assert.Null(job.JobId);
        await context.SaveChangesAsync();

        var stored = (await harness.Store.GetAsync(job.JobId!.Value))!;
        Assert.Equal(("order-1", JobStatus.Queued), (stored.Payload.GetProperty("value").GetString(), stored.Status));
        Assert.Equal(1, await context.Orders.CountAsync());

        var again = context.Enqueue(Confirm, new Alpha("order-1"), Confirm.DefaultOptions with { IdempotencyKey = "confirm:order-1" });
        await context.SaveChangesAsync();
        Assert.Equal(job.JobId, again.JobId);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task A_failed_save_or_a_rolled_back_transaction_leaves_no_job(string provider)
    {
        await using var harness = await StoreHarness.CreateAsync(provider);
        await using (var first = await ContextAsync(harness, provider))
        {
            first.Orders.Add(new() { Id = "order-1", Customer = "ada" });
            await first.SaveChangesAsync();
        }

        await using (var duplicate = await ContextAsync(harness, provider))
        {
            duplicate.Orders.Add(new() { Id = "order-1", Customer = "grace" });
            duplicate.Enqueue(Confirm, new Alpha("duplicate"));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        await using (var rolledBack = await ContextAsync(harness, provider))
        {
            await using var transaction = await rolledBack.Database.BeginTransactionAsync();
            rolledBack.Orders.Add(new() { Id = "order-2", Customer = "alan" });
            rolledBack.Enqueue(Confirm, new Alpha("rolled back"));
            await rolledBack.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.Empty(await harness.Store.ListAsync(new()));
        await using var check = await ContextAsync(harness, provider);
        Assert.Equal(["order-1"], await check.Orders.Select(order => order.Id).ToListAsync());
    }

    [Fact]
    public async Task A_context_without_WorkThrough_refuses_to_record_jobs()
    {
        await using var harness = await StoreHarness.CreateAsync("sqlite");
        await using var context = new OrdersContext(new DbContextOptionsBuilder<OrdersContext>().UseSqlite(harness.ConnectionString).Options);

        Assert.Throws<InvalidOperationException>(() => context.Enqueue(Confirm, new Alpha("x")));
    }
}
