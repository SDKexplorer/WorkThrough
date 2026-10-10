namespace WorkThrough;

/// <summary>Host-owned redaction, applied after authorized diagnostic reads and before serialization or access logging.</summary>
/// <remarks>Worker DescribeFailure redaction still applies at persistence. This callback never receives job input/output.</remarks>
public interface IMcpDiagnosticRedactor
{
    JobFailure Redact(JobFailure failure);
}

internal sealed class StoredDiagnosticRedactor : IMcpDiagnosticRedactor
{
    public JobFailure Redact(JobFailure failure) => failure;
}
