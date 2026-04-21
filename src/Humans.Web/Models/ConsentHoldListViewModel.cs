namespace Humans.Web.Models;

public class ConsentHoldListViewModel
{
    public List<ConsentHoldListEntryRow> Entries { get; set; } = new();
}

public class ConsentHoldListEntryRow
{
    public int Id { get; set; }
    public string Entry { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime AddedAt { get; set; }
    public Guid AddedByUserId { get; set; }
}
