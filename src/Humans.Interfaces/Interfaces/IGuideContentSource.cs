namespace Humans.Base.Interfaces;

/// <summary>
/// Abstracts a GitHub markdown fetch against the configured Humans repo/branch so callers
/// (GuideContentService, agent doc readers) are testable without network and share one
/// Octokit client + token-resolution path.
/// </summary>
public interface IGuideContentSource
{
    /// <summary>
    /// Fetches the raw markdown for one guide file by stem (e.g. "Profiles" → Profiles.md)
    /// from the configured guide folder (<c>GuideSettings.FolderPath</c>).
    /// </summary>
    Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the raw markdown for one file from an arbitrary folder inside the same
    /// configured Humans repo/branch (e.g. <c>docs/sections</c>, <c>docs/features/global</c>).
    /// Throws Octokit's <c>NotFoundException</c> when the file is not present; callers
    /// that want null-on-miss must catch it explicitly.
    /// </summary>
    Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every markdown file in the configured repo/branch as a repo-root-relative path
    /// (e.g. <c>src/Sections/Humans.Shifts/Docs/features/shift-management.md</c>), in one recursive tree
    /// request. For a corpus scattered across many folders — feature specs now live in each
    /// section's own <c>Docs/</c> — this is what lets the caller derive its file set from the
    /// repository structure instead of paying a folder listing per section.
    /// </summary>
    /// <returns>
    /// The paths found, and whether that listing is the whole corpus. <c>IsComplete</c> is
    /// false when the tree could not be read (empty paths) or GitHub truncated the recursive
    /// tree (partial paths). A caller that derives a set membership test from this — where an
    /// absent path means "does not exist" — must not persist an incomplete listing, or one
    /// transient failure silently removes real files for the process lifetime.
    /// </returns>
    Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the markdown file stems (filename without the <c>.md</c> extension) in a folder
    /// inside the configured Humans repo/branch (e.g. <c>docs/community-kb</c>). Returns an
    /// empty list when the folder is absent. Used for dynamically-discovered corpora whose
    /// file set changes without a code change.
    /// </summary>
    Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default);
}
