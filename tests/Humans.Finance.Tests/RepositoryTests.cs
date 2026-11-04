using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// The repository owns every Finance table. What is pinned here is the part
/// that is not a one-line query: what a document snapshot replaces, what updates preserve,
/// and which binding facts cannot be erased by missing metadata.
/// </summary>
public class RepositoryTests
{
    private static readonly Instant Created = Instant.FromUtc(2026, 4, 1, 0, 0);
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 1, 12, 0);

    private static (Repository Repo, IDbContextFactory<FinanceDbContext> Factory) Make()
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase($"finance-{Guid.NewGuid():N}")
            .Options;
        var factory = new Factory(options);
        return (new Repository(factory), factory);
    }

    private sealed class Factory(DbContextOptions<FinanceDbContext> options)
        : IDbContextFactory<FinanceDbContext>
    {
        public FinanceDbContext CreateDbContext() => new(options);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task UpsertCreditorContact_ANullAccountNumberDoesNotEraseAResolvedOne()
    {
        // A push that has not resolved the 400000xx yet still writes the contact id. It must not
        // clear a number an earlier push or an admin already recorded.
        var (repo, factory) = Make();
        var userId = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-1", 40000004, CreditorContactSource.Manual), Created, Ct);

        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-2", null), Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        var stored = await ctx.HoldedCreditorContacts.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        stored.HoldedContactId.Should().Be("contact-2");
        stored.SupplierAccountNum.Should().Be(40000004);
        stored.UpdatedAt.Should().Be(Now);
    }

    [HumansFact]
    public async Task UpsertCreditorContact_ReturnsTrue_OnInsert_AndOnUpdate()
    {
        // The false branch (Postgres 23505 on IX_holded_creditor_contacts_UserId) cannot be raised here:
        // EF InMemory enforces no unique indexes. It is covered in the service tests via the substitute.
        var (repo, _) = Make();
        var userId = Guid.NewGuid();

        (await repo.UpsertCreditorContactAsync(Binding(userId, "contact-1", 40000004), Created, Ct))
            .Should().BeTrue();
        (await repo.UpsertCreditorContactAsync(Binding(userId, "contact-2", 40000004), Now, Ct))
            .Should().BeTrue();
    }

    [HumansFact]
    public async Task TryInsertCreditorContact_ReturnsTrue_AndStoresTheRowOnAFreshMember()
    {
        // The false branch (Postgres 23505 on IX_holded_creditor_contacts_UserId) cannot be raised here:
        // EF InMemory enforces no unique indexes. It is covered in the service tests via the substitute.
        var (repo, factory) = Make();
        var userId = Guid.NewGuid();

        (await repo.TryInsertCreditorContactAsync(Binding(userId, "contact-1", 40000004), Ct))
            .Should().BeTrue();

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        (await ctx.HoldedCreditorContacts.SingleAsync(Xunit.TestContext.Current.CancellationToken)).HoldedContactId.Should().Be("contact-1");
    }

    [HumansFact]
    public async Task UpsertCreditorContact_IsKeyedByMember_SoAMemberNeverHoldsTwoBindings()
    {
        var (repo, factory) = Make();
        var userId = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-1", 40000004), Created, Ct);
        await repo.UpsertCreditorContactAsync(
            Binding(userId, "contact-2", 40000009), Now, Ct);

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        var stored = await ctx.HoldedCreditorContacts.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        stored.SupplierAccountNum.Should().Be(40000009);
        stored.CreatedAt.Should().Be(Created); // mutated in place, not replaced
    }

    [HumansFact]
    public async Task DeleteCreditorContact_RemovesOnlyThatMembersRow()
    {
        var (repo, factory) = Make();
        var ana = Guid.NewGuid();
        var bo = Guid.NewGuid();
        await repo.UpsertCreditorContactAsync(Binding(ana, "c-a", 40000004), Now, Ct);
        await repo.UpsertCreditorContactAsync(Binding(bo, "c-b", 40000005), Now, Ct);

        (await repo.DeleteCreditorContactAsync(ana, Ct))!.HoldedContactId.Should().Be("c-a");
        (await repo.DeleteCreditorContactAsync(ana, Ct)).Should().BeNull();

        await using var ctx = await factory.CreateDbContextAsync(Ct);
        (await ctx.HoldedCreditorContacts.SingleAsync(Xunit.TestContext.Current.CancellationToken)).UserId.Should().Be(bo);
    }

    [HumansFact]
    public async Task SaveSepaTransferBooking_StampsBookedAtBookedByAndMovementId()
    {
        var (repo, _) = Make();
        var fileId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await repo.AddSepaPayoutAsync(
            new SepaPayoutFile
            {
                Id = fileId,
                GeneratedAt = Created,
                GeneratedByUserId = actor,
                FileName = "payout.xml",
                Checksum = "abc",
                Xml = "<x/>",
            },
            [new SepaPayoutTransfer
            {
                Id = transferId, FileId = fileId, UserId = userId, SupplierAccountNum = 40000004,
                HoldedContactId = "c1",
                CreditorName = "Ana Ruiz", Iban = "ES7921000813610123456789",
                IbanMasked = "ES79****789", Amount = 30m,
            }], Ct);

        // A run that posted and then stopped records what landed; the booking then overwrites it
        // with the full list.
        await repo.SaveSepaTransferPaymentRefsAsync(transferId, "pay-1", Ct);
        var landed = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        landed.IsBooked.Should().BeFalse();
        landed.HoldedPaymentRefs.Should().Be("pay-1");
        landed.PostedButNotBooked.Should().BeTrue();

        // Well past 512 characters: the column must hold every ref the run posted.
        var refs = string.Join(",", Enumerable.Range(1, 30).Select(i => $"68f2a7c1e5b3d4a9f0c1b2{i:D2}"));
        await repo.SaveSepaTransferBookingAsync(transferId, Now, actor, "mv-1", null, refs, Ct);

        var row = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        row.TransferId.Should().Be(transferId);
        row.FileName.Should().Be("payout.xml");
        row.BookedAt.Should().Be(Now);
        row.BookedByUserId.Should().Be(actor);
        row.HoldedBankMovementId.Should().Be("mv-1");
        row.ReconciledAt.Should().BeNull();
        row.HoldedPaymentRefs.Should().Be(refs);
        row.HoldedContactId.Should().Be("c1");
        row.IsBooked.Should().BeTrue();
        row.PostedButNotBooked.Should().BeFalse();
    }

    [HumansFact]
    public async Task MarkSepaTransferReconciled_StampsOnlyReconciledAt()
    {
        var (repo, _) = Make();
        var fileId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await repo.AddSepaPayoutAsync(
            new SepaPayoutFile
            {
                Id = fileId,
                GeneratedAt = Created,
                GeneratedByUserId = actor,
                FileName = "payout.xml",
                Checksum = "abc",
                Xml = "<x/>",
            },
            [new SepaPayoutTransfer
            {
                Id = transferId, FileId = fileId, UserId = userId, SupplierAccountNum = 40000004,
                HoldedContactId = "c1",
                CreditorName = "Ana Ruiz", Iban = "ES7921000813610123456789",
                IbanMasked = "ES79****789", Amount = 30m,
            }], Ct);
        await repo.SaveSepaTransferBookingAsync(transferId, Now, actor, "mv-1", null, null, Ct);

        var reconciledAt = Now.Plus(Duration.FromHours(3));
        await repo.MarkSepaTransferReconciledAsync(transferId, reconciledAt, Ct);

        var row = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        row.BookedAt.Should().Be(Now);
        row.BookedByUserId.Should().Be(actor);
        row.HoldedBankMovementId.Should().Be("mv-1");
        row.ReconciledAt.Should().Be(reconciledAt);
    }

    [HumansFact]
    public async Task GetSepaPayoutTransferRows_ProjectsMovementIdAndReconciledAt()
    {
        var (repo, _) = Make();
        var fileId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await repo.AddSepaPayoutAsync(
            new SepaPayoutFile
            {
                Id = fileId,
                GeneratedAt = Created,
                GeneratedByUserId = actor,
                FileName = "payout.xml",
                Checksum = "abc",
                Xml = "<x/>",
            },
            [new SepaPayoutTransfer
            {
                Id = transferId, FileId = fileId, UserId = userId, SupplierAccountNum = 40000004,
                HoldedContactId = "c1",
                CreditorName = "Ana Ruiz", Iban = "ES7921000813610123456789",
                IbanMasked = "ES79****789", Amount = 30m,
            }], Ct);

        var unbooked = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        unbooked.HoldedBankMovementId.Should().BeNull();
        unbooked.ReconciledAt.Should().BeNull();

        var reconciledAt = Now.Plus(Duration.FromHours(1));
        await repo.SaveSepaTransferBookingAsync(transferId, Now, actor, "mv-2", reconciledAt, null, Ct);

        var booked = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        booked.HoldedBankMovementId.Should().Be("mv-2");
        booked.ReconciledAt.Should().Be(reconciledAt);
    }

    [HumansFact]
    public async Task SaveSepaTransferWithdrawn_StampsWithdrawnAtAndBy_AndTheRowsCarryThem()
    {
        var (repo, _) = Make();
        var fileId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await repo.AddSepaPayoutAsync(
            new SepaPayoutFile
            {
                Id = fileId,
                GeneratedAt = Created,
                GeneratedByUserId = actor,
                FileName = "payout.xml",
                Checksum = "abc",
                Xml = "<x/>",
            },
            [new SepaPayoutTransfer
            {
                Id = transferId, FileId = fileId, UserId = userId, SupplierAccountNum = 41000060,
                HoldedContactId = "c1",
                CreditorName = "Paul Vivien", Iban = "ES7921000813610123456789",
                IbanMasked = "ES79****789", Amount = 4800m,
            }], Ct);

        var fresh = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        fresh.IsWithdrawn.Should().BeFalse();
        fresh.WithdrawnAt.Should().BeNull();

        await repo.SaveSepaTransfersWithdrawnAsync([transferId], Now, actor, Ct);

        var row = (await repo.GetSepaPayoutTransferRowsAsync(Ct)).Should().ContainSingle().Subject;
        row.WithdrawnAt.Should().Be(Now);
        row.WithdrawnByUserId.Should().Be(actor);
        row.IsWithdrawn.Should().BeTrue();
        row.IsBooked.Should().BeFalse();
        row.BookedAt.Should().BeNull();

        var export = (await repo.GetSepaPayoutsForUserAsync(userId, Ct)).Should().ContainSingle().Subject;
        export.WithdrawnAt.Should().Be(Now);
        export.BookedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task ManagedAccounts_UpsertByNumber_KeepsIdAndCreatedAt_ReplacesTheRest()
    {
        var (repo, _) = Make();
        var t0 = Instant.FromUtc(2026, 9, 1, 0, 0);
        var first = new HoldedManagedAccount
        {
            Id = Guid.NewGuid(),
            HoldedAccountNumber = 62900150,
            HoldedAccountId = "acc-1",
            Label = "Workgroups / ALM 2027",
            IsActive = true,
            CreatedAt = t0,
            UpdatedAt = t0,
        };
        await repo.UpsertManagedAccountAsync(first, Ct);

        var t1 = t0.Plus(Duration.FromDays(1));
        await repo.UpsertManagedAccountAsync(new HoldedManagedAccount
        {
            Id = Guid.NewGuid(),
            HoldedAccountNumber = 62900150,
            HoldedAccountId = "acc-1",
            Label = "Workgroups / ALM 2027",
            IsActive = false,
            CreatedAt = t1,
            UpdatedAt = t1,
        }, Ct);

        var rows = await repo.GetManagedAccountsAsync(Ct);
        var row = rows.Should().ContainSingle().Which;
        row.Id.Should().Be(first.Id);
        row.CreatedAt.Should().Be(t0);
        row.UpdatedAt.Should().Be(t1);
        row.IsActive.Should().BeFalse();
    }

    private static HoldedCreditorContact Binding(
        Guid userId, string contactId, int? accountNum,
        CreditorContactSource source = CreditorContactSource.Auto) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HoldedContactId = contactId,
            SupplierAccountNum = accountNum,
            Source = source,
            CreatedAt = Created,
            UpdatedAt = Created,
        };
}
