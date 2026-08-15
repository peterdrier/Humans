namespace Humans.Users.Contracts;

/// <summary>
/// Marks a string property as containing Markdown content.
/// Views rendering these properties should use Html.SanitizedMarkdown() rather than raw output.
/// </summary>
/// <remarks>
/// A deliberate internal twin of <c>Humans.Domain.Attributes.MarkdownContentAttribute</c>
/// (G5 lane 3b, nobodies-collective/Humans#866). The original lives in Humans.Interfaces and
/// stays there — eight section projects apply it and it is a generic concern, not a Users one —
/// but this leaf must reach zero &lt;ProjectReference&gt; so Base may reference it
/// (design §15 step 5b), so the one usage here needs a local declaration.
///
/// Different namespace, so <c>Humans.Users</c> — which references both assemblies — sees no
/// CS0433. <c>internal</c>, so the leaf's public surface does not grow; applying an internal
/// attribute to a public member is legal C#.
///
/// Duplication is safe because the attribute has zero readers: no <c>GetCustomAttribute</c>,
/// no analyzer, no test names it. It is documentation for view authors. If a reader is ever
/// added it must handle both declarations, or this twin must be deleted and Profile.MarkdownBio
/// left unmarked.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class MarkdownContentAttribute : Attribute;
