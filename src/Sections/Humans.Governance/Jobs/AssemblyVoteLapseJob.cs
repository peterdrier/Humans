using Humans.Governance.Services;
using Microsoft.Extensions.Logging;

namespace Humans.Governance.Jobs;

/// <summary>
/// Hourly sweep for assembly votes: closes votes whose announced time has passed and sends
/// the T-24h reminder to roster members who have not voted.
/// </summary>
/// <remarks>
/// A backstop, not the mechanism: the service closes a lapsed vote inline on the first
/// request that observes the deadline, so correctness never depends on this job having run.
/// What the job adds is the audit entry and the stored result for a vote nobody happened to
/// look at, and the reminder, which has no request to ride along with.
/// <para>
/// Calls the service like every other job in this section — never a repository directly.
/// </para>
/// </remarks>
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
