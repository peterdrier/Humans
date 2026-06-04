using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Camps;

public sealed class ShiftObligationServiceTests : ServiceTestHarness
{
    private readonly ShiftObligationService _sut;
    private readonly ICampServiceRead _campServiceRead;
    private readonly IShiftServiceRead _shiftServiceRead;
    private readonly ITeamServiceRead _teamServiceRead;
    private readonly IUserService _userService;
    private readonly ShiftObligationRepository _obligationRepo;
    private readonly CampRepository _campRepo;
    private readonly Guid _actorUserId = Guid.NewGuid();

    public ShiftObligationServiceTests()
        : base(Instant.FromUtc(2026, 4, 26, 12, 0))
    {
        _campServiceRead = Substitute.For<ICampServiceRead>();
        _shiftServiceRead = Substitute.For<IShiftServiceRead>();
        _teamServiceRead = Substitute.For<ITeamServiceRead>();
        _userService = NewDbBackedUserService();
        _obligationRepo = new ShiftObligationRepository(DbFactory);
        _campRepo = new CampRepository(DbFactory);

        _sut = new ShiftObligationService(
            _obligationRepo,
            _campServiceRead,
            _campRepo,
            _shiftServiceRead,
            _teamServiceRead,
            _userService,
            Substitute.For<IEmailService>(),
            Substitute.For<IEmailMessageFactory>(),
            AuditLog,
            Clock,
            NullLogger<ShiftObligationService>.Instance);
    }

    [HumansFact]
    public async Task Matrix_ExemptsNorg_AndAppliesPowerGridFilter()
    {
        var teamId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        var powerId = await SeedFunctionAsync(
            ShiftObligationTargetType.Team, teamId,
            ObligationApplicability.ElectricalGridConnected, defaultRequired: 6, sortOrder: 0);
        var shitNinjaId = await SeedFunctionAsync(
            ShiftObligationTargetType.Rota, rotaId,
            ObligationApplicability.AllBarrios, defaultRequired: 2, sortOrder: 1);

        StubColumnTargets(teamId, "power", "Power", rotaId, "Shit Ninja", "shit-ninja");

        SeedBarrios(
            ("Yellow Camp", "yellow-camp", ElectricalGrid.Yellow),
            ("OwnSupply Camp", "ownsupply-camp", ElectricalGrid.OwnSupply),
            ("Norg Camp", "norg-camp", ElectricalGrid.Norg),
            ("Unset Camp", "unset-camp", null));

        _shiftServiceRead.GetConfirmedSignupCountsByUserForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _shiftServiceRead.GetConfirmedSignupCountsByUserForRotaAsync(rotaId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        var m = await _sut.GetComplianceMatrixAsync(2026);

        m.ExemptNobodiesOrg.Select(e => e.BarrioName).Should().ContainSingle().Which.Should().Be("Norg Camp");
        m.Rows.Should().NotContain(r => r.BarrioName == "Norg Camp");

        var yellow = m.Rows.Single(r => string.Equals(r.BarrioName, "Yellow Camp", StringComparison.Ordinal));
        yellow.Cells.Single(c => c.ShiftObligationId == powerId).Applicable.Should().BeTrue();

        var ownSupply = m.Rows.Single(r => string.Equals(r.BarrioName, "OwnSupply Camp", StringComparison.Ordinal));
        ownSupply.Cells.Single(c => c.ShiftObligationId == powerId).Applicable.Should().BeFalse();
        ownSupply.Cells.Single(c => c.ShiftObligationId == shitNinjaId).Applicable.Should().BeTrue();

        m.OffGridForPower.Should().Contain(o => o.BarrioName == "OwnSupply Camp" && o.Reason == "OwnSupply");
        m.OffGridForPower.Should().Contain(o => o.BarrioName == "Unset Camp" && o.Reason == "Unclassified");
    }

    [HumansFact]
    public async Task Cell_Done_SumsBarrioMembers_OverrideBeatsDefault_UnderMembered()
    {
        var teamId = Guid.NewGuid();
        var powerId = await SeedFunctionAsync(
            ShiftObligationTargetType.Team, teamId,
            ObligationApplicability.ElectricalGridConnected, defaultRequired: 6, sortOrder: 0);

        StubColumnTargets(teamId, "power", "Power", rotaId: null, rotaName: null);

        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        var nonMember = Guid.NewGuid();
        var seasonId = SeedBarrioWithMembers(
            "Yellow Camp", "yellow-camp", ElectricalGrid.Yellow,
            (memberA, CampMemberStatus.Active), (memberB, CampMemberStatus.Active));

        await Db.SaveChangesAsync();

        // Override 8 beats the default 6 for this season.
        await _obligationRepo.SetOverrideAsync(seasonId, powerId, 8);

        _shiftServiceRead.GetConfirmedSignupCountsByUserForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>
            {
                [memberA] = 2,
                [memberB] = 1,
                [nonMember] = 5, // not a member of this barrio — ignored
            });

        var m = await _sut.GetComplianceMatrixAsync(2026);

        var cell = m.Rows.Single(r => string.Equals(r.BarrioName, "Yellow Camp", StringComparison.Ordinal))
            .Cells.Single(c => c.ShiftObligationId == powerId);
        cell.Done.Should().Be(3);           // 2 + 1, ignoring the non-member's 5
        cell.Required.Should().Be(8);       // override beats default 6
        cell.UnderMembered.Should().BeTrue(); // 2 active members < 8
    }

