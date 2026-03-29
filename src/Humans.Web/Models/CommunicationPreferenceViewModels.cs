using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class CommunicationPreferencesViewModel
{
    public List<CategoryPreferenceItem> Categories { get; set; } = [];
}

public class CategoryPreferenceItem
{
    public MessageCategory Category { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool OptedOut { get; set; }
    public bool IsEditable { get; set; }
}
