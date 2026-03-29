namespace Humans.Domain.Attributes;

/// <summary>
/// Marks a string property as containing Markdown content.
/// Views rendering these properties should use Html.SanitizedMarkdown() rather than raw output.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MarkdownContentAttribute : Attribute;
