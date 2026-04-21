using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class AgentWidgetViewComponent : ViewComponent
{
    private readonly IAgentSettingsService _settings;
    private readonly IConsentService _consents;
    private readonly UserManager<Humans.Domain.Entities.User> _users;

    public AgentWidgetViewComponent(
        IAgentSettingsService settings,
        IConsentService consents,
        UserManager<Humans.Domain.Entities.User> users)
    {
        _settings = settings;
        _consents = consents;
        _users = users;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        // Phase 1: restrict widget to Admins only. Other users will get it when we expand coverage in Phase 2.
        if (!UserClaimsPrincipal.IsInRole(RoleNames.Admin))
            return Content(string.Empty);

        if (!_settings.Current.Enabled)
            return Content(string.Empty);

        var user = await _users.GetUserAsync(UserClaimsPrincipal);
        if (user is null)
            return Content(string.Empty);

        // GetPendingDocumentNamesAsync returns the display names of documents the user has not yet consented to.
        // We check whether "Agent Chat Terms" is in the pending list.
        var pending = await _consents.GetPendingDocumentNamesAsync(user.Id, HttpContext.RequestAborted);
        var hasConsent = !pending.Any(name => string.Equals(name, "Agent Chat Terms", StringComparison.Ordinal));

        // Render the widget even without consent — the Razor view shows the consent gate UI on first click.
        return View(new AgentWidgetModel(hasConsent));
    }
}

public sealed record AgentWidgetModel(bool HasConsented);
