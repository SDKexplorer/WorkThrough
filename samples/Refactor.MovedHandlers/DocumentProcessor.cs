using WorkThrough;

namespace NewCompany.Documents;

[Processor("documents.raw")]
public sealed class RelocatedRawProcessor : IProcessor<string>
{
    public Task<JobResult> ProcessAsync(string input, JobExecutionContext context, CancellationToken cancellationToken)
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

public sealed class RenamedProcessor : IJobHandler<DocumentRequest>
{
    public Task<JobResult> HandleAsync(DocumentRequest job, JobExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(JobResult.From(new
        {
            job.DocumentId,
            implementation = "moved-and-renamed"
        }));
}

[ManualProcessorRegistration]
public sealed class RenamedAddressProcessor : IProcessor<string>
{
    private readonly RelocatedRawProcessor processor = new();
    public Task<JobResult> ProcessAsync(string input, JobExecutionContext context, CancellationToken cancellationToken) =>
        processor.ProcessAsync(input, context, cancellationToken);
}
