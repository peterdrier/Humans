using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Infrastructure.Services.Mailer;
using NodaTime;

namespace Humans.Application.Tests.Services.Mailer;

/// <summary>
/// Pins <see cref="MailerLiteSubscriberConverter"/> against a verbatim
/// MailerLite v2 response captured from the live <c>GET /api/subscribers/{email}</c>
/// endpoint. Without this test the FirstName/LastName mapping silently regresses
/// (names live under <c>fields.*</c>, not top-level).
/// </summary>
public class MailerLiteSubscriberConverterTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new MailerLiteSubscriberConverter() }
    };

    [HumansFact]
    public void Maps_TopLevelFields_From_V2_Response()
    {
        const string json = """
        {
          "id": "187280337252910686",
          "email": "mail@example.com",
          "status": "active",
          "source": "webform",
          "subscribed_at": "2026-05-12 16:47:51",
          "unsubscribed_at": null,
          "opted_in_at": "2026-05-12 16:47:51",
          "fields": { "name": null, "last_name": null }
        }
        """;
        var s = JsonSerializer.Deserialize<MailerLiteSubscriber>(json, Opts)!;
        s.Id.Should().Be("187280337252910686");
        s.Email.Should().Be("mail@example.com");
        s.Status.Should().Be("active");
        s.Source.Should().Be("webform");
        s.SubscribedAt.Should().Be(Instant.FromUtc(2026, 5, 12, 16, 47, 51));
        s.UnsubscribedAt.Should().BeNull();
        s.OptedInAt.Should().Be(Instant.FromUtc(2026, 5, 12, 16, 47, 51));
    }

    [HumansFact]
    public void Maps_FirstName_LastName_From_Nested_Fields_Object()
    {
        const string json = """
        {
          "id": "1",
          "email": "x@y.z",
          "status": "active",
          "fields": { "name": "Anne", "last_name": "Lister" }
        }
        """;
        var s = JsonSerializer.Deserialize<MailerLiteSubscriber>(json, Opts)!;
        s.FirstName.Should().Be("Anne");
        s.LastName.Should().Be("Lister");
    }

    [HumansFact]
    public void Tolerates_Missing_Fields_Object()
    {
        const string json = """
        { "id": "1", "email": "x@y.z", "status": "active" }
        """;
        var s = JsonSerializer.Deserialize<MailerLiteSubscriber>(json, Opts)!;
        s.FirstName.Should().BeNull();
        s.LastName.Should().BeNull();
    }
}
