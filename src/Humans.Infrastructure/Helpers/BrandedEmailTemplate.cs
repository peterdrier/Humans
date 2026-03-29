using System.Net;

namespace Humans.Infrastructure.Helpers;

public static class BrandedEmailTemplate
{
    public static string Wrap(string content, string baseUrl, string environmentName, string? unsubscribeUrl = null)
    {
        var isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        var envLabel = string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase)
            ? "QA"
            : environmentName.ToUpperInvariant();
        var unsubscribeFooter = unsubscribeUrl is not null
            ? $"""<p style="font-size: 11px; color: #8b7355; margin: 8px 0 0 0;"><a href="{WebUtility.HtmlEncode(unsubscribeUrl)}" style="color: #8b7355;">Unsubscribe from these emails</a></p>"""
            : "";

        var envBanner = isProduction
            ? ""
            : $"""
                <div style="background:#a0522d;color:#fff;text-align:center;font-size:11px;font-weight:700;letter-spacing:0.15em;text-transform:uppercase;padding:4px 0;">
                    {WebUtility.HtmlEncode(envLabel)} &bull; {WebUtility.HtmlEncode(envLabel)} &bull; {WebUtility.HtmlEncode(envLabel)}
                </div>
                """;

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <style>
                    body { font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0; }
                    h2 { color: #3d2b1f; font-family: 'Cormorant Garamond', Georgia, 'Times New Roman', serif; font-weight: 600; }
                    a { color: #8b6914; }
                    ul { padding-left: 20px; }
                </style>
            </head>
            <body style="font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0;">
            {{envBanner}}
            <div style="background: #3d2b1f; padding: 16px 24px; border-bottom: 3px solid #c9a96e;">
                <span style="font-family: Georgia, serif; font-size: 22px; color: #c9a96e; letter-spacing: 0.05em;">Humans</span>
                <span style="font-family: Georgia, serif; font-size: 12px; color: #8b7355; margin-left: 8px; letter-spacing: 0.1em;">NOBODIES COLLECTIVE</span>
            </div>
            <div style="padding: 28px 24px 20px 24px;">
            {{content}}
            </div>
            <div style="background: #f0e2c8; padding: 16px 24px; border-top: 1px solid #e8d4ab;">
                <p style="font-size: 12px; color: #6b5a4e; margin: 0; line-height: 1.5;">
                    Humans &mdash; Nobodies Collective<br>
                    <a href="{{baseUrl}}" style="color: #8b6914;">{{baseUrl}}</a>
                </p>
                {{unsubscribeFooter}}
            </div>
            </body>
            </html>
            """;
    }
}
