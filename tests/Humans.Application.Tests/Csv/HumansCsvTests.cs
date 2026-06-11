using System.Text;
using AwesomeAssertions;
using CsvHelper;
using Humans.Application.Csv;
using Xunit;

namespace Humans.Application.Tests.Csv;

/// <summary>
/// Pins the app-wide CSV conventions every export now relies on: UTF-8 BOM,
/// CRLF records, invariant formatting, RFC 4180 conditional quoting, and
/// OWASP CSV-injection escaping (memory/code/csv-use-csvhelper.md).
/// </summary>
public sealed class HumansCsvTests
{
    [HumansFact]
    public void WriteBytes_EmitsUtf8BomAndCrlfRecords()
    {
        var bytes = HumansCsv.WriteBytes(csv => csv.WriteRow("a", "b"));

        bytes[..3].Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes).Should().EndWith("a,b\r\n");
    }

    [HumansFact]
    public void WriteRow_NullBecomesEmptyCell_AndNumbersFormatInvariant()
    {
        Text(csv => csv.WriteRow("x", null, 1234.5m, 60)).Should().Be("x,,1234.5,60\r\n");
    }

    [HumansFact]
    public void WriteRow_QuotesOnlyWhenNeeded_AndDoublesEmbeddedQuotes()
    {
        Text(csv => csv.WriteRow("plain", "a,b", "say \"hi\"")).Should().Be("plain,\"a,b\",\"say \"\"hi\"\"\"\r\n");
    }

    [HumansFact]
    public void WriteRow_EmbeddedNewline_StaysInsideQuotedField()
    {
        Text(csv => csv.WriteRow("line one\nline two")).Should().StartWith("\"line one\nline two\"");
    }

    [HumansTheory]
    [InlineData("=1+2")]
    [InlineData("+34 600 000 000")]
    [InlineData("-2+3+cmd")]
    [InlineData("@user")]
    [InlineData("\ttabbed")] // \t and \r are in our injection set; CsvHelper's default omits them
    public void WriteRow_EscapesOwaspFormulaPrefixes(string payload)
    {
        var cell = Text(csv => csv.WriteRow(payload)).TrimEnd('\r', '\n');

        cell.TrimStart('"').Should().StartWith("'");
    }

    [HumansFact]
    public void ReadConfig_MatchesHeadersCaseAndWhitespaceInsensitively_AndDetectsSemicolons()
    {
        using var reader = new StringReader(" NAME ;Count\nAna;3\n");
        using var csv = new CsvReader(reader, HumansCsv.ReadConfig());

        csv.Read();
        csv.ReadHeader();
        csv.Read();

        csv.GetField("name").Should().Be("Ana");
        csv.GetField(1).Should().Be("3");
    }

    private static string Text(Action<CsvWriter> write) =>
        Encoding.UTF8.GetString(HumansCsv.WriteBytes(write)).TrimStart('﻿');
}
