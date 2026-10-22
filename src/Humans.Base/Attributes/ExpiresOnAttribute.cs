namespace Humans.Base.Attributes;

/// <summary>
/// Marks a symbol as having a hard removal deadline. The
/// <c>ExpiresOnAnalyzer</c> emits diagnostics that escalate from warning to
/// error as the date passes, so a deadline cannot silently slip past.
/// </summary>
/// <remarks>
/// <para>HUM0010 (usage site): every reference warns before the date and errors on or after it.</para>
/// <para>
/// HUM0011 (declaration site): clean until the date, a warning during <see cref="GraceDays"/>, then an error, so the
/// author has time to delete the symbol once callers have migrated.
/// </para>
/// <para>"Today" is the build machine's UTC date: a clean CI build flips red on the deadline with no code change.</para>
/// </remarks>
/// <example>
/// <code>
/// [ExpiresOn("2026-05-26", reason: "replaced by IUserEmailService.UpdateEmailAsync")]
/// public Task LegacyUpdateEmail(string email) { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
public sealed class ExpiresOnAttribute(string date, int graceDays = 7, string? reason = null) : Attribute
{
    /// <summary>ISO date (<c>yyyy-MM-dd</c>) on which callers start erroring.</summary>
    public string Date { get; } = date;

    /// <summary>
    /// Additional days after <see cref="Date"/> during which the declaration
    /// itself is only a warning. After this window the declaration also errors.
    /// </summary>
    public int GraceDays { get; } = graceDays;

    /// <summary>Optional context shown in the diagnostic message.</summary>
    public string? Reason { get; } = reason;
}
