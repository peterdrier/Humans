using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Surveys.Data;

/// <summary>EF-backed <see cref="ISurveyRepository"/>.</summary>
internal sealed partial class SurveyRepository(IDbContextFactory<SurveysDbContext> factory) : ISurveyRepository
{
    public async Task<Survey?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Order applied by the service/consumer (display-sort lives above the repository).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Surveys
            .AsNoTracking()
            .Include(s => s.Questions)
                .ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IReadOnlyList<Survey>> GetAllSummariesAsync(CancellationToken ct = default)
    {
        // No display ordering here — the admin controller sorts the index (hard rule).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Surveys
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task AddAsync(Survey survey, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Surveys.Add(survey);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Survey survey, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.Surveys
            .Include(s => s.Questions)
                .ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(s => s.Id == survey.Id, ct);
        if (existing is null) return;

        // Scalars
        existing.Title = survey.Title;
        existing.Intro = survey.Intro;
        existing.ThankYou = survey.ThankYou;
        existing.InvitationEmailSubject = survey.InvitationEmailSubject;
        existing.InvitationEmailMessage = survey.InvitationEmailMessage;
        existing.DefaultCulture = survey.DefaultCulture;
        existing.AllowAnonymous = survey.AllowAnonymous;
        existing.IsAsociadoVote = survey.IsAsociadoVote;
        // Status is owned by Open/Close (SetStatusAsync) — authoring updates never change it.
        existing.OpensAt = survey.OpensAt;
        existing.ClosesAt = survey.ClosesAt;
        existing.AudienceType = survey.AudienceType;
        existing.AudienceTeamId = survey.AudienceTeamId;
        existing.AudienceLoggedInSince = survey.AudienceLoggedInSince;
        existing.PublicSlug = survey.PublicSlug;
        existing.UpdatedAt = survey.UpdatedAt;

        ReconcileQuestions(ctx, existing, survey);

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<SurveyStatus?> GetStatusAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Surveys
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => (SurveyStatus?)s.Status)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyAnswers
            .AsNoTracking()
            .AnyAsync(answer => answer.Response.SurveyId == surveyId, ct);
    }

