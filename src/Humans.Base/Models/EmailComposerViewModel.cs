namespace Humans.Base.Models;

/// <summary>
/// Drives the shared <c>_EmailComposer</c> partial: an EasyMDE-backed Markdown body (via the
/// <c>markdown-editor</c> tag helper), an optional subject field, and a "Preview" button that
/// shows the exact branded, sanitized send through <c>/Email/PreviewMarkdown</c>.
/// Every human-composed send form (profile message, camp contact, rota messages, survey
/// invitation, feedback reply, issue comment, campaign) renders through this one component
/// instead of its own subject/body/editor markup.
/// </summary>
public class EmailComposerViewModel
{
    /// <summary>Posted field name for the Markdown body (matches the receiving action's parameter/property).</summary>
    public required string BodyName { get; init; }
    public string? BodyValue { get; init; }
    public required string BodyLabel { get; init; }
    public int BodyRows { get; init; } = 8;
    public bool BodyRequired { get; init; } = true;
    public int BodyMaxLength { get; init; }
    public string? BodyHelpText { get; init; }
    public string? BodyPlaceholder { get; init; }

    /// <summary>When true, the body label is visually hidden (screen-reader only) — for compact,
    /// placeholder-driven composers like a reply box, where a full label would repeat the surrounding UI.</summary>
    public bool BodyLabelVisuallyHidden { get; init; }

    /// <summary>
    /// Subject field name. Null omits the subject input entirely — most compose forms send a
    /// fixed, server-generated subject line and only let the human edit the body.
    /// </summary>
    public string? SubjectName { get; init; }
    public string? SubjectValue { get; init; }
    public string? SubjectLabel { get; init; }
    public bool SubjectRequired { get; init; }
    public int SubjectMaxLength { get; init; }

    /// <summary>
    /// The <c>MessageCategory</c> enum name this send uses, or null/"System" for an always-send
    /// message. Tells the preview endpoint whether to show a placeholder unsubscribe footer.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Overrides which posted field name the Preview button reads the subject value from, for a
    /// caller that renders its own subject input outside this partial (survey invitation's
    /// per-culture tabs, where <see cref="SubjectName"/> stays null so this partial doesn't also
    /// render one). Defaults to <see cref="SubjectName"/>.
    /// </summary>
    public string? PreviewSubjectName { get; init; }

    /// <summary>
    /// Distinguishes this instance's preview modal from any others on the same page. Needed only
    /// when a page hosts more than one composer at once (survey invitation's per-culture tabs) or
    /// supplies its own page-level modal that an AJAX-injected composer must target instead of
    /// rendering a redundant, inert copy (Feedback/Issues detail panels, whose own markup never
    /// executes). Null uses the classic shared "emailPreviewModal" id.
    /// </summary>
    public string? PreviewModalId { get; init; }

    /// <summary>
    /// False skips rendering this instance's own preview modal, for a host page that already
    /// includes one copy of it at <see cref="PreviewModalId"/> — because this composer renders
    /// inside an AJAX-injected partial whose own &lt;script&gt; never runs.
    /// </summary>
    public bool IncludePreviewModal { get; init; } = true;
}
