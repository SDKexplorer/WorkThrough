using System.Diagnostics;

namespace WorkThrough.Exercises;

/// <summary>Runs the docker CLI for exercises that need containers, networks or database tools.</summary>
internal static class Docker
{
    public static async Task<string> RunAsync(string arguments, string? input = null)
    {
        var start = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments.Split(' ')[0]} failed: {await errors}");
        }

        return await output;
    }
}
