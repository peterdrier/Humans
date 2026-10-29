using System.Globalization;
using AwesomeAssertions;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Humans.Preapprovals.Tests.Services;

public sealed class MoneyValidationTests : PreapprovalsTestHarness
{
    [HumansTheory]
    [InlineData(false, "per-person", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData(true, "per-person", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData(false, "ceiling", "1000.005", "Preapprovals_Error_AmountPrecision")]
    [InlineData(true, "ceiling", "1000.005", "Preapprovals_Error_AmountPrecision")]
    [InlineData(false, "per-person", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData(true, "per-person", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData(false, "ceiling", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData(true, "ceiling", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    public async Task ProgramMoney_RejectsValuesThatCannotBeStoredExactly(
        bool editing, string field, string text, string expected)
    {
        var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
        var program = editing ? await SeedProgramAsync() : null;
        var save = new ProgramSave("Program", null, ProgramKind.FixedPerPerson,
            field is "per-person" ? amount : 300m, field is "ceiling" ? amount : 1000m,
            62900150, true, Today.PlusMonths(1), ProgramStatus.Open, []);
        var actor = SeedUser();
        var service = NewService();

        var refusal = editing
            ? (await service.UpdateProgramAsync(program!.Id, save, actor, Ct)).Refusal
            : (await service.CreateProgramAsync(save, actor, Ct)).Refusal;

        refusal!.Key.Should().Be(expected);
        await using var db = OpenContext();
        var stored = await db.Programs.ToListAsync(Ct);
        stored.Should().HaveCount(editing ? 1 : 0);
        if (editing)
        {
            stored[0].PerPersonAmount.Should().Be(300m);
            stored[0].TotalCeiling.Should().Be(1000m);
        }
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("issue", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData("apply", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData("edit-application", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData("approve", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData("change", "0.001", "Preapprovals_Error_AmountPrecision")]
    [InlineData("issue", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData("apply", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData("edit-application", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData("approve", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    [InlineData("change", "10000000000000000", "Preapprovals_Error_AmountTooLarge")]
    public async Task GrantMoney_RejectsValuesThatCannotBeStoredExactly(string operation, string text, string expected)
    {
        var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, ceiling: null);
        var actor = SeedUser();
        var beneficiary = SeedUser();
        var grant = operation is "edit-application" or "approve" or "change"
            ? await SeedGrantAsync(program, beneficiary, amount: 300m,
                status: operation is "change" ? GrantStatus.Open : GrantStatus.Applied)
            : null;
        var service = NewService();

        var refusal = operation switch
        {
            "issue" => (await service.IssueAsync(program.Id, beneficiary, amount, null, actor, Ct)).Refusal,
            "apply" or "edit-application" => (await service.ApplyAsync(program.Id, beneficiary, null, amount, Ct)).Refusal,
            "approve" => (await service.ApproveAsync(grant!.Id, amount, actor, Ct)).Refusal,
            "change" => (await service.ChangeAmountAsync(grant!.Id, amount, actor, Ct)).Refusal,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        refusal!.Key.Should().Be(expected);
        await using var db = OpenContext();
        var stored = await db.Grants.ToListAsync(Ct);
        stored.Should().HaveCount(grant is null ? 0 : 1);
        if (grant is not null) stored[0].MaxAmount.Should().Be(300m);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Notifications.ReceivedCalls().Should().BeEmpty();
        Email.ReceivedCalls().Should().BeEmpty();
    }
    [HumansTheory]
    [InlineData("0.01")]
    [InlineData("0.0100")]
    [InlineData("9999999999999999.99")]
    public async Task MoneyBoundary_StoresExactCentAmounts(string text)
    {
        var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
        var actor = SeedUser();
        var save = new ProgramSave("Program", null, ProgramKind.FixedPerPerson, amount, amount,
            62900150, true, Today.PlusMonths(1), ProgramStatus.Open, []);
        var service = NewService();

        var created = await service.CreateProgramAsync(save, actor, Ct);
        created.Refusal.Should().BeNull();
        var issued = await service.IssueAsync(created.Value, SeedUser(), null, null, actor, Ct);

        issued.Refusal.Should().BeNull();
        await using var db = OpenContext();
        (await db.Grants.SingleAsync(Ct)).MaxAmount.Should().Be(amount);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedGrant_RefusesLegacyZeroAmounts(bool bulk)
    {
        var program = await SeedProgramAsync(perPerson: 0m);
        var beneficiary = SeedUser();
        var actor = SeedUser();
        var service = NewService();

        var refusal = bulk
            ? (await service.IssueManyAsync(program.Id, [beneficiary], actor, Ct)).Refusal
            : (await service.IssueAsync(program.Id, beneficiary, null, null, actor, Ct)).Refusal;

        refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountPositive);
        await using var db = OpenContext();
        (await db.Grants.CountAsync(Ct)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

}
