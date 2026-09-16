using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Backdoor.Contracts;
using Humans.Users.Contracts;

namespace Humans.Web.Authorization;

/// <summary>
/// Global filter routing authenticated users by their stored <see cref="UserState"/>:
/// only <see cref="UserState.Active"/> reaches the app. <see cref="UserState.Bare"/> → name entry;
/// <see cref="UserState.DeletePending"/> → the cancel-deletion screen; Suspended/AdminSuspended/
/// Rejected/Deleted/Merged → the account-status wall. Exempt controllers/actions are public/self-gated
/// pages, the onboarding surface, and the redirect targets themselves (so non-Active users can reach their landing).
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    // Only controllers a non-Active user must still reach. Public controllers use
    // [AllowAnonymous]/API keys; role-gated app controllers are still blocked until Active.
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account",          // Login/logout/OAuth
        "OnboardingWidget", // Guided onboarding (name entry) — the Bare landing target
        "Consent",          // Sign required legal documents (onboarding surface)
        "User",             // Account-status wall + cancel-deletion landing (redirect targets)
        "Language",         // Language switching
        "Guest",            // Profileless account dashboard
        "GovernanceApplications", // Tier application submission — any logged-in user
        "Issues",           // In-app issue reporting — any logged-in user. Inherited from the
                            // Feedback exemption when Issues replaced Feedback wholesale (#977):
                            // someone stuck mid-onboarding or on the status wall still sees the
                            // Help widget, and reporting that they are stuck must reach the queue.
        "Notifications",    // Notification inbox — any logged-in user
        "Survey",           // Tokenised survey answering — invited non-Active users must still reach it ([AllowAnonymous])
    };

    // Live accounts retain own-profile maintenance outside Active membership. Other humans'
    // profiles, messaging, search and admin email actions still require Active membership.
    // Deleted/Merged accounts are handled before these recovery exemptions.
    // Public picture/popover and email-verification actions use [AllowAnonymous].
    private static readonly HashSet<(string Controller, string Action)> ExemptActions =
    [
        ("Profile", "Index"),
        ("Profile", "Me"),
        ("Profile", "Edit"),
        ("Profile", "DeclareNotAttending"),
        ("Profile", "UndoNotAttending"),
        ("Profile", "MyOutbox"),
        ("Profile", "Privacy"),
        ("Profile", "RequestDeletion"),
        ("Profile", "DietaryMedical"),
        ("Profile", "CommunicationPreferences"),
        ("Profile", "UpdatePreference"),
        ("Profile", "Notifications"),
        ("Profile", "DownloadData"),
        ("ProfileEmails", "Emails"),
        ("ProfileEmails", "AddEmail"),
        ("ProfileEmails", "SetPrimary"),
        ("ProfileEmails", "SetEmailVisibility"),
        ("ProfileEmails", "DeleteEmail"),
        ("ProfileEmails", "SetGoogle"),
        ("ProfileEmails", "ClearGoogle"),
        ("ProfileEmails", "ClearPrimary"),
        ("ProfileEmails", "Link"),
        ("ProfileEmails", "Unlink"),
        ("ProfileEmails", "UnlinkLinkedAccount"),
    ];

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return next();
        }

        // A Backdoor API key authenticates a machine, not a browsing session
        // (nobodies-collective/Humans#1128): there is no onboarding page to send a JSON
        // client to, and the key's owner never passes through claims transformation, so the
        // state claim this gate reads is absent by construction.
        if (IsMachineRequest(user))
        {
            return next();
        }

        if (context.ActionDescriptor is ControllerActionDescriptor cad &&
            (cad.MethodInfo.IsDefined(typeof(AllowAnonymousAttribute), true) ||
             cad.ControllerTypeInfo.IsDefined(typeof(AllowAnonymousAttribute), true)))
        {
            return next();
        }

        // A lingering cookie must not turn an anonymized account into an editable profile.
        // Terminal accounts retain the status wall and session/language routes, but none
        // of the onboarding or self-service exemptions for recoverable accounts below.
        var state = RoleAssignmentClaimsTransformation.GetUserState(user);
        if (state is UserState.Deleted or UserState.Merged)
        {
            if (context.ActionDescriptor is ControllerActionDescriptor terminalAction
                && (terminalAction.ControllerName is "Account" or "Language"
                    || terminalAction is { ControllerName: "User", ActionName: "Status" }))
            {
                return next();
            }

            context.Result = new RedirectToActionResult("Status", "User", null);
            return Task.CompletedTask;
        }

        if (context.Controller is Controller controller)
        {
            var descriptor = controller.ControllerContext.ActionDescriptor;
            if (ExemptControllers.Contains(descriptor.ControllerName) ||
                ExemptActions.Contains((descriptor.ControllerName, descriptor.ActionName)))
            {
                return next();
            }
        }

        // Access is the stored UserState (stamped on the principal by
        // RoleAssignmentClaimsTransformation). Only Active reaches the app.
        if (state == UserState.Active)
        {
            return next();
        }

        context.Result = state switch
        {
            UserState.DeletePending => new RedirectToActionResult("Deletion", "User", null),
            UserState.Suspended or UserState.AdminSuspended
                or UserState.Rejected
                => new RedirectToActionResult("Status", "User", null),
            // Bare or null (not yet named / unseeded) → name entry.
            _ => new RedirectToActionResult("Index", "OnboardingWidget", null),
        };
        return Task.CompletedTask;
    }

    /// <summary>True when the principal came from a Backdoor API key rather than a sign-in cookie.</summary>
    internal static bool IsMachineRequest(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, BackdoorAuthentication.SchemeName, StringComparison.Ordinal);
}
