using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Preapprovals.Data;

internal sealed class PreapprovalsDbContextFactory : SectionDesignTimeDbContextFactory<PreapprovalsDbContext>
{
    protected override PreapprovalsDbContext Create(DbContextOptions<PreapprovalsDbContext> options) => new(options);
}
