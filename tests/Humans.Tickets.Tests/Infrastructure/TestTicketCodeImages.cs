using Humans.Base.Configuration;
using Humans.Tickets.Services.Delivery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Tests.Infrastructure;

internal static class TestTicketCodeImages
{
    public static TicketCodeImages Create(IDataProtectionProvider? provider = null) =>
        new(provider ?? new EphemeralDataProtectionProvider(),
            Options.Create(new EmailSettings { BaseUrl = TestTicketsEmails.BaseUrl }));
}
