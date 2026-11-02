using System.Text;
using AwesomeAssertions;
using CsvHelper;
using Humans.Base.Csv;
using Xunit;

namespace Humans.Base.Tests.Csv;

/// <summary>
/// Pins the app-wide CSV conventions every export relies on: UTF-8 BOM,
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

    [HumansFact]
    public void WriteBlankLine_SeparatesSectionsWithAnEmptyLine()
    {
        Text(csv =>
        {
            csv.WriteRow("a", "b");
            csv.WriteBlankLine();
            csv.WriteRow("c");
        }).Should().Be("a,b\r\n\r\nc\r\n");
    }

    [HumansTheory]
    [InlineData("=1+2")]
    [InlineData("+34 600 000 000")]
    [InlineData("-2+3+cmd")]
    [InlineData("@user")]
    [InlineData("\ttabbed")]
    public void WriteRow_EscapesOwaspFormulaPrefixes(string payload)
    {
        var cell = Text(csv => csv.WriteRow(payload)).TrimEnd('\r', '\n');

        cell.TrimStart('"').Should().StartWith("'");
    }

    [HumansFact]
    public void ReadConfig_MatchesHeadersCaseAndWhitespaceInsensitively_AndDetectsSemicolons()
    {
        using var csv = CsvReader.FromText(" NAME ;Count\nAna;3\n", HumansCsv.ReadConfig());

        csv.Read();

        csv.GetString("name").Should().Be("Ana");
        csv.GetString(1).Should().Be("3");
    }

    private static string Text(Action<CsvWriter> write) =>
        Encoding.UTF8.GetString(HumansCsv.WriteBytes(write)).TrimStart('﻿');
}
