using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Stripe.Data;

internal sealed class StripeDbContextFactory : SectionDesignTimeDbContextFactory<StripeDbContext>
{
    protected override StripeDbContext Create(DbContextOptions<StripeDbContext> options) => new(options);
}
