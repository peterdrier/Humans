using AwesomeAssertions;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services.Contributors;
using Humans.Workgroups.Tests.Infrastructure;
using NodaTime;

namespace Humans.Workgroups.Tests.Services.Contributors;

/// <summary>
/// Workgroups' half of Calendar's fan-out (design §8, §20): a member's own meetings on
/// their personal feed (Dormant groups excluded), and every public meeting overlapping the
/// requested window on the community calendar. A soft-deleted meeting appears in neither.
/// </summary>
public sealed class WorkgroupCalendarContributorTests : WorkgroupsTestHarness
{
    private WorkgroupCalendarContributor NewContributor() => new(NewService());

    [HumansFact]
    public async Task PersonalFeed_IncludesMeetingsOfActiveGroupsTheUserBelongsTo()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var member = workgroup.Members.Single().UserId;
        var meeting = await AddMeetingAsync(workgroup.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(1)));

        var items = await NewContributor().GetCalendarItemsForUserAsync(member, Ct);

        items.Should().ContainSingle(i => i.Uid == $"workgroup-meeting-{meeting.Id}@humans.nobodies.team");
    }

    [HumansFact]
    public async Task PersonalFeed_ExcludesMeetingsOfDormantGroups()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var member = workgroup.Members.Single().UserId;
        await AddMeetingAsync(workgroup.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(1)));

        var items = await NewContributor().GetCalendarItemsForUserAsync(member, Ct);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PersonalFeed_ExcludesMeetingsOfGroupsTheUserIsNotAMemberOf()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        await AddMeetingAsync(workgroup.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(1)));
        var outsider = SeedUser("Outsider");

        var items = await NewContributor().GetCalendarItemsForUserAsync(outsider, Ct);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PersonalFeed_ExcludesASoftDeletedMeeting()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var member = workgroup.Members.Single().UserId;
        var now = Clock.GetCurrentInstant();
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(1)), deletedAt: now);

        var items = await NewContributor().GetCalendarItemsForUserAsync(member, Ct);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PublicItems_IncludeOnlyPublicMeetingsOverlappingTheWindow()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var now = Clock.GetCurrentInstant();
        var inWindowPublic = await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(1)), isPublic: true);
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(1)), isPublic: false);
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(30)), isPublic: true);

        var items = await NewContributor().GetPublicItemsForWindowAsync(
            now, now.Plus(Duration.FromDays(7)), Ct);

        items.Should().ContainSingle(i => i.Uid == $"workgroup-meeting-{inWindowPublic.Id}@humans.nobodies.team");
    }

    [HumansFact]
    public async Task PublicItems_ExcludeMeetingsOfDormantGroups()
    {
        var workgroup = await SeedWorkgroupAsync(
            status: WorkgroupStatus.Dormant, dormantReason: WorkgroupDormantReason.Quiet);
        var now = Clock.GetCurrentInstant();
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(1)), isPublic: true);

        var items = await NewContributor().GetPublicItemsForWindowAsync(
            now, now.Plus(Duration.FromDays(7)), Ct);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task PublicItems_ExcludeASoftDeletedMeeting()
    {
        var workgroup = await SeedWorkgroupAsync(status: WorkgroupStatus.Active);
        var now = Clock.GetCurrentInstant();
        await AddMeetingAsync(workgroup.Id, now.Plus(Duration.FromDays(1)), isPublic: true, deletedAt: now);

        var items = await NewContributor().GetPublicItemsForWindowAsync(
            now, now.Plus(Duration.FromDays(7)), Ct);

        items.Should().BeEmpty();
    }
}
