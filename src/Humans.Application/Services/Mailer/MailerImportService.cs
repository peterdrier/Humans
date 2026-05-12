using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Mailer;

public sealed class MailerImportService : IMailerImportService
{
    private readonly IMailerLiteService _ml;
    private readonly IUserEmailService _userEmails;
    private readonly IUserService _users;
    private readonly IAccountProvisioningService _provisioning;
    private readonly ICommunicationPreferenceService _prefs;
    private readonly IForgottenEmailService _forgotten;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<MailerImportService> _logger;

    public MailerImportService(
        IMailerLiteService ml,
        IUserEmailService userEmails,
        IUserService users,
        IAccountProvisioningService provisioning,
        ICommunicationPreferenceService prefs,
        IForgottenEmailService forgotten,
        IAuditLogService audit,
        IClock clock,
        ILogger<MailerImportService> logger)
    {
        _ml = ml;
        _userEmails = userEmails;
        _users = users;
        _provisioning = provisioning;
        _prefs = prefs;
        _forgotten = forgotten;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var decisions = new List<SubscriberDecision>();
        var subs = new List<MailerLiteSubscriber>();
        await foreach (var s in _ml.ListSubscribersAsync(ct)) subs.Add(s);

        foreach (var s in subs)
        {
            // 1. Unconfirmed
            if (string.Equals(s.Status, "unconfirmed", StringComparison.OrdinalIgnoreCase))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.UnconfirmedSkipped, null, null, null));
                continue;
            }

            // 2. Forgotten
            if (await _forgotten.IsForgottenAsync(s.Email, ct))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.ForgottenSkipped, null, null, null));
                continue;
            }

            // 3. Verified match
            var verified = await _userEmails.FindVerifiedEmailWithUserAsync(s.Email, ct);
            if (verified is not null)
            {
                var targetId = await ResolveTombstoneAsync(verified.UserId, ct);
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.AttachVerified, targetId, null, null));
                continue;
            }

            // 4. Unverified match
            var row = await _userEmails.FindAnyEmailRowByAddressAsync(s.Email, ct);
            if (row is var (uid, emailId))
            {
                decisions.Add(new SubscriberDecision(s.Email, s.Status,
                    SubscriberOutcome.DeleteUnverifiedThenCreate, uid, emailId, null));
                continue;
            }

            // 5. No match
            decisions.Add(new SubscriberDecision(s.Email, s.Status,
                SubscriberOutcome.CreateContact, null, null, null));
        }

        return new ImportPlan(decisions, subs.Count);
    }

    private async Task<Guid> ResolveTombstoneAsync(Guid userId, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { userId };
        var current = userId;
        while (true)
        {
            var user = await _users.GetByIdAsync(current, ct);
            if (user?.MergedToUserId is not Guid next) return current;
            if (!visited.Add(next)) return current;
            current = next;
        }
    }

    public Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken ct = default)
        => throw new NotSupportedException("ApplyAsync is implemented in Task 21.");
}
