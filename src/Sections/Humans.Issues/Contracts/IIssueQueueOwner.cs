using Humans.Base.Interfaces;

namespace Humans.Issues.Contracts;

/// <summary>
/// A section's declaration that it owns an issue queue. Admin is implicit on every queue and never listed. Dropping
/// an implementation sends that key's stored issues to the Admin-only queue, with no migration.
/// </summary>
public interface IIssueQueueOwner : ISectionAnnotations
{
    /// <summary>The canonical <c>Issue.Section</c> value, normally the section's own name.</summary>
    string QueueKey { get; }

    /// <summary>Besides Admin.</summary>
    IReadOnlyList<string> OwningRoles { get; }

    /// <summary>An IssuesResource key.</summary>
    string AreaLabelResourceKey { get; }

    /// <summary>First URL path segments whose issue reports belong to this queue.</summary>
    IReadOnlyList<string> PathSegments { get; }

    IEnumerable<SectionAnnotation> ISectionAnnotations.Annotations() =>
        [new SectionAnnotation(QueueKey, "Issue queue", string.Join(", ", OwningRoles))];
}
