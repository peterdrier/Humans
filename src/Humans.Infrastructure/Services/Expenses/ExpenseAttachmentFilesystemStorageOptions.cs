namespace Humans.Infrastructure.Services.Expenses;

public sealed class ExpenseAttachmentFilesystemStorageOptions
{
    public const string Section = "ExpenseAttachments";
    public string Root { get; set; } = "/var/lib/humans/expense-attachments";
    public long MaxBytes { get; set; } = 20 * 1024 * 1024; // 20 MB
}