    [HumansFact]
    public async Task Detail_ListsSignedUpMembersDesc_OmitsNonMembers_PutsZeroSignupInChaseList()
    {
        var teamId = Guid.NewGuid();
        var powerId = await SeedFunctionAsync(
            ShiftObligationTargetType.Team, teamId,
            ObligationApplicability.ElectricalGridConnected, defaultRequired: 6, sortOrder: 0);

        StubColumnTargets(teamId, "power", "Power", rotaId: null, rotaName: null);

        var alice = SeedUser("Alice").Id;
        var bob = SeedUser("Bob").Id;
        var carol = SeedUser("Carol").Id; // active member, no signups -> chase list
        var nonMember = SeedUser("Ned").Id;
        var seasonId = SeedBarrioWithMembers(
            "Yellow Camp", "yellow-camp", ElectricalGrid.Yellow,
            (alice, CampMemberStatus.Active),
            (bob, CampMemberStatus.Active),
            (carol, CampMemberStatus.Active));
        await Db.SaveChangesAsync();

        _shiftServiceRead.GetConfirmedSignupCountsByUserForTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>
            {
                [alice] = 1,
                [bob] = 3,
                [nonMember] = 9, // not a member -> omitted
            });

        var detail = await _sut.GetBarrioObligationDetailAsync(seasonId);

        detail.Should().NotBeNull();
        detail!.BarrioName.Should().Be("Yellow Camp");
        var func = detail.Functions.Single(f => f.ShiftObligationId == powerId);
        func.Done.Should().Be(4); // 1 + 3
        func.SignedUp.Select(s => s.Name).Should().ContainInOrder("Bob", "Alice"); // count desc
        func.SignedUp.Should().NotContain(s => s.Name == "Ned");
        func.NotYetSignedUpNames.Should().Contain("Carol");
    }

    // ----- helpers ----------------------------------------------------------

    private async Task<Guid> SeedFunctionAsync(
        ShiftObligationTargetType targetType, Guid targetId,
        ObligationApplicability applicability, int defaultRequired, int sortOrder,
        bool isActive = true, string roleSlug = "power")
    {
        var obligation = new ShiftObligation
        {
            Id = Guid.NewGuid(),
            TargetType = targetType,
            TargetId = targetId,
            CampRoleSlug = roleSlug,
            Applicability = applicability,
            DefaultRequiredShiftCount = defaultRequired,
            IsActive = isActive,
            SortOrder = sortOrder,
            CreatedAt = Clock.GetCurrentInstant(),
        };
        await _obligationRepo.AddAsync(obligation);
        return obligation.Id;
    }

    private void StubColumnTargets(
        Guid teamId, string teamSlug, string teamName,
        Guid? rotaId, string? rotaName, string? rotaTeamSlug = null)
    {
        _teamServiceRead.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(MakeTeamInfo(teamId, teamName, teamSlug));

        if (rotaId is { } rid)
        {
            _shiftServiceRead.GetRotaTargetInfoAsync(rid, Arg.Any<CancellationToken>())
                .Returns(new RotaTargetInfo(rid, rotaName ?? "Rota", Guid.NewGuid(), rotaTeamSlug ?? "owning-team"));
        }
    }

    private static TeamInfo MakeTeamInfo(Guid id, string name, string slug) =>
        new(id, name, null, slug, true, false, SystemTeamType.None, false,
            false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), []);

    private void SeedBarrios(params (string Name, string Slug, ElectricalGrid? Grid)[] barrios)
    {
        var infos = barrios.Select(b => MakeCampInfo(b.Name, b.Slug, b.Grid, [])).ToList();
        _campServiceRead.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CampInfo>)infos);
    }

    private Guid SeedBarrioWithMembers(
        string name, string slug, ElectricalGrid? grid,
        params (Guid UserId, CampMemberStatus Status)[] members)
    {
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var memberInfos = members
            .Select(m => new CampSeasonMemberInfo(
                Guid.NewGuid(), m.UserId, m.Status,
                Clock.GetCurrentInstant(),
                m.Status == CampMemberStatus.Active ? Clock.GetCurrentInstant() : null,
                false))
            .ToList();

        var info = MakeCampInfo(name, slug, grid, memberInfos, campId, seasonId);

        // Seed the real camp/season/members so CampRepository reads agree with the
        // ICampServiceRead stub (the service may resolve names/members either way).
        Db.Camps.Add(new Camp { Id = campId, Slug = slug });
        Db.CampSeasons.Add(new CampSeason
        {
            Id = seasonId, CampId = campId, Year = 2026, Name = name,
            Status = CampSeasonStatus.Active, ElectricalGrid = grid,
        });
        foreach (var (mi, src) in memberInfos.Zip(members))
        {
            Db.CampMembers.Add(new CampMember
            {
                Id = mi.Id, CampSeasonId = seasonId, UserId = src.UserId,
                Status = src.Status, RequestedAt = Clock.GetCurrentInstant(),
                ConfirmedAt = mi.ConfirmedAt,
            });
        }

        _campServiceRead.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CampInfo>)new List<CampInfo> { info });

        return seasonId;
    }

    private static CampInfo MakeCampInfo(
        string name, string slug, ElectricalGrid? grid,
        IReadOnlyList<CampSeasonMemberInfo> members,
        Guid? campId = null, Guid? seasonId = null)
    {
        var cid = campId ?? Guid.NewGuid();
        var sid = seasonId ?? Guid.NewGuid();
        var season = new CampSeasonInfo(
            sid, cid, slug, 2026, null, name, "blurb", "EN", [],
            CampSeasonStatus.Active, YesNoMaybe.Yes, YesNoMaybe.No,
            AdultPlayspacePolicy.No, members.Count, null, null, grid, 0, null, null)
        {
            Members = members,
        };
        return new CampInfo(cid, slug, "c@example.com", "+34600000000", false, 0, [season]);
    }
}
