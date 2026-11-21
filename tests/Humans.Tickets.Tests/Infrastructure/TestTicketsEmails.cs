using Humans.Base.Configuration;
using Humans.Tickets.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Tests.Infrastructure;

/// <summary>
/// A real <see cref="TicketsEmails"/> over test settings — the builder is sealed and has
/// no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Uses the real resx set, so assertions run against the English copy.
/// </summary>
internal static class TestTicketsEmails
{
    public const string BaseUrl = "https://humans.example";

    public static TicketsEmails Create() =>
        new(Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            new StringLocalizer<TicketsResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)),
            NullLogger<TicketsEmails>.Instance);
}
