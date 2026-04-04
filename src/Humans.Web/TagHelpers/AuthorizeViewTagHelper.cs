using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Web.TagHelpers;

/// <summary>
/// Attribute-based TagHelper that conditionally renders an element based on
/// a named authorization policy. Suppresses the element when the current user
/// does not satisfy the policy.
///
/// Delegates to <see cref="IAuthorizationService"/> so that the same registered
/// ASP.NET Core policies used by controllers are evaluated consistently in views.
///
/// Unknown policy names fail closed (element hidden) and log a warning.
///
/// Usage:
///   &lt;li authorize-policy="AdminOnly"&gt;Only admins see this&lt;/li&gt;
///   &lt;div authorize-policy="ReviewQueueAccess"&gt;...&lt;/div&gt;
/// </summary>
[HtmlTargetElement("*", Attributes = "authorize-policy")]
public class AuthorizeViewTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<AuthorizeViewTagHelper> _logger;

    public AuthorizeViewTagHelper(
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationService authorizationService,
        ILogger<AuthorizeViewTagHelper> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    /// <summary>
    /// Named policy to evaluate. Must match a registered ASP.NET Core authorization policy.
    /// </summary>
    [HtmlAttributeName("authorize-policy")]
    public string Policy { get; set; } = "";

    /// <summary>
    /// Run before other TagHelpers to avoid unnecessary processing of suppressed elements.
    /// </summary>
    public override int Order => -1;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            output.SuppressOutput();
            return;
        }

        if (string.IsNullOrEmpty(Policy))
        {
            output.SuppressOutput();
            return;
        }

        try
        {
            var result = await _authorizationService.AuthorizeAsync(user, Policy);
            if (!result.Succeeded)
            {
                output.SuppressOutput();
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // Unknown policy name — fail closed (hide the element)
            _logger.LogWarning("Unknown authorize-policy \"{PolicyName}\" — element suppressed (fail closed)", Policy);
            output.SuppressOutput();
            return;
        }

        // Remove the attribute from rendered HTML
        output.Attributes.RemoveAll("authorize-policy");
    }
}
