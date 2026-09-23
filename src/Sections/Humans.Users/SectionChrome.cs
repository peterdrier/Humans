using Humans.Base.Interfaces;
using Humans.Users.ViewComponents;

namespace Humans.Users;

/// <summary>Layout chrome contribution — the preferred-language card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(PreferredLanguageCardViewComponent), Weight: 40),
            new(ChromeSlots.WidgetGallery, typeof(UsersGalleryViewComponent)),
        ];
}
