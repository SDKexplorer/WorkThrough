using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace WorkThrough.Exercises;

internal sealed class InvestigationReaders(DateTimeOffset historyFrom, long retainedAttempts)
{
    private readonly ConcurrentDictionary<string, LatencyHistogram> queries = new();
    private readonly ConcurrentDictionary<string, long> failures = new();
    private long responseMaximum;
    private long redactionViolations;

    internal long Failures => failures.Values.Sum();
    internal IReadOnlyDictionary<string, long> FailureTypes => failures;
    internal long RedactionViolations => Interlocked.Read(ref redactionViolations);
    internal long MaximumResponseBytes => Interlocked.Read(ref responseMaximum);
    internal IReadOnlyDictionary<string, object> Queries => queries.OrderBy(pair => pair.Key)
        .ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot());

    internal async Task ReadAsync(InvestigationClient client, int index, CancellationToken ct)
    {
        var tools = new[] { "workthrough_search_executions", "workthrough_get_execution", "workthrough_get_attempts", "workthrough_get_metrics", "workthrough_get_capacity", "workthrough_get_retention" };
        var next = index;
        while (!ct.IsCancellationRequested)
        {
            var tool = tools[next++ % tools.Length];
            var arguments = tool switch
            {
                "workthrough_search_executions" => new Dictionary<string, object?> { ["query"] = new { processorKey = RetainedHistoryFixture.ProcessorKey, correlationId = $"fixture:{next % 16}", limit = 25 } },
                "workthrough_get_execution" or "workthrough_get_attempts" or "workthrough_get_capacity" => new Dictionary<string, object?> { ["id"] = RetainedHistoryFixture.FirstId },
                "workthrough_get_metrics" => new Dictionary<string, object?> { ["query"] = new { from = historyFrom } },
                _ => new Dictionary<string, object?>()
            };
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await client.Client.CallToolAsync(tool, arguments, cancellationToken: ct);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                if (System.Text.Encoding.UTF8.GetString(bytes).Contains(RetainedHistoryFixture.Secret, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref redactionViolations);
                }

                UpdateMaximum(ref responseMaximum, bytes.Length);
                if (response.IsError == true)
                {
                    failures.AddOrUpdate(tool + ":tool-error", 1, (_, count) => count + 1);
                }
                else if (tool == "workthrough_get_metrics")
                {
                    PerformanceSuite.Ensure(response.StructuredContent!.Value.GetProperty("finishedAttempts").GetInt64() >= retainedAttempts,
                        "The MCP metrics response excluded retained attempts.");
                }
                else if (tool == "workthrough_search_executions")
                {
                    PerformanceSuite.Ensure(response.StructuredContent!.Value.GetProperty("items").GetArrayLength() > 0,
                        "The MCP search response did not find retained executions.");
                }

                queries.GetOrAdd(tool, _ => new()).Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                failures.AddOrUpdate(tool + ":" + error.GetType().Name, 1, (_, count) => count + 1);
                queries.GetOrAdd(tool, _ => new()).Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        var current = Interlocked.Read(ref maximum);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
