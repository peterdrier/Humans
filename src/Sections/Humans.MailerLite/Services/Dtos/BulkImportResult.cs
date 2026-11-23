namespace Humans.MailerLite.Services.Dtos;

internal sealed record BulkImportResult(
    int Created,
    int Errors);
