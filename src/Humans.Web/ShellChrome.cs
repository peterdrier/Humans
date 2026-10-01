using Humans.Base.Interfaces;
using Humans.Web.ViewComponents;

namespace Humans.Web;

/// <summary>
/// Shell's own chrome contribution — the sign-in method dial on the admin dashboard.
/// Registered by name in <c>Program.cs</c>: section discovery walks section assemblies only.
/// </summary>
internal sealed class ShellChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(LoginMethodCardViewComponent), Weight: 50)];
}
