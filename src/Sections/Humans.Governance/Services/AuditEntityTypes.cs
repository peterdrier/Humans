namespace Humans.Governance.Services;

/// <summary>
/// Persisted audit discriminators, as literals so a CLR rename is not a silent schema change.
/// </summary>
internal static class AuditEntityTypes
{
    /// <summary>Matches the historical <c>nameof(Humans.Domain.Entities.Application)</c>.</summary>
    internal const string Application = "Application";

    internal const string AssemblyVote = "AssemblyVote";

    /// <summary>
    /// Not written: ballot audit entries are keyed to the vote, never the ballot, so erasure
    /// leaves no join from a person to their choice.
    /// </summary>
    internal const string AssemblyBallot = "AssemblyBallot";
}
