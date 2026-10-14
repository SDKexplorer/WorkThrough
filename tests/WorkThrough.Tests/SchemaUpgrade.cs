using System.Collections;
using System.Reflection;

namespace WorkThrough.Tests;

/// <summary>Returns a migrated store to the exact state a schema-10 installation left: its steps, fingerprint and no calendar table.</summary>
internal static class SchemaUpgrade
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    public static async Task RewindAsync(StoreHarness harness, Type store, params string[] schema10Steps)
    {
        var steps = schema10Steps.SelectMany(name => ((IEnumerable)store.GetField(name, Private)!.GetValue(null)!).Cast<object>()).ToArray();
        var stepType = steps[0].GetType();
        var typed = Array.CreateInstance(stepType, steps.Length);
        Array.Copy(steps, typed, steps.Length);
        var fingerprint = (string)store.GetMethod("Fingerprint", Private)!.Invoke(null, [typed])!;

        await harness.ExecuteAsync("DROP TABLE dj_calendars");
        await harness.ExecuteAsync("UPDATE dj_schema SET version=10,step=@step,definition_hash=@hash", ("step", steps.Length), ("hash", fingerprint));
    }

    public static AdministrativeCommand Command() => new(Guid.NewGuid(), new("tests", "operator"), "Upgrade check");
}
