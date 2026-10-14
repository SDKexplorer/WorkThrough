using WorkThroughWorker;
using WorkThrough;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("WorkThrough")
    ?? throw new InvalidOperationException("Set ConnectionStrings:WorkThrough.");

// Run as many copies of this worker as you need against the same database; they share the work.
builder.Services.AddWorkThrough()
#if (UsePostgreSql)
    .UsePostgreSql(connectionString)
#elif (UseSqlServer)
    .UseSqlServer(connectionString)
#elif (UseMySql)
    .UseMySql(connectionString)
#else
    .UseOracle(connectionString)
#endif
    .Job(Reports.Daily, job => job
        .RunWith<DailyReportJob>()
        .Every("0 6 * * *", new DailyReport(Days: 1))
        .AtMostRunning(1))
    .AddScheduler()
    .AddWorker(options => options.MaxConcurrentJobs = 8);

using var host = builder.Build();
await host.RunAsync();
