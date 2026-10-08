using System.Diagnostics;
using System.Reflection;

namespace WorkThrough.Exercises;

internal sealed class WorkerProcess : IDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    public TimeSpan Cpu => process.TotalProcessorTime;
    public long PeakBytes
    {
        get
        {
            process.Refresh();

            return process.PeakWorkingSet64;
        }
    }

    public WorkerProcess(ExerciseDatabase database, bool crashRecovery = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Invoked using dotnet so children use the exact same runtime as the parent.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        start.ArgumentList.Add("worker");
        start.Environment["WT_EXERCISE_CRASH"] = crashRecovery ? "1" : "0";
        start.Environment["WT_EXERCISE_PROVIDER"] = database.Provider;
        start.Environment["WT_EXERCISE_CONNECTION"] = database.ConnectionString;
        start.Environment["WT_EXERCISE_EFFECTS"] = Path.Combine(database.DirectoryPath, "effects.db");
        process = Process.Start(start)!;
        errors = process.StandardError.ReadToEndAsync();
    }

    public async Task ReadyAsync()
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (line != "READY")
        {
            throw new InvalidOperationException("Worker startup failed: " + (await errors.WaitAsync(TimeSpan.FromSeconds(5))));
        }
    }
    public void CheckAlive()
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Worker exited unexpectedly with {process.ExitCode}.");
        }
    }
    public void Kill()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.WaitForExit(10000);
    }
    public void Dispose()
    {
        Kill();
        process.Dispose();
    }
}
