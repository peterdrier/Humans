using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Surveys.Data;

internal sealed class SurveysDbContextFactory : SectionDesignTimeDbContextFactory<SurveysDbContext>
{
    protected override SurveysDbContext Create(DbContextOptions<SurveysDbContext> options) => new(options);
}
