using System.Text.Json;
using AwesomeAssertions;
using Humans.Infrastructure.Services.Mailer;
using NodaTime;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteDateConverterTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Converters = { new MailerLiteDateConverter() }
    };

    private sealed record Wrap(Instant? At);

    [HumansFact]
    public void Reads_MailerLiteSpaceSeparatedDateAsUtc()
    {
        var json = """{"At":"2026-05-12 09:30:00"}""";
        var wrap = JsonSerializer.Deserialize<Wrap>(json, Opts);
        wrap!.At.Should().Be(Instant.FromUtc(2026, 5, 12, 9, 30, 0));
    }

    [HumansFact]
    public void Reads_NullAsNull()
    {
        var json = """{"At":null}""";
        var wrap = JsonSerializer.Deserialize<Wrap>(json, Opts);
        wrap!.At.Should().BeNull();
    }

    [HumansFact]
    public void Reads_MissingFieldAsNull()
    {
        var json = "{}";
        var wrap = JsonSerializer.Deserialize<Wrap>(json, Opts);
        wrap!.At.Should().BeNull();
    }
}
