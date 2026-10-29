using Humans.Base.Interfaces;
using Humans.Tour.ViewComponents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tour;

/// <summary>
/// <c>Register</c> is empty, but the type must stay: implementing <c>ISection</c> is what makes this assembly a
/// section.
/// </summary>
public sealed class Section : ISection, ISectionMemberDashboard, ISectionNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(TourCardViewComponent), Weight: 100)
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
    [
        new(
            "Tour",
            Controller: "Tour",
            Action: "Index",
            Weight: 1000,
            Visible: (_, user) => user.Identity?.IsAuthenticated != true)
    ];
}
