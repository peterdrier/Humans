using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Surveys;

/// <summary>EF-backed <see cref="ISurveyRepository"/>. Declared <c>partial</c>; later phases add their reads/writes.</summary>
internal sealed partial class SurveyRepository(IDbContextFactory<HumansDbContext> factory) : ISurveyRepository
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
        existing.DefaultCulture = survey.DefaultCulture;
        existing.AllowAnonymous = survey.AllowAnonymous;
        // Status is owned by Open/Close (SetStatusAsync) — authoring updates never change it.
        existing.OpensAt = survey.OpensAt;
        existing.ClosesAt = survey.ClosesAt;
        existing.AudienceType = survey.AudienceType;
        existing.AudienceTeamId = survey.AudienceTeamId;
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

    /// <summary>Reconciles the persisted question/option graph against the incoming survey by id — removes dropped, updates kept, inserts new.</summary>
    private static void ReconcileQuestions(HumansDbContext ctx, Survey existing, Survey incoming)
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
                existing.Questions.Add(incomingQuestion);
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
            keptQuestion.ShowIf = incomingQuestion.ShowIf;

            ReconcileOptions(ctx, keptQuestion, incomingQuestion);
        }
    }

    private static void ReconcileOptions(HumansDbContext ctx, SurveyQuestion existing, SurveyQuestion incoming)
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
                existing.Options.Add(incomingOption);
                continue;
            }

            keptOption.Order = incomingOption.Order;
            keptOption.Value = incomingOption.Value;
            keptOption.Label = incomingOption.Label;
        }
    }
}
