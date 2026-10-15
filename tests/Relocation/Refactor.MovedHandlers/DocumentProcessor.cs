using WorkThrough;

namespace NewCompany.Documents;

[Job("documents.raw")]
public sealed class RelocatedRawProcessor : IJob<string>
{
    public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken)
    {
        using var document = System.Text.Json.JsonDocument.Parse(input);

        return Task.FromResult(JobResult.From(new
        {
            documentId = document.RootElement.GetProperty("documentId").GetString(),
            implementation = "relocated-raw"
        }));
    }
}

[JobContract("documents.generate")]
public sealed record DocumentRequest(string DocumentId);

public sealed class RenamedProcessor : IJob<DocumentRequest>
{
    public Task<JobResult> ExecuteAsync(DocumentRequest job, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new
        {
            job.DocumentId,
            implementation = "moved-and-renamed"
        }));
}

[ManualJobRegistration]
public sealed class RenamedAddressProcessor : IJob<string>
{
    private readonly RelocatedRawProcessor processor = new();
    public Task<JobResult> ExecuteAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
        processor.ExecuteAsync(input, context, cancellationToken);
}
