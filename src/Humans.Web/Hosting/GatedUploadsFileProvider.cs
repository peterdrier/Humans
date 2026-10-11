using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Humans.Web.Hosting;

/// <summary>
/// Wraps the web-root file provider so upload subfolders that must be served through a gated
/// controller (profile pictures: GDPR gate; issue screenshots: reach check) are structurally
/// unreachable by <c>UseStaticFiles</c>. The subpath is canonicalized before the check
/// (repeated separators, <c>.</c>/<c>..</c> segments, <c>\</c>), so alternate spellings of a
/// gated path cannot slip past a raw prefix comparison and still resolve to the same file.
/// </summary>
public sealed class GatedUploadsFileProvider(IFileProvider inner) : IFileProvider
{
    private static readonly string[][] GatedPrefixes =
    [
        ["uploads", "profile-pictures"],
        ["uploads", "issues"],
    ];

    public IFileInfo GetFileInfo(string subpath) =>
        IsGated(subpath) ? new NotFoundFileInfo(subpath) : inner.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        IsGated(subpath) ? NotFoundDirectoryContents.Singleton : inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    internal static bool IsGated(string subpath)
    {
        var segments = subpath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (normalized.Count > 0)
                {
                    normalized.RemoveAt(normalized.Count - 1);
                }
                continue;
            }
            normalized.Add(segment);
        }

        return GatedPrefixes.Any(prefix =>
            normalized.Count >= prefix.Length &&
            prefix.Zip(normalized).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)));
    }
}
