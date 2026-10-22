using Humans.Governance.Services;

namespace Humans.Governance.Jobs;

/// <summary>
/// Backstop, not the mechanism: the service closes a lapsed vote on the first request that sees
/// the deadline. The job adds the audit entry and stored result for an unobserved vote, and the
/// T-24h reminder, which has no request to ride along with.
/// </summary>
internal sealed class AssemblyVoteLapseJob(
    IAssemblyVoteService votes,
    ILogger<AssemblyVoteLapseJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var closed = await votes.RunLapseAndReminderSweepAsync(ct);
        logger.LogInformation(
            "Assembly vote sweep finished; closed {Closed} lapsed vote(s).", closed);
    }
}
