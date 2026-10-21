using AwesomeAssertions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

public sealed class HtmlPlainTextConverterTests
{
    [HumansTheory]
    [InlineData("<h2>Message from Bob</h2><p>Hi Alice,</p>", "Message from Bob\n\nHi Alice,")]
    [InlineData("<p><a href=\"https://humans.example/verify?t=A%2FB&amp;email=a%40example.com\">Verify email</a></p>",
        "Verify email (https://humans.example/verify?t=A%2FB&email=a%40example.com)")]
    [InlineData("<a class='button' href='https://humans.example/survey'><strong>Answer</strong> now</a>",
        "Answer now (https://humans.example/survey)")]
    [InlineData("<a title='Contains > and href=other' href='https://humans.example/real'>Open</a>",
        "Open (https://humans.example/real)")]
    [InlineData("<a href='mailto:alice@example.com'>Contact Alice</a>", "Contact Alice (mailto:alice@example.com)")]
    [InlineData("<a href='https://humans.example'>https://humans.example</a>", "https://humans.example")]
    [InlineData("<H2>Title</H2><P>First<BR>Second</P><UL><LI>Item</LI></UL>", "Title\n\nFirst\nSecond\n\nItem")]
    [InlineData("<p>Plain &amp; encoded &amp;lt;text&amp;gt;</p><a id='anchor'>Label</a>",
        "Plain & encoded &lt;text&gt;\n\nLabel")]
    [InlineData("<table><tr><th>Name</th><th>Shift</th></tr><tr><td>Alice</td><td>Gate</td></tr></table>",
        "Name\tShift\nAlice\tGate")]
    [InlineData("<table>\n<caption>Roster</caption>\n<tr>\n<th> Name </th>\n<th>Shift</th>\n</tr>\n<tr><td>Alice</td><td><a href='https://humans.example/shift'>Gate</a></td></tr>\n</table><p>See you there.</p>",
        "Roster\nName\tShift\nAlice\tGate (https://humans.example/shift)\n\nSee you there.")]
    [InlineData("Before<table><tr><td>First</td><td>Second</td></tr></table>After",
        "Before\n\nFirst\tSecond\n\nAfter")]
    public void Convert_PreservesReadableBlocksAndActionDestinations(string html, string expected)
    {
        HtmlPlainTextConverter.Convert(html).Should().Be(expected);
    }
}
