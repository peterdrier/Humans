namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// Aggregated outcome of a bulk import call (or chain of chunked calls) that
/// creates-and-assigns subscribers to a single MailerLite group.
/// </summary>
public sealed record BulkImportResult(
    int Created,
    int Updated,
    int Duplicates,
    int Errors);
