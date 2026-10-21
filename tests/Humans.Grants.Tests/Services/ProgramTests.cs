using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Grants.Tests.Services;

/// <summary>Creating and editing programs: validation, managers, audit.</summary>
public sealed class ProgramTests : GrantsTestHarness
{
    private static ProgramSave Save(
        string name = "Build crew 2027", GrantIssuer issuedBy = GrantIssuer.Managers, string? sourceKey = null,
        params Guid[] managers) =>
        new(name, "a ticket at the build-crew price", null, 2027, issuedBy, sourceKey, null,
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
        await AuditLog.Received(1).LogAsync(AuditAction.GrantProgramCreated, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(AuditAction.GrantProgramManagerAdded, AuditEntityTypes.Program, program.Id,
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
        await AuditLog.Received(1).LogAsync(AuditAction.GrantProgramEdited, AuditEntityTypes.Program, program.Id,
            Arg.Is<string>(s => s.Contains("Renamed")), actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        await AuditLog.Received(1).LogAsync(AuditAction.GrantProgramManagerAdded, AuditEntityTypes.Program, program.Id,
            Arg.Any<string>(), actor, newManager, AuditEntityTypes.User);
        await AuditLog.Received(1).LogAsync(AuditAction.GrantProgramManagerRemoved, AuditEntityTypes.Program, program.Id,
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
}
