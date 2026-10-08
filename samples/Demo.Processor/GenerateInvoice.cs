using WorkThrough;

namespace Demo.Processor;

// The composition root assigns the identity. Moving this class never changes the saved address.
[ManualProcessorRegistration]
public sealed class GenerateInvoice : IProcessor<InvoiceInput>
{
    public async Task<JobResult> ProcessAsync(InvoiceInput invoice, JobExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invoice.InvoiceId))
        {
            return JobResult.Reject("An invoice ID is required.");
        }

        await context.ReportProgressAsync("Invoice input validated", cancellationToken);

        // Supply context.IdempotencyKey to a real invoice service to deduplicate external effects.

        return JobResult.From(new InvoiceOutput(invoice.InvoiceId, Generated: true));
    }
}
