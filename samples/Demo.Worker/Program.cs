using WorkThrough;
using WorkThrough.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkThrough()
    .UseSqlite(builder.Configuration["Jobs:ConnectionString"] ?? "Data Source=durablejobs-demo.db")
    .AddGeneratedJobs(typeof(Program).Assembly)
    .SeedClusterLimits(new(null, 12), new("orders.dispatch", 2))
    .AddWorker(options => { options.MaxConcurrentJobs = 4; options.Queues = ["default", "shipping"]; });
await builder.Build().RunAsync();
