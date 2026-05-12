namespace Humans.Domain.Entities;

public class ExpenseLine
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public Guid? AttachmentId { get; set; }
    public int SortOrder { get; set; }

    public ExpenseAttachment? Attachment { get; set; }
}
