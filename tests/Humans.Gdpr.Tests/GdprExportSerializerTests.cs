using System.Text;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;

namespace Humans.Gdpr.Tests;

public class GdprExportSerializerTests
{
    [HumansFact]
    public void Serialize_preserves_download_document_format()
    {
        var export = new GdprExport(
            "2026-04-15T10:30:00Z",
            Guid.Parse("00000000-0000-0000-0000-000000000005"),
            [Guid.Parse("00000000-0000-0000-0000-000000000003")],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Profile"] = new { Name = "Nobody", Status = DayOfWeek.Monday, Missing = (string?)null },
                ["ContactFields"] = Array.Empty<object>()
            });

        var bytes = GdprExportSerializer.Serialize(export);

        Encoding.UTF8.GetString(bytes).Should().Be("""
            {
              "ExportedAt": "2026-04-15T10:30:00Z",
              "UserId": "00000000-0000-0000-0000-000000000005",
              "MergedFromUserIds": [
                "00000000-0000-0000-0000-000000000003"
              ],
              "Profile": {
                "Name": "Nobody",
                "Status": "Monday"
              },
              "ContactFields": []
            }
            """);
    }
}
