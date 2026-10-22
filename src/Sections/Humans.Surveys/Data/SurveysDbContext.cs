using Humans.Surveys.Domain;
using Humans.Surveys.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Surveys.Data;

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
