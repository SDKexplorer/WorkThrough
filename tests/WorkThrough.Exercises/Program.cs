using WorkThrough.Exercises;

if (args.FirstOrDefault() == "worker")
{
    await Workload.RunWorkerAsync();

    return;
}

if (args.Length < 2 || args[1] is not ("sqlite" or "postgres" or "sqlserver" or "oracle"))
{
    Console.Error.WriteLine("Usage: WorkThrough.Exercises <reliability|performance> <sqlite|postgres|sqlserver|oracle> [output.json] [--full]");
    Environment.ExitCode = 2;

    return;
}

if (args[0] == "relocation" && args.Length == 4)
{
    await RelocationSuite.RunAsync(args[1], args[2], args[3]);
}
else if (args[0] == "reliability")
{
    await ReliabilitySuite.RunAsync(args[1]);
}
else if (args[0] == "performance")
{
    await PerformanceSuite.RunAsync(args[1], args.ElementAtOrDefault(2) ?? "performance.json", args.Contains("--full"));
}
else
{
    throw new ArgumentException("Unknown exercise.");
}
