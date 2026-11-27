using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Caching;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>A merge folds two accounts into one human, so a creditor binding on either id is that human's.</summary>
public class CreditorMergeTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 1, 12, 0);
    private static readonly Guid Admin = Guid.NewGuid();
    private readonly Guid _archived = Guid.NewGuid();
    private readonly Guid _survivor = Guid.NewGuid();
    private readonly IDbContextFactory<FinanceDbContext> _factory = new TestDbContextFactory<FinanceDbContext>(
        TestDbOptions.InMemory<FinanceDbContext>());
    private readonly IHoldedWrite _write = Substitute.For<IHoldedWrite>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public CreditorMergeTests()
    {
        // The merge has happened: both ids resolve to the survivor, who carries the archived id.
        var survivor = UserInfo.Create(new User { Id = _survivor }, [], [], null) with { MergedUserIds = [_archived] };
        _users.GetUserInfoAsync(_survivor, Arg.Any<CancellationToken>()).Returns(survivor);
        _users.GetUserInfoAsync(_archived, Arg.Any<CancellationToken>()).Returns(survivor);
        _write.CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c-minted");
    }


    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Service MakeService() => new(
        FixedOrgTimeZone.Madrid, new Repository(_factory), Substitute.For<IBudgetService>(), TestContacts.Holded(), _write,
        Substitute.For<IExpenseHoldedBookingService>(), new FakeClock(Now), _audit, _users,
        Substitute.For<IEmailService>(), TestFinanceEmails.Create(), Options.Create(new SepaOptions()),
        new TrackedCache<string, Dictionary<string, int>>(
            "Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        Substitute.For<IPaymentDetailsManagement>(), NullLogger<Service>.Instance);

    private async Task BindAsync(Guid userId, string contactId, int accountNum)
    {
        await using var ctx = await _factory.CreateDbContextAsync(Ct);
        ctx.HoldedCreditorContacts.Add(new HoldedCreditorContact
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = CreditorContactSource.Auto,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<List<HoldedCreditorContact>> BindingsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync(Ct);
        return await ctx.HoldedCreditorContacts.AsNoTracking().ToListAsync(Ct);
    }

    private Task<string> PushAsync(string? seedContactId = null, int? seedAccountNum = null) =>
        MakeService().EnsureCreditorContactAsync(
            _survivor, "Ana Ruiz", null, null, seedContactId, seedAccountNum, "TestJob", Ct);

    [HumansFact]
    public async Task Merge_MovesTheArchivedBinding_AndTheNextPushReusesIt()
    {
        await BindAsync(_archived, "c-x", 40000012);

        await MakeService().ReassignAsync(_archived, _survivor, Admin, Now, Ct);
        await MakeService().ReassignAsync(_archived, _survivor, Admin, Now, Ct); // a re-run is a no-op
        var contactId = await PushAsync("c-x", 40000012);

        contactId.Should().Be("c-x");
        (await BindingsAsync()).Should().ContainSingle()
            .Which.Should().Match<HoldedCreditorContact>(b =>
                b.UserId == _survivor && b.HoldedContactId == "c-x" && b.SupplierAccountNum == 40000012);
        await _write.DidNotReceive().CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(FinanceAuditActions.HoldedCreditorBound, Arg.Any<string>(), _survivor,
            Arg.Is<string>(d => d.Contains("40000012") && d.Contains(_archived.ToString())), Admin,
            _survivor, Arg.Any<string?>());
    }

    [HumansFact]
    public async Task MergeBeforeBindingsMoved_SurvivorsPush_TakesOverTheArchivedBinding_InsteadOfMinting()
    {
        // The merge ran before Finance moved bindings: the archived id still holds the human's contact.
        await BindAsync(_archived, "c-x", 40000012);

        var contactId = await PushAsync("c-x", 40000012);

        contactId.Should().Be("c-x");
        await _write.DidNotReceive().CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>());
        (await BindingsAsync()).Should().ContainSingle().Which.UserId.Should().Be(_survivor);
        (await MakeService().GetCreditorContactByUserAsync(_archived, Ct))!.HoldedContactId.Should().Be("c-x");
    }

    [HumansFact]
    public async Task Merge_BothBound_KeepsBothBindings()
    {
        await BindAsync(_archived, "c-x", 40000012);
        await BindAsync(_survivor, "c-y", 40000013);

        await MakeService().ReassignAsync(_archived, _survivor, Admin, Now, Ct);
        var contactId = await PushAsync("c-x", 40000012);

        contactId.Should().Be("c-y");
        (await BindingsAsync()).Select(b => (b.UserId, b.HoldedContactId)).Should().BeEquivalentTo(
            [(_archived, "c-x"), (_survivor, "c-y")]);
        await _write.DidNotReceive().CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>());
    }
}
