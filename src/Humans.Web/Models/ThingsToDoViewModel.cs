namespace Humans.Web.Models;

public class ThingsToDoViewModel
{
    public List<TodoItem> Items { get; set; } = [];
    public int PendingCount => Items.Count(i => !i.IsDone);

    public bool HasAnyItems => Items.Count > 0;
    public bool AllDone => Items.Count > 0 && Items.All(i => i.IsDone);
}

public class TodoItem
{
    public required string Key { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required bool IsDone { get; set; }
    public string? ActionUrl { get; set; }
    public string? ActionText { get; set; }
    public required string IconClass { get; set; }
}
