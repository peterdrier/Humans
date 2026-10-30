using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using Humans.Settings.Contracts;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Grants.Tests.Services;

/// <summary>Creating and editing programs: validation, managers, audit.</summary>
public sealed class ProgramTests : GrantsTestHarness
{
    private static ProgramSave Save(
        string name = "Build crew 2027", GrantIssuer issuedBy = GrantIssuer.Managers, string? sourceKey = null,
        params Guid[] managers) =>
        new(name, "a ticket at the build-crew price", null, Event2027.Id, issuedBy, sourceKey, null,
            GrantProgramStatus.Open, managers);

    [HumansFact]
    public async Task Create_StoresProgramAndManagers_AuditsBoth()
    {
        var actor = SeedUser("Board");
        var manager = SeedUser("Manager");

        var result = await NewService().CreateProgramAsync(Save(managers: manager), actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        var program = await db.Programs.Include(p => p.Managers).SingleAsync(Ct);
        program.Id.Should().Be(result.Value);
        program.Managers.Select(m => m.UserId).Should().Equal(manager);
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramCreated, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramManagerAdded, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, manager, AuditEntityTypes.User);
    }

    [HumansTheory]
    [Xunit.InlineData("", false, null)]
    [Xunit.InlineData("Name", true, null)]
    [Xunit.InlineData("Name", true, "  ")]
    public async Task Create_RefusesMissingNameOrSourceKey(string name, bool fromSource, string? sourceKey)
    {
        var issuer = fromSource ? GrantIssuer.Source : GrantIssuer.Managers;
        var result = await NewService().CreateProgramAsync(Save(name, issuer, sourceKey), SeedUser(), Ct);

        result.Refusal.Should().NotBeNull();
        await using var db = OpenContext();
        (await db.Programs.CountAsync(Ct)).Should().Be(0);
    }

    [HumansTheory]
    [Xunit.InlineData(2, 0)]
    [Xunit.InlineData(0, 2)]
    public async Task CreateAndUpdate_RefuseUndefinedIssuerOrStatus(int issuer, int status)
    {
        var save = Save() with { IssuedBy = (GrantIssuer)issuer, Status = (GrantProgramStatus)status };
        var program = await SeedProgramAsync();
        var service = NewService();

        (await service.CreateProgramAsync(save, SeedUser(), Ct)).Refusal.Should().NotBeNull();
        (await service.UpdateProgramAsync(program.Id, save, SeedUser(), Ct)).Refusal.Should().NotBeNull();

        await using var db = OpenContext();
        var stored = await db.Programs.SingleAsync(Ct);
        stored.IssuedBy.Should().Be(GrantIssuer.Managers);
        stored.Status.Should().Be(GrantProgramStatus.Open);
    }

    [HumansFact]
    public async Task Create_ManagersProgram_DropsAnySourceKey()
    {
        var result = await NewService().CreateProgramAsync(Save(sourceKey: "low-income"), SeedUser(), Ct);

        await using var db = OpenContext();
        (await db.Programs.SingleAsync(p => p.Id == result.Value, Ct)).SourceKey.Should().BeNull();
    }

    [HumansFact]
    public async Task Update_SwapsManagers_AuditsEditAddAndRemove()
    {
        var actor = SeedUser("Board");
        var oldManager = SeedUser("Old");
        var newManager = SeedUser("New");
        var program = await SeedProgramAsync(managers: oldManager);

        var result = await NewService().UpdateProgramAsync(program.Id, Save("Renamed", managers: newManager), actor, Ct);

        result.Refusal.Should().BeNull();
        await using var db = OpenContext();
        var saved = await db.Programs.Include(p => p.Managers).SingleAsync(Ct);
        saved.Name.Should().Be("Renamed");
        saved.Managers.Select(m => m.UserId).Should().Equal(newManager);
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(s => s.Contains("Renamed")), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramManagerAdded, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, newManager, AuditEntityTypes.User);
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramManagerRemoved, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, oldManager, AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task OneOpenProgramPerSourceKey_OnCreateAndOnReopen()
    {
        var actor = SeedUser("Admin");
        await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var closed = await SeedProgramAsync(GrantIssuer.Source, "low-income", status: GrantProgramStatus.Closed, name: "Old");
        var service = NewService();

        (await service.CreateProgramAsync(Save("Second", GrantIssuer.Source, "low-income"), actor, Ct)).Refusal
            .Should().Contain("already the open program");
        (await service.UpdateProgramAsync(closed.Id, Save("Old", GrantIssuer.Source, "low-income"), actor, Ct)).Refusal
            .Should().Contain("already the open program");
        (await service.CreateProgramAsync(Save("Other key", GrantIssuer.Source, "workers"), actor, Ct)).Refusal
            .Should().BeNull();
    }

