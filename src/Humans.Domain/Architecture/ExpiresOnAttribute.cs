namespace Humans.Domain.Architecture;

/// <summary>
/// Marks a symbol as having a hard removal deadline. The
/// <c>ExpiresOnAnalyzer</c> emits diagnostics that escalate from warning to
/// error as the date passes, so a deadline cannot silently slip past.
/// </summary>
/// <remarks>
/// <para>Two diagnostics derive from this attribute:</para>
/// <list type="bullet">
///   <item>
///     <b>HUM0010 — Usage site.</b> Every reference to the decorated symbol
///     (call, property/field/event/method-group access, or constructor)
///     fires a <b>warning</b> before the date and an <b>error</b> on/after
///     the date. Callers must migrate before the deadline.
///   </item>
///   <item>
///     <b>HUM0011 — Declaration site.</b> The decorated symbol itself is
///     clean until the date, then a <b>warning</b> during the <see cref="GraceDays"/>
///     window (default 7 days), then an <b>error</b>. The grace period
///     gives the author time to actually delete the symbol after the callers
///     have all migrated.
///   </item>
/// </list>
///
/// <para>
/// Use this to set a real deadline on deprecated API surface — typically two
/// weeks out — rather than letting <c>[Obsolete]</c> warnings accumulate
/// indefinitely.
/// </para>
///
/// <para>"Today" is the build machine's UTC date. A clean CI build on the
/// deadline day will flip red without any code change — which is exactly the
/// point of a deadline.</para>
/// </remarks>
/// <example>
/// <code>
/// [ExpiresOn("2026-05-26", reason: "replaced by IUserEmailService.UpdateEmailAsync")]
/// public Task LegacyUpdateEmail(string email) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
public sealed class ExpiresOnAttribute : Attribute
{
    public ExpiresOnAttribute(string date, int graceDays = 7, string? reason = null)
    {
        Date = date;
        GraceDays = graceDays;
        Reason = reason;
    }

    /// <summary>ISO date (<c>yyyy-MM-dd</c>) on which callers start erroring.</summary>
    public string Date { get; }

    /// <summary>
    /// Additional days after <see cref="Date"/> during which the declaration
    /// itself is only a warning. After this window the declaration also errors.
    /// </summary>
    public int GraceDays { get; }

    /// <summary>Optional context shown in the diagnostic message.</summary>
    public string? Reason { get; }
}
