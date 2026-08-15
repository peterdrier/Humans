using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Humans.Infrastructure.Logging;

/// <summary>
/// Serilog enricher that attaches the current authenticated user's id (<c>UserId</c>)
/// to every log event. Reads the principal from <see cref="IHttpContextAccessor"/>.
/// Out-of-request emissions (background jobs, startup) result in a null <c>UserId</c>
/// so log consumers can clearly attribute events to a specific user — or not.
/// Pattern follows <see cref="PiiRedactionEnricher"/>.
/// </summary>
public sealed class CurrentUserEnricher : ILogEventEnricher
{
    public const string UserIdProperty = "UserId";

    private readonly IHttpContextAccessor? _explicitAccessor;
    private readonly bool _useExplicit;

    /// <summary>
    /// Default constructor used by Serilog's <c>.Enrich.With&lt;T&gt;()</c> activator.
    /// Resolves <see cref="IHttpContextAccessor"/> lazily via <see cref="StaticAccessor"/>
    /// on each enrich call — Serilog instantiates the enricher when the logger is built,
    /// which happens before <c>builder.Build()</c> runs and seeds <see cref="StaticAccessor"/>,
    /// so the static must be read at enrich time, not captured in the ctor.
    /// </summary>
    public CurrentUserEnricher()
    {
        _explicitAccessor = null;
        _useExplicit = false;
    }

    /// <summary>
    /// Test/DI-friendly constructor. Pass <c>null</c> to simulate no <see cref="HttpContext"/>
    /// being available at all (e.g. during background jobs).
    /// </summary>
    public CurrentUserEnricher(IHttpContextAccessor? httpContextAccessor)
    {
        _explicitAccessor = httpContextAccessor;
        _useExplicit = true;
    }

    /// <summary>
    /// Static accessor seeded at startup so the parameterless ctor (used by Serilog's
    /// activator) can find the request <see cref="HttpContext"/>. Set once in <c>Program.cs</c>
    /// after <c>AddHttpContextAccessor</c>; unset only in tests.
    /// </summary>
    public static IHttpContextAccessor? StaticAccessor { get; set; }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var userId = TryGetUserId();
        var value = userId.HasValue ? (object)userId.Value : null!;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(UserIdProperty, value));
    }

    /// <summary>
    /// Reads the <c>UserId</c> property off a <see cref="LogEvent"/>, accepting both
    /// <see cref="Guid"/> and string-form scalars (Serilog's destructurer may pick either
    /// depending on how the value was attached). Centralized so log consumers
    /// (admin views, log API) don't duplicate the extraction.
    /// </summary>
    public static Guid? ExtractFromEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue(UserIdProperty, out var prop))
            return null;

        if (prop is not ScalarValue scalar || scalar.Value is null)
            return null;

        return scalar.Value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }

    private Guid? TryGetUserId()
    {
        var accessor = _useExplicit ? _explicitAccessor : StaticAccessor;
        var httpContext = accessor?.HttpContext;
        if (httpContext is null)
            return null;

        // Reading the principal off ASP.NET's HttpContext (not a domain entity nav).
        // Pragma scoped to silence the cross-domain-nav-reads ratchet, whose textual
        // heuristic otherwise matches this access by property name.
#pragma warning disable CS0618
        var user = httpContext.User;
#pragma warning restore CS0618
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
            return null;

        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null || !Guid.TryParse(claim.Value, out var userId))
            return null;

        return userId;
    }
}
