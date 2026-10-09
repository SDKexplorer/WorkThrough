using System.Diagnostics;

namespace WorkThrough.Exercises;

internal static class RelocationSuite
{
    internal static async Task RunAsync(string provider, string before, string after)
    {
        await using var database = await ExerciseDatabase.CreateAsync(provider);
        var receipt = Path.Combine(database.DirectoryPath, "receipt.json");
        var runtime8 = Environment.GetEnvironmentVariable("WORKTHROUGH_DOTNET8") ?? "dotnet";
        await RunAsync(runtime8, [before, database.ConnectionString, receipt], provider);
        File.Move(before, before + ".removed");
        try
        {
            await RunAsync("dotnet", ["--roll-forward", "LatestMajor", after, database.ConnectionString, receipt], provider);
        }
        finally
        {
            File.Move(before + ".removed", before);
        }

        Console.WriteLine($"PASS {provider}: separately published processor relocation and compatibility aliases.");
    }

    private static async Task RunAsync(string executable, string[] arguments, string provider)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["WT_REFACTOR_PROVIDER"] = provider;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the published proof.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The {provider} published proof failed with exit code {process.ExitCode}.");
        }
    }
}
