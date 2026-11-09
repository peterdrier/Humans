using System.Text;
using Octokit;

namespace Humans.Base.Services;

/// <summary>Shared read, cancellation and missing-list behavior for GitHub markdown repositories.</summary>
internal sealed class GitHubMarkdownReader(IGitHubClient client, ILogger logger)
{
    public async Task<string> GetMarkdownAsync(
        string owner, string repository, string branch, string folderPath, string fileStem, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = $"{folderPath.TrimEnd('/')}/{fileStem}.md";
        logger.LogDebug("Fetching markdown file {Path} from {Owner}/{Repository}@{Branch}",
            path, owner, repository, branch);
        // Octokit has no token overload on these reads; cancellation stops waiting for the result.
        var bytes = await client.Repository.Content.GetRawContentByRef(owner, repository, path, branch).WaitAsync(ct);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<IReadOnlyList<string>> ListMarkdownStemsAsync(
        string owner, string repository, string branch, string folderPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var contents = await client.Repository.Content.GetAllContentsByRef(
                owner, repository, folderPath.TrimEnd('/'), branch).WaitAsync(ct);
            return contents
                .Where(c => c.Type == ContentType.File && c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name[..^3])
                .ToList();
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Folder not found in GitHub: {Owner}/{Repository}@{Branch}/{Folder}",
                owner, repository, branch, folderPath);
            return [];
        }
    }

    public async Task<(IReadOnlyList<string> Paths, bool IsComplete)> ListMarkdownPathsAsync(
        string owner, string repository, string branch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var tree = await client.Git.Tree.GetRecursive(owner, repository, branch).WaitAsync(ct);
            if (tree.Truncated)
                logger.LogWarning(
                    "Recursive tree for {Owner}/{Repository}@{Branch} was truncated by GitHub; the markdown listing is incomplete",
                    owner, repository, branch);
            IReadOnlyList<string> paths = [.. tree.Tree
                .Where(i => i.Type == TreeType.Blob && i.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Path)];
            return (paths, !tree.Truncated);
        }
        catch (NotFoundException)
        {
            logger.LogWarning("Tree not found in GitHub: {Owner}/{Repository}@{Branch}", owner, repository, branch);
            return ([], false);
        }
    }
}
