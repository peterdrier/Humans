using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Email.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

/// <summary>Monitoring reads the "smtp" key and "external" tag: keep both.</summary>
internal sealed class SectionHealthChecks : ISectionHealthChecks
{
    public void AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<SmtpHealthCheck>("smtp", tags: [HealthCheckTags.External]);
    }
}
