using AwesomeAssertions;
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Tests.Services.EarlyEntry;

public class CampEarlyEntryProjectionTests
{
    private static readonly LocalDate Ee = new(2026, 7, 7);

    private static CampMember Member(Guid userId, CampMemberStatus status, bool ee) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Status = status, HasEarlyEntry = ee, RequestedAt = Instant.MinValue };

    [HumansFact]
    public void Emits_one_grant_per_active_granted_member_with_camp_name_and_global_date()
    {
        var seasonA = Guid.NewGuid();
        var seasonB = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        var membersBySeason = new Dictionary<Guid, IReadOnlyList<CampMember>>
        {
            [seasonA] = new[] { Member(u1, CampMemberStatus.Active, true) },
            [seasonB] = new[] { Member(u2, CampMemberStatus.Active, true) },
        };
        var seasonNames = new Dictionary<Guid, string> { [seasonA] = "Flaming Lotus", [seasonB] = "Flags" };

        var grants = CampEarlyEntryProjection.Project(Ee, membersBySeason, seasonNames);

        grants.Should().HaveCount(2);
        grants.Should().Contain(g => g.UserId == u1 && g.EntryDate == Ee && g.Source == "Camp: Flaming Lotus");
        grants.Should().Contain(g => g.UserId == u2 && g.EntryDate == Ee && g.Source == "Camp: Flags");
    }

    [HumansFact]
    public void Excludes_non_active_and_non_granted_members()
    {
        var season = Guid.NewGuid();
        var membersBySeason = new Dictionary<Guid, IReadOnlyList<CampMember>>
        {
            [season] = new[]
            {
                Member(Guid.NewGuid(), CampMemberStatus.Pending, true),
                Member(Guid.NewGuid(), CampMemberStatus.Active, false),
            },
        };
        var seasonNames = new Dictionary<Guid, string> { [season] = "Flags" };

        var grants = CampEarlyEntryProjection.Project(Ee, membersBySeason, seasonNames);

        grants.Should().BeEmpty();
    }
}