    public async Task SetStatusAsync(Guid id, SurveyStatus status, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var survey = await ctx.Surveys.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (survey is null) return;
        survey.Status = status;
        survey.UpdatedAt = updatedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetInvitedCountsBySurveyAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.SurveyInvitations
            .AsNoTracking()
            .Where(i => i.SentAt != null)
            .GroupBy(i => i.SurveyId)
            .Select(g => new { SurveyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SurveyId, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetResponseCountsBySurveyAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.SurveyResponses
            .AsNoTracking()
            .Where(r => r.SubmittedAt != null)
            .GroupBy(r => r.SurveyId)
            .Select(g => new { SurveyId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SurveyId, r => r.Count);
    }

    public async Task<IReadOnlySet<Guid>> GetInvitedUserIdsAsync(Guid surveyId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = await ctx.SurveyInvitations
            .AsNoTracking()
            .Where(i => i.SurveyId == surveyId && i.SentAt != null)
            .Select(i => i.UserId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<SurveyInvitation>> GetInvitationsAsync(Guid surveyId, CancellationToken ct = default)
    {
        // No display ordering here — the controller sorts the Send status list (hard rule).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyInvitations
            .AsNoTracking()
            .Where(i => i.SurveyId == surveyId)
            .ToListAsync(ct);
    }

    public async Task AddInvitationAndSaveAsync(SurveyInvitation invitation, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.SurveyInvitations.Add(invitation);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<SurveyInvitation> GetOrCreateParticipationAsync(
        Guid surveyId, Guid userId, Instant createdAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.SurveyInvitations
            .FirstOrDefaultAsync(i => i.SurveyId == surveyId && i.UserId == userId, ct);
        if (existing is not null) return existing;

        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
            CreatedAt = createdAt,
        };
        ctx.SurveyInvitations.Add(participation);

        try
        {
            await ctx.SaveChangesAsync(ct);
            return participation;
        }
        catch (DbUpdateException)
        {
            // A concurrent Start POST may have won the unique (SurveyId, UserId) insert race.
            // Re-read the canonical row; unrelated persistence failures still propagate.
            ctx.ChangeTracker.Clear();
            var raced = await ctx.SurveyInvitations
                .FirstOrDefaultAsync(i => i.SurveyId == surveyId && i.UserId == userId, ct);
            if (raced is not null) return raced;
            throw;
        }
    }

    public async Task UpdateInvitationStatusAsync(Guid id, EmailOutboxStatus status, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var invitation = await ctx.SurveyInvitations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invitation is null) return;
        if (status == EmailOutboxStatus.Queued && invitation.SentAt is null)
        {
            invitation.SentAt = at;
        }
        invitation.LatestEmailStatus = status;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SurveyInvitation>> GetInvitationsDueForReminderAsync(Instant cutoff, CancellationToken ct = default)
    {
        // No display ordering — the service sweeps the result (hard rule). Uses the
        // (SurveyId, Completed, SentAt) index. Joins to the survey's status (repo owns both tables).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyInvitations
            .AsNoTracking()
            .Where(i => !i.Completed
                        && i.ReminderSentAt == null
                        && i.SentAt != null
                        && i.SentAt <= cutoff
                        && ctx.Surveys.Any(s => s.Id == i.SurveyId && s.Status == SurveyStatus.Open))
            .ToListAsync(ct);
    }

    public async Task SetReminderSentAsync(Guid invitationId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var invitation = await ctx.SurveyInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null) return;
        invitation.ReminderSentAt = at;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<SurveyInvitation?> GetInvitationByIdAsync(Guid invitationId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invitationId, ct);
    }

    public async Task<Guid?> GetIdByPublicSlugAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Surveys
            .AsNoTracking()
            .Where(s => s.PublicSlug == slug)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var survey = await ctx.Surveys.FirstOrDefaultAsync(s => s.Id == surveyId, ct);
        if (survey is null) return;
        survey.PublicStartedCount++;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<SurveyResponse?> GetDraftResponseAsync(Guid surveyId, Guid userId, CancellationToken ct = default)
    {
        // No display ordering here — answer order is reconstructed by question (caller/wizard).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .FirstOrDefaultAsync(
                r => r.SurveyId == surveyId
                     && r.UserId == userId
                     && r.Anonymity == ResponseAnonymity.Identified
                     && r.SubmittedAt == null,
                ct);
    }

    public async Task AddResponseAsync(SurveyResponse response, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.SurveyResponses.Add(response);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveDraftAnswersAsync(
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswer> answers,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var draft = await ctx.SurveyResponses
            .Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.Id == draftResponseId && r.SubmittedAt == null, ct);
        if (draft is null) return;

        ctx.SurveyAnswers.RemoveRange(draft.Answers);
        foreach (var answer in answers)
        {
            answer.Response = draft;
            ctx.SurveyAnswers.Add(answer);
        }

        draft.InputMethod = inputMethod;
        draft.Culture = culture;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddResponseWithAnswersAndSaveAsync(SurveyResponse response, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.SurveyResponses.Add(response);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task FinalizeIdentifiedResponseAsync(
        Guid invitationId,
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswer> answers,
        Instant submittedAt,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var invitation = await ctx.SurveyInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null)
            throw new InvalidOperationException("Survey participation not found.");
        if (invitation.Completed) return;

        var draft = await ctx.SurveyResponses
            .Include(r => r.Answers)
            .FirstAsync(
                r => r.Id == draftResponseId
                     && r.InvitationId == invitationId
                     && r.SubmittedAt == null,
                ct);

        ctx.SurveyAnswers.RemoveRange(draft.Answers);
        foreach (var answer in answers)
        {
            answer.Response = draft;
            ctx.SurveyAnswers.Add(answer);
        }

        draft.InputMethod = inputMethod;
        draft.Culture = culture;
        draft.SubmittedAt = submittedAt;
        invitation.Completed = true;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task FinalizeCompletionTrackedResponseAsync(
        Guid invitationId,
        Guid userId,
        SurveyResponse response,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var invitation = await ctx.SurveyInvitations
            .FirstOrDefaultAsync(
                i => i.Id == invitationId
                     && i.SurveyId == response.SurveyId
                     && i.UserId == userId,
                ct);
        if (invitation is null)
            throw new InvalidOperationException("Survey participation not found.");
        if (invitation.Completed) return;

        var draft = await ctx.SurveyResponses
            .FirstOrDefaultAsync(
                r => r.SurveyId == response.SurveyId
                     && r.UserId == userId
                     && r.Anonymity == ResponseAnonymity.Identified
                     && r.SubmittedAt == null,
                ct);
        if (draft is not null) ctx.SurveyResponses.Remove(draft);

        ctx.SurveyResponses.Add(response);
        invitation.Completed = true;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var invitation = await ctx.SurveyInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct);
        if (invitation is null) return;
        invitation.Started = true;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetResponsesForResultsAsync(Guid surveyId, CancellationToken ct = default)
    {
        // No display ordering here — aggregation/sorting lives in the service (hard rule).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.SurveyId == surveyId && r.SubmittedAt != null)
            .ToListAsync(ct);
    }

    public async Task<int> GetStartedInvitationCountAsync(Guid surveyId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyInvitations
            .AsNoTracking()
            .CountAsync(i => i.SurveyId == surveyId && i.Started, ct);
    }

    public async Task<IReadOnlyList<SurveyResponse>> GetIdentifiedResponsesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // No display ordering here — the GDPR contributor shapes/orders the payload (hard rule).
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SurveyResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.UserId == userId
                        && r.Anonymity == ResponseAnonymity.Identified
                        && r.SubmittedAt != null)
            .ToListAsync(ct);
    }

    public async Task<int> AnonymizeResponsesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var responses = await ctx.SurveyResponses
            .Where(r => r.UserId == userId)
            .ToListAsync(ct);

        foreach (var response in responses)
        {
            // UserId / InvitationId / Anonymity are init-only — write through the tracked entry.
            var entry = ctx.Entry(response);
            entry.Property(nameof(SurveyResponse.UserId)).CurrentValue = null;
            entry.Property(nameof(SurveyResponse.InvitationId)).CurrentValue = null;
            entry.Property(nameof(SurveyResponse.Anonymity)).CurrentValue = ResponseAnonymity.Anonymous;
        }

        var invitations = await ctx.SurveyInvitations
            .Where(i => i.UserId == userId)
            .ToListAsync(ct);
        ctx.SurveyInvitations.RemoveRange(invitations);

        return await ctx.SaveChangesAsync(ct);
    }

    /// <summary>Reconciles the persisted question/option graph against the incoming survey by id — removes dropped, updates kept, inserts new.</summary>
    private static void ReconcileQuestions(SurveysDbContext ctx, Survey existing, Survey incoming)
    {
        var incomingQuestionIds = incoming.Questions.Select(q => q.Id).ToHashSet();

        foreach (var dropped in existing.Questions.Where(q => !incomingQuestionIds.Contains(q.Id)).ToList())
        {
            ctx.SurveyQuestions.Remove(dropped);
        }

        foreach (var incomingQuestion in incoming.Questions)
        {
            var keptQuestion = existing.Questions.FirstOrDefault(q => q.Id == incomingQuestion.Id);
            if (keptQuestion is null)
            {
                // Builder questions carry client-generated ids so newly authored questions can
                // participate in branching before their first save. With a non-default key, adding
                // through a tracked collection makes EF assume the entity already exists and issue
                // an UPDATE, which then fails optimistic concurrency because there is no row yet.
                // The id comparison above is the source of truth for persistence, so explicitly
                // classify an unmatched question (and its option graph) as new.
                ctx.SurveyQuestions.Add(incomingQuestion);
                continue;
            }

            keptQuestion.PageNumber = incomingQuestion.PageNumber;
            keptQuestion.Order = incomingQuestion.Order;
            keptQuestion.Type = incomingQuestion.Type;
            keptQuestion.Prompt = incomingQuestion.Prompt;
            keptQuestion.HelpText = incomingQuestion.HelpText;
            keptQuestion.IsRequired = incomingQuestion.IsRequired;
            keptQuestion.RatingMin = incomingQuestion.RatingMin;
            keptQuestion.RatingMax = incomingQuestion.RatingMax;
            keptQuestion.RatingMinLabel = incomingQuestion.RatingMinLabel;
            keptQuestion.RatingMaxLabel = incomingQuestion.RatingMaxLabel;
            keptQuestion.GridSelectionMode = incomingQuestion.GridSelectionMode;
            keptQuestion.GridRows = incomingQuestion.GridRows;
            keptQuestion.InformationImages = incomingQuestion.InformationImages;
            keptQuestion.RankedSettings = incomingQuestion.RankedSettings;
            keptQuestion.RankedUnavailableOptionValues = incomingQuestion.RankedUnavailableOptionValues;
            keptQuestion.ShowIf = incomingQuestion.ShowIf;

            ReconcileOptions(ctx, keptQuestion, incomingQuestion);
        }
    }

    private static void ReconcileOptions(SurveysDbContext ctx, SurveyQuestion existing, SurveyQuestion incoming)
    {
        var incomingOptionIds = incoming.Options.Select(o => o.Id).ToHashSet();

        foreach (var dropped in existing.Options.Where(o => !incomingOptionIds.Contains(o.Id)).ToList())
        {
            ctx.SurveyQuestionOptions.Remove(dropped);
        }

        foreach (var incomingOption in incoming.Options)
        {
            var keptOption = existing.Options.FirstOrDefault(o => o.Id == incomingOption.Id);
            if (keptOption is null)
            {
                // Options also receive client-generated ids in the builder. An unmatched id is a
                // new row even though its key is already populated.
                ctx.SurveyQuestionOptions.Add(incomingOption);
                continue;
            }

            keptOption.Order = incomingOption.Order;
            keptOption.Value = incomingOption.Value;
            keptOption.Label = incomingOption.Label;
        }
    }
}
