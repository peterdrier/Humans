using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Web.TagHelpers;

/// <summary>
/// Renders a <c>&lt;textarea&gt;</c> upgraded with the EasyMDE WYSIWYG-ish Markdown
/// editor (toolbar, side-by-side preview, syntax help).
///
/// Usage:
///   <c>&lt;markdown-editor asp-for="BlurbLong" rows="6" /&gt;</c>
///   <c>&lt;markdown-editor name="emailBodyTemplate" value="@ViewBag.EmailBodyTemplate" rows="10" required="true" /&gt;</c>
///
/// Server-side Markdown rendering is unchanged: the textarea posts raw Markdown
/// to the controller exactly as before, and <c>HtmlHelperExtensions.SanitizedMarkdown</c>
/// still owns rendering. This tag helper is a pure client-side editing upgrade.
///
/// Graceful degradation: if EasyMDE JS fails to load, the bare textarea still
/// submits Markdown as plain text.
///
/// Loads EasyMDE assets from jsDelivr with SRI integrity on first use per request,
/// and auto-emits the shared <c>_MarkdownHelp</c> modal so the toolbar "?" button
/// always has something to open — callers do not need to include the partial.
/// </summary>
[HtmlTargetElement("markdown-editor", Attributes = "asp-for", TagStructure = TagStructure.WithoutEndTag)]
[HtmlTargetElement("markdown-editor", Attributes = "name", TagStructure = TagStructure.WithoutEndTag)]
[HtmlTargetElement("markdown-editor", TagStructure = TagStructure.WithoutEndTag)]
public class MarkdownEditorTagHelper(
    IHtmlGenerator htmlGenerator,
    IHttpContextAccessor httpContextAccessor,
    IHtmlHelper htmlHelper) : TagHelper
{
    // EasyMDE 2.21.0 — MIT, actively maintained fork of SimpleMDE.
    private const string EasyMdeCssHref = "https://cdn.jsdelivr.net/npm/easymde@2.21.0/dist/easymde.min.css";
    private const string EasyMdeCssIntegrity = "sha384-ZoLYv3S+AsZX+zhbN1D1+WPpc8f+DmLfxfgw+qn0Nq8wJPOYQQXEW5ZrRhcGozlG";
    private const string EasyMdeJsSrc = "https://cdn.jsdelivr.net/npm/easymde@2.21.0/dist/easymde.min.js";
    private const string EasyMdeJsIntegrity = "sha384-mTM6vzy+/UiHrMBClNGViM9qEv0/26iCGqpJKhSzdnjrxbKjO3vkT62ujXQ8B5iv";

    private const string AssetsEmittedKey = "MarkdownEditor.AssetsEmitted";
    private const string InstanceCounterKey = "MarkdownEditor.InstanceCounter";

    /// <summary>Standard <c>asp-for</c> model binding (preferred).</summary>
    [HtmlAttributeName("asp-for")]
    public ModelExpression? For { get; set; }

    /// <summary>Explicit <c>name</c> attribute when <c>asp-for</c> is not used.</summary>
    [HtmlAttributeName("name")]
    public string? Name { get; set; }

    /// <summary>Explicit value when <c>asp-for</c> is not used.</summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    /// <summary>Optional explicit id; defaults to the asp-for/name expression.</summary>
    [HtmlAttributeName("id")]
    public string? Id { get; set; }

    /// <summary>Textarea rows (height before EasyMDE upgrade). Defaults to 6.</summary>
    [HtmlAttributeName("rows")]
    public int Rows { get; set; } = 6;

    /// <summary>Additional CSS classes for the textarea.</summary>
    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    /// <summary>HTML <c>required</c> attribute.</summary>
    [HtmlAttributeName("required")]
    public bool Required { get; set; }

    /// <summary>HTML <c>maxlength</c> attribute (0 = unset).</summary>
    [HtmlAttributeName("maxlength")]
    public int MaxLength { get; set; }

    /// <summary>HTML <c>placeholder</c> attribute.</summary>
    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        output.TagName = null; // we replace the wrapper entirely
        output.TagMode = TagMode.StartTagAndEndTag;

        var httpContext = httpContextAccessor.HttpContext;
        var cssClasses = string.IsNullOrWhiteSpace(CssClass) ? "form-control" : "form-control " + CssClass;

        // Build the textarea — prefer asp-for via IHtmlGenerator (so ModelState/validation work identically),
        // fall back to a plain textarea bound by name + value.
        TagBuilder textarea;
        if (For is not null)
        {
            textarea = htmlGenerator.GenerateTextArea(
                ViewContext,
                For.ModelExplorer,
                For.Name,
                Rows,
                columns: 0,
                htmlAttributes: null) ?? new TagBuilder("textarea");
        }
        else
        {
            textarea = new TagBuilder("textarea");
            if (!string.IsNullOrEmpty(Name))
            {
                textarea.Attributes["name"] = Name;
            }
            textarea.InnerHtml.Append(Value ?? string.Empty);
        }

        // Ensure a stable, unique id so the init script can target it.
        var elementId = Id;
        if (string.IsNullOrEmpty(elementId))
        {
            if (textarea.Attributes.TryGetValue("id", out var existingId) && !string.IsNullOrEmpty(existingId))
            {
                elementId = existingId;
            }
            else if (!string.IsNullOrEmpty(Name))
            {
                elementId = Name;
            }
            else if (For is not null)
            {
                elementId = TagBuilder.CreateSanitizedId(For.Name, "_");
            }
            else
            {
                elementId = "markdown-editor";
            }
        }

        // Disambiguate when multiple editors on the page share the same id (e.g. role-description
        // textareas in a loop). We append a per-request instance counter so EasyMDE attaches to
        // the right element.
        var counter = (httpContext?.Items[InstanceCounterKey] as int?) ?? 0;
        counter++;
        if (httpContext is not null)
        {
            httpContext.Items[InstanceCounterKey] = counter;
        }
        var uniqueId = $"{elementId}-mde-{counter.ToString(CultureInfo.InvariantCulture)}";
        textarea.Attributes["id"] = uniqueId;

        // Force class/rows/optional attributes onto the textarea regardless of asp-for shape.
        textarea.Attributes["class"] = cssClasses;
        textarea.Attributes["rows"] = Rows.ToString(CultureInfo.InvariantCulture);
        if (Required)
        {
            textarea.Attributes["required"] = "required";
        }
        if (MaxLength > 0)
        {
            textarea.Attributes["maxlength"] = MaxLength.ToString(CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrEmpty(Placeholder))
        {
            textarea.Attributes["placeholder"] = Placeholder;
        }

        // Write the textarea.
        using (var writer = new StringWriter(CultureInfo.InvariantCulture))
        {
            textarea.WriteTo(writer, HtmlEncoder.Default);
            output.Content.AppendHtml(writer.ToString());
        }

        // Read CSP nonce from HttpContext.Items (set by CspNonceMiddleware). Raw HTML written via
        // AppendHtml bypasses NonceTagHelper, so we stamp the nonce ourselves on every <script> we emit.
        var nonce = httpContext?.Items["CspNonce"] as string;
        var nonceAttr = nonce is not null
            ? $" nonce=\"{HtmlEncoder.Default.Encode(nonce)}\""
            : string.Empty;

        // Per-request, emit CSS/JS asset tags exactly once. After that only the init script.
        var alreadyEmitted = httpContext is not null && httpContext.Items.ContainsKey(AssetsEmittedKey);
        if (!alreadyEmitted)
        {
            if (httpContext is not null)
            {
                httpContext.Items[AssetsEmittedKey] = true;
            }

            output.Content.AppendHtml(
                $"<link rel=\"stylesheet\" href=\"{EasyMdeCssHref}\" " +
                $"integrity=\"{EasyMdeCssIntegrity}\" crossorigin=\"anonymous\">");
            output.Content.AppendHtml(
                $"<script src=\"{EasyMdeJsSrc}\" " +
                $"integrity=\"{EasyMdeJsIntegrity}\" crossorigin=\"anonymous\" defer{nonceAttr}></script>");

            // Force the toolbar to a single horizontally-scrollable row instead of wrapping
            // into 2–3 ragged rows when the container is narrower than the full button strip.
            var styleNonceAttr = nonce is not null
                ? $" nonce=\"{HtmlEncoder.Default.Encode(nonce)}\""
                : string.Empty;
            output.Content.AppendHtml(
                $"<style{styleNonceAttr}>.editor-toolbar{{white-space:nowrap;overflow-x:auto;overflow-y:hidden;}}</style>");

            // Render the help modal partial once per request so callers don't have to.
            ((IViewContextAware)htmlHelper).Contextualize(ViewContext);
            var modalHtml = await htmlHelper.PartialAsync("_MarkdownHelp");
            using var modalWriter = new StringWriter(CultureInfo.InvariantCulture);
            modalHtml.WriteTo(modalWriter, HtmlEncoder.Default);
            output.Content.AppendHtml(modalWriter.ToString());
        }

        // Encode the textarea id for use inside a JS string literal.
        var jsIdLiteral = JavaScriptEncoder.Default.Encode(uniqueId);

        // Inline init script — defers EasyMDE construction until the asset script has loaded.
        // We stamp the CSP nonce directly because this <script> is emitted as raw HTML via
        // AppendHtml and never re-parsed by NonceTagHelper.
        var initScript = $@"<script{nonceAttr}>
(function() {{
    function initMde() {{
        if (typeof EasyMDE === 'undefined') {{ return false; }}
        var el = document.getElementById('{jsIdLiteral}');
        if (!el || el.dataset.mdeInitialized === 'true') {{ return true; }}
        el.dataset.mdeInitialized = 'true';
        try {{
            new EasyMDE({{
                element: el,
                autoDownloadFontAwesome: false,
                spellChecker: false,
                status: false,
                forceSync: true,
                minHeight: '120px',
                toolbar: [
                    'bold', 'italic', 'strikethrough', 'heading',
                    '|',
                    'unordered-list', 'ordered-list',
                    {{ name: 'task-list', action: function(editor) {{
                        var cm = editor.codemirror;
                        var sel = cm.getSelection() || 'task';
                        cm.replaceSelection('- [ ] ' + sel);
                    }}, className: 'fa-solid fa-square-check', title: 'Task list' }},
                    'quote',
                    '|',
                    'code', 'horizontal-rule', 'link',
                    {{ name: 'insert-table', action: EasyMDE.drawTable, className: 'fa-solid fa-table', title: 'Insert Table' }},
                    '|',
                    'preview',
                    {{ name: 'guide', action: function() {{
                        var modalEl = document.getElementById('markdownHelpModal');
                        if (!modalEl || typeof bootstrap === 'undefined') {{ return; }}
                        bootstrap.Modal.getOrCreateInstance(modalEl).show();
                    }}, className: 'fa-solid fa-circle-question', title: 'Markdown help' }}
                ]
            }});
        }} catch (e) {{
            // Leave the bare textarea in place on failure.
            console.warn('EasyMDE initialization failed', e);
        }}
        return true;
    }}

    if (!initMde()) {{
        var attempts = 0;
        var iv = setInterval(function() {{
            attempts++;
            if (initMde() || attempts > 50) {{ clearInterval(iv); }}
        }}, 100);
    }}
}})();
</script>";
        output.Content.AppendHtml(initScript);
    }
}
