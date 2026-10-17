using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Surveys.Services;

/// <summary>Queues the daily survey reminder batch independently of survey authoring.</summary>
internal sealed class SurveyReminderSender(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    IUserService userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    SurveysEmails emailMessages,
    ISurveyInviteTokenProvider tokenProvider,
    ILogger<SurveyReminderSender> logger) : ISurveyReminderSender, IApplicationService
{
    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var due = await repo.GetInvitationsDueForReminderAsync(cutoff, ct);
        if (due.Count == 0) return 0;

        var userIds = due.Select(i => i.UserId).Distinct().ToList();
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(userIds, ct);
        var users = await userService.GetUserInfosAsync(userIds, ct);

        // Loaded once per distinct survey (few open surveys at this scale). The answer window is
        // re-checked here rather than in the query: Open alone is not answerable, and reminding
        // someone about a survey past its ClosesAt sends them to the Closed page — and spends their
        // one ReminderSentAt stamp doing it.
        var surveys = new Dictionary<Guid, (LocalizedText Title, string DefaultCulture, bool Answerable)>();

        var reminded = 0;
        foreach (var inv in due)
        {
            // An invitation held by a since-merged id belongs to the survivor's past: the
            // survivor's own invitation (if any) carries the reminder, and for an Asociado vote
            // this token could no longer answer. Left unstamped and skipped, like a missing email.
            if (users.TryGetValue(inv.UserId, out var holder) && holder.Id != inv.UserId)
            {
                logger.LogInformation(
                    "Invitation {InvitationId} belongs to merged user {UserId}; skipping reminder",
                    inv.Id, inv.UserId);
                continue;
            }

            if (!emails.TryGetValue(inv.UserId, out var email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping reminder",
                    inv.UserId, inv.SurveyId);
                continue;
            }

            if (!surveys.TryGetValue(inv.SurveyId, out var meta))
            {
                var survey = await repo.GetByIdAsync(inv.SurveyId, ct);
                if (survey is null) continue;
                meta = (
                    survey.Title,
                    survey.DefaultCulture,
                    SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, now));
                surveys[inv.SurveyId] = meta;
            }

            if (!meta.Answerable) continue;

            // Per-invitee guard (mirrors SendInvitesAsync): preparation or transport failure must not abort the
            // sweep. ReminderSentAt stays unstamped on failure so the next daily run retries.
            try
            {
                var preferredCulture = users.TryGetValue(inv.UserId, out var user) ? user.PreferredLanguage : null;
                var culture = preferredCulture.IsSupportedCultureCode()
                    ? preferredCulture!
                    : meta.DefaultCulture;
                var name = user?.BurnerName ?? string.Empty;
                var token = tokenProvider.Create(inv.Id);
                var title = meta.Title.Resolve(culture, meta.DefaultCulture);
                var msg = emailMessages.SurveyReminder(email, name, title, token, culture);

                await emailService.SendAsync(msg, ct);
                await repo.SetReminderSentAsync(inv.Id, now, ct);
                reminded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue survey reminder email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    inv.UserId, inv.Id, inv.SurveyId);
            }
        }

        await auditLog.LogAsync(AuditAction.SurveyReminderSent, AuditEntityTypes.Survey, Guid.Empty,
            $"Sent {reminded} survey reminder(s)", jobName: AuditEntityTypes.ReminderJob);

        return reminded;
    }
}
