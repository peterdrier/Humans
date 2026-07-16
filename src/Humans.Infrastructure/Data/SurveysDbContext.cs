using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Surveys;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Surveys section
/// (nobodies-collective/Humans#858): maps only <c>surveys</c>,
/// <c>survey_questions</c>, <c>survey_question_options</c>,
/// <c>survey_invitations</c>, <c>survey_responses</c> and <c>survey_answers</c>,
/// with its own <c>__EFMigrationsHistory_Surveys</c> table and migrations under
/// <c>Migrations/Surveys/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// </remarks>
internal sealed class SurveysDbContext(DbContextOptions<SurveysDbContext> options)
    : DbContext(options)
{
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<SurveyQuestion> SurveyQuestions => Set<SurveyQuestion>();
    public DbSet<SurveyQuestionOption> SurveyQuestionOptions => Set<SurveyQuestionOption>();
    public DbSet<SurveyInvitation> SurveyInvitations => Set<SurveyInvitation>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyAnswer> SurveyAnswers => Set<SurveyAnswer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new SurveyConfiguration());
        builder.ApplyConfiguration(new SurveyQuestionConfiguration());
        builder.ApplyConfiguration(new SurveyQuestionOptionConfiguration());
        builder.ApplyConfiguration(new SurveyInvitationConfiguration());
        builder.ApplyConfiguration(new SurveyResponseConfiguration());
        builder.ApplyConfiguration(new SurveyAnswerConfiguration());
    }
}
