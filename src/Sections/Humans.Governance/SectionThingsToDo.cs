using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Governance;

/// <summary>
/// Governance's entries on the member dashboard's things-to-do list: the required consents
/// the membership snapshot says are still outstanding (no required documents, no entry), and
/// one entry per open assembly vote the member is on the roster for.
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var snapshot = await services.GetRequiredService<IMembershipCalculatorRead>()
            .GetMembershipSnapshotAsync(userId);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var complete = snapshot.PendingConsentCount == 0;

        List<ThingsToDoEntry> entries = [];

        if (snapshot.RequiredConsentCount > 0)
        {
            entries.Add(new ThingsToDoEntry("consents", localizer["Todo_Consents_Title"].Value,
                "fa-solid fa-file-signature", Controller: "Consent", Action: "Index", Weight: 20)
            {
                Description = complete
                    ? localizer["Todo_Consents_Done"].Value
                    : string.Format(CultureInfo.CurrentCulture, localizer["Todo_Consents_Pending"].Value,
                        snapshot.PendingConsentCount, snapshot.RequiredConsentCount),
                IsDone = complete,
                ActionText = localizer["Todo_Consents_Action"].Value,
            });
        }

        entries.AddRange(await VoteEntriesAsync(services, userId, localizer));
        return entries;
    }

    /// <summary>
    /// One entry per open vote the member may cast in, done once they have a ballot on
    /// record. Non-roster members get nothing here: they can see the vote, but there is
    /// nothing for them to do.
    /// </summary>
    private static async ValueTask<IEnumerable<ThingsToDoEntry>> VoteEntriesAsync(
        IServiceProvider services, Guid userId, IStringLocalizer<SharedResource> localizer)
    {
        var votes = await services.GetRequiredService<IAssemblyVoteService>()
            .GetVotesForMemberAsync(userId);

        return votes
            .Where(v => v.Status == AssemblyVoteStatus.Open && v.IsOnRoster)
            .Select(v => new ThingsToDoEntry(
                $"assembly-vote-{v.Id}",
                localizer["Todo_AssemblyVote_Title"].Value,
                "fa-solid fa-check-to-slot",
                Controller: "GovernanceVotes", Action: "Index", Weight: 15)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer[v.HasBallot ? "Todo_AssemblyVote_Done" : "Todo_AssemblyVote_Pending"].Value,
                    v.Title),
                IsDone = v.HasBallot,
                ActionText = localizer["Todo_AssemblyVote_Action"].Value,
            })
            .ToList();
    }
}
