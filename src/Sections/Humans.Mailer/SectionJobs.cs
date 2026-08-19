using Humans.Base.Interfaces;
using Humans.Mailer.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Mailer;

/// <summary>Mailer's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // MailerLite:AudienceSyncCron is opt-in. When empty/unset the job keeps its place in
        // the contributed set but is not scheduled — admins still trigger syncs on demand via
        // the /Mailer/Admin "Push Now" button. Set to e.g. "0 6 * * *" to enable.
        var configuration = services.GetRequiredService<IConfiguration>();
        var cron = configuration.GetValue<string>("MailerLite:AudienceSyncCron") ?? string.Empty;

        yield return new RecurringJobDescriptor("mailer-audience-sync", typeof(MailerAudienceSyncJob), cron);
    }
}
