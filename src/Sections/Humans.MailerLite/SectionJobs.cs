using Humans.Base.Interfaces;
using Humans.MailerLite.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.MailerLite;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Opt-in: with MailerLite:AudienceSyncCron unset the job is contributed but not scheduled, and admins
        // sync from /MailerLite/Admin. Set e.g. "0 6 * * *" to enable.
        var configuration = services.GetRequiredService<IConfiguration>();
        var cron = configuration.GetValue<string>("MailerLite:AudienceSyncCron") ?? string.Empty;

        yield return new RecurringJobDescriptor("mailerlite-audience-sync", typeof(MailerLiteAudienceSyncJob), cron);
    }
}
