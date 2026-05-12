using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Humans.Application.Architecture;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Identity;

/// <summary>
/// Issue #635 (§15i, Phase 6 alt): a logging-only decorator over the EF
/// <see cref="UserStore{TUser,TRole,TContext,TKey}"/>. Subclasses for
/// minimal surface (Identity exposes ~30 methods across 8+ store
/// interfaces; subclassing inherits all of them and lets us override only
/// the two lookup paths we want to observe). Behavior is unchanged — every
/// override delegates to <c>base</c>; the only side effect is a warning log
/// on each <see cref="FindByEmailAsync"/> / <see cref="FindByNameAsync"/>
/// hit so we can answer "does Identity itself ever call these?" with data
/// from production traffic.
/// </summary>
/// <remarks>
/// <para>
/// The existing <c>IdentityFindByEmailRestrictionsTests</c> arch test
/// already pins <i>application code</i> away from <see cref="UserManager{TUser}.FindByEmailAsync"/>
/// and <see cref="UserManager{TUser}.FindByNameAsync"/>. What that test
/// can't assert is whether Identity's own internal flows (claims principal
/// hydration, password-reset / email-confirmation token handlers, security
/// stamp validators) end up calling the store's lookups under the hood.
/// </para>
/// <para>
/// This decorator surfaces those calls. If, after some weeks of soak in
/// production, the warning never fires, the arch test's bet ("custom
/// HumansUserStore is dead weight") is verified — and we can drop this
/// decorator. If it DOES fire, the log line tells us exactly which app
/// path triggered Identity into making the call (via the
/// <see cref="ResolveAppCaller"/> stack-walk) so we can ship the spec's
/// real lookup-rerouting store at that point with confidence.
/// </para>
/// <para>
/// Per Peter (issue #635 conversation): logs are safe — emails / names are
/// emitted raw, not obfuscated.
/// </para>
/// </remarks>
[Grandfathered(
    ruleId: "HUM0009",
    justification: "ASP.NET Identity's UserStore<TUser,TRole,TContext,TKey> base class requires HumansDbContext as a type argument; this is an Identity-owned shape, not a service. Resolve when the lookup-rerouting store from issue #635 ships and this decorator can be dropped.",
    since: "2026-05-12",
    issueRef: "nobodies-collective/Humans#701")]
public sealed class LoggingUserStoreDecorator
    : UserStore<User, IdentityRole<Guid>, HumansDbContext, Guid>
{
    private readonly ILogger<LoggingUserStoreDecorator> _logger;

    public LoggingUserStoreDecorator(
        HumansDbContext context,
        ILogger<LoggingUserStoreDecorator> logger,
        IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        _logger = logger;
    }

    public override Task<User?> FindByEmailAsync(
        string normalizedEmail, CancellationToken cancellationToken = default)
    {
        // IsEnabled guard avoids the StackTrace allocation in ResolveAppCaller
        // when Warning is filtered out (e.g. log-level tightening in some envs).
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Identity.FindByEmailAsync called for {Email} from {Caller}",
                normalizedEmail, ResolveAppCaller());
        }
        return base.FindByEmailAsync(normalizedEmail, cancellationToken);
    }

    public override Task<User?> FindByNameAsync(
        string normalizedUserName, CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Identity.FindByNameAsync called for {Name} from {Caller}",
                normalizedUserName, ResolveAppCaller());
        }
        return base.FindByNameAsync(normalizedUserName, cancellationToken);
    }

    /// <summary>
    /// Walks the managed stack and returns the first frame whose declaring
    /// type lives in a Humans-owned assembly. Format: <c>Type.Method</c>.
    /// Falls back to the top-5 frames joined with <c>" ← "</c> when no
    /// Humans-frame is found (the call originated entirely inside Identity
    /// or framework code). [CallerMemberName] won't help here because the
    /// caller is Identity framework code, not application code; what we
    /// want is which Humans path triggered it.
    /// </summary>
    internal static string ResolveAppCaller()
    {
        try
        {
            var stack = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
            var frames = stack.GetFrames();

            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                var declaringType = method?.DeclaringType;
                var assemblyName = declaringType?.Assembly.GetName().Name;
                if (assemblyName is not null &&
                    assemblyName.StartsWith("Humans.", StringComparison.Ordinal) &&
                    !string.Equals(assemblyName, "Humans.Infrastructure", StringComparison.Ordinal))
                {
                    return $"{declaringType!.Name}.{method!.Name}";
                }
            }

            // No Humans-app frame in the stack — fall back to a short top-5
            // summary so we have *some* signal even when Identity invoked
            // itself entirely from internal code.
            return string.Join(" ← ", frames
                .Take(5)
                .Select(f =>
                {
                    var m = f.GetMethod();
                    var t = m?.DeclaringType?.Name ?? "?";
                    return $"{t}.{m?.Name ?? "?"}";
                }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"(stack-walk failed: {ex.GetType().Name})";
        }
    }
}