    [HumansFact]
    public async Task Create_TakesTheYearFromTheChosenEvent()
    {
        var result = await NewService().CreateProgramAsync(Save(), SeedUser(), Ct);

        await using var db = OpenContext();
        var program = await db.Programs.SingleAsync(p => p.Id == result.Value, Ct);
        program.EventSettingsId.Should().Be(Event2027.Id);
        program.Year.Should().Be(2027);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Create_RefusesAnUnknownOrDeletedEvent(bool deleted)
    {
        var withdrawn = BurnFixtures.Burn(eventName: "Withdrawn", year: 2028) with { Status = EventSettingsStatus.Deleted };
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([Event2027, withdrawn]);

        var result = await NewService().CreateProgramAsync(
            Save() with { EventSettingsId = deleted ? withdrawn.Id : Guid.NewGuid() }, SeedUser(), Ct);

        result.Refusal.Should().Be("Pick an event.");
        await using var db = OpenContext();
        (await db.Programs.CountAsync(Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task Update_MovesTheProgramToAnotherEvent_AndAuditsIt()
    {
        var autumn = BurnFixtures.Burn(eventName: "Autumn 2027", year: 2027, gateOpeningDate: new LocalDate(2027, 10, 1));
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([Event2027, autumn]);
        var program = await SeedProgramAsync();
        var actor = SeedUser();

        (await NewService().UpdateProgramAsync(program.Id, Save() with { EventSettingsId = autumn.Id }, actor, Ct))
            .Refusal.Should().BeNull();

        await using var db = OpenContext();
        (await db.Programs.SingleAsync(Ct)).EventSettingsId.Should().Be(autumn.Id);
        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(d => d.Contains("event Nowhere 2027 → Autumn 2027")), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AnUnstampedProgram_ReadsAsTheEarliestEventOfItsYear_UntilTheDataMigrationStampsIt()
    {
        var autumn = BurnFixtures.Burn(eventName: "Autumn 2027", year: 2027, gateOpeningDate: new LocalDate(2027, 10, 1));
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([autumn, Event2027]);
        var program = await SeedProgramAsync();
        var service = NewService();

        var info = (await service.GetProgramAsync(program.Id, Ct))!;
        info.EventSettingsId.Should().Be(Event2027.Id);
        info.EventName.Should().Be("Nowhere 2027");

        (await service.GetEventIdPlanAsync(Ct)).Should().ContainSingle().Which.Event.Id.Should().Be(Event2027.Id);
        (await service.AssignProgramEventIdsAsync(Ct)).Should().Be(1);
        (await service.AssignProgramEventIdsAsync(Ct)).Should().Be(0);
        await using var db = OpenContext();
        (await db.Programs.SingleAsync(Ct)).EventSettingsId.Should().Be(Event2027.Id);
    }

    [HumansFact]
    public async Task Update_AuditsAMoveBetweenTwoEventsOfTheSameName()
    {
        var twin = BurnFixtures.Burn(eventName: Event2027.EventName, year: 2027, gateOpeningDate: new LocalDate(2027, 10, 1));
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([Event2027, twin]);
        var program = await SeedProgramAsync();
        var actor = SeedUser();

        await NewService().UpdateProgramAsync(program.Id, Save() with { EventSettingsId = twin.Id }, actor, Ct);

        await AuditLog.Received(1).LogAsync(GrantsAuditActions.GrantProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task TheYearFollowsTheEvent_WhenTheEventMovesToAnotherYear()
    {
        var program = await SeedProgramAsync();
        await NewService().AssignProgramEventIdsAsync(Ct);
        var moved = Event2027 with { Year = 2028, GateOpeningDate = new LocalDate(2028, 1, 5) };
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns([moved]);

        (await NewService().GetProgramAsync(program.Id, Ct))!.Year.Should().Be(2028);
    }
}
