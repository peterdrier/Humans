namespace Humans.MailerLite.Services.Dtos;

internal sealed record BulkImportResult(
    int Created,
    int Updated,
    int Duplicates,
    int Errors);
