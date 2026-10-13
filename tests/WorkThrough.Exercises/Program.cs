using WorkThrough.Exercises;

if (args.FirstOrDefault() == "worker")
{
    await Workload.RunWorkerAsync();

    return;
}

if (args.Length < 2 || args[1] is not ("sqlite" or "postgres" or "sqlserver" or "oracle"))
{
    Console.Error.WriteLine("Usage: WorkThrough.Exercises <reliability|performance|investigation|admission|soak|throughput|restore|partition> <sqlite|postgres|sqlserver|oracle> [output.json] [--full]");
    Console.Error.WriteLine("Investigation arguments: [output.json] [retainedJobs=100000] [baselineSeconds=60] [investigativeSeconds=120]");
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
else if (args[0] == "investigation")
{
    await InvestigationSuite.RunAsync(args[1], args.ElementAtOrDefault(2) ?? "investigation.json",
        int.Parse(args.ElementAtOrDefault(3) ?? "100000"), int.Parse(args.ElementAtOrDefault(4) ?? "60"),
        int.Parse(args.ElementAtOrDefault(5) ?? "120"));
}
else if (args[0] == "throughput")
{
    await ThroughputSuite.RunAsync(args[1], int.Parse(args.ElementAtOrDefault(2) ?? "5000"), int.Parse(args.ElementAtOrDefault(3) ?? "0"));
}
else if (args[0] == "partition")
{
    await PartitionSuite.RunAsync(args[1]);
}
else if (args[0] == "restore")
{
    await RestoreSuite.RunAsync(args[1]);
}
else if (args[0] == "soak")
{
    await SoakSuite.RunAsync(args[1], int.Parse(args.ElementAtOrDefault(2) ?? "120"), args.ElementAtOrDefault(3) ?? "soak.json");
}
else if (args[0] == "admission")
{
    await AdmissionSuite.RunAsync(args[1], args.ElementAtOrDefault(2) ?? "admission.json");
}
else if (args[0] == "performance")
{
    await PerformanceSuite.RunAsync(args[1], args.ElementAtOrDefault(2) ?? "performance.json", args.Contains("--full"));
}
else
{
    throw new ArgumentException("Unknown exercise.");
}
