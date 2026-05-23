using AwesomeAssertions;
using Xunit;

namespace Humans.Application.Tests.Agent;

/// <summary>
/// Guards the case-insensitive whitelist + canonical-cased path resolution
/// (nobodies-collective/Humans#789). LLMs routinely lowercase the section key
/// (e.g. "shifts"); the reader must canonicalize it to the on-disk filename
/// ("Shifts.md") so the lookup works on case-sensitive filesystems (Linux).
/// </summary>
public class AgentSectionDocReaderTests
{
    [HumansTheory]
    [InlineData("Shifts")]
    [InlineData("shifts")]
    [InlineData("SHIFTS")]
    public async Task ReadAsync_resolves_any_casing_to_the_canonical_guide(string key)
    {
        var env = new TestHostEnvironment();
        var reader = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);

        var content = await reader.ReadAsync(key, CancellationToken.None);

        var canonical = await File.ReadAllTextAsync(
            Path.Combine(env.ContentRootPath, "docs", "sections", "Shifts.md"),
            CancellationToken.None);

        content.Should().Be(canonical, "any casing of a whitelisted key must resolve to docs/sections/Shifts.md");
    }

    /// <summary>
    /// Proves the canonicalization is independent of the host filesystem's case sensitivity:
    /// a content root containing ONLY the canonical-cased file "Profiles.md" is read
    /// successfully even when the caller supplies a lowercased "profiles" key. The reader
    /// must build the path from the whitelist's canonical entry, not the raw key.
    /// </summary>
    [HumansFact]
    public async Task ReadAsync_uses_canonical_filename_not_caller_casing()
    {
        var root = Path.Combine(Path.GetTempPath(), "humans-doc-reader-" + Guid.NewGuid().ToString("N"));
        var sectionsDir = Path.Combine(root, "docs", "sections");
        Directory.CreateDirectory(sectionsDir);
        try
        {
            const string body = "# Profiles guide\nbody";
            await File.WriteAllTextAsync(Path.Combine(sectionsDir, "Profiles.md"), body);

            var env = new TestHostEnvironment { ContentRootPath = root };
            var reader = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);

            var content = await reader.ReadAsync("profiles", CancellationToken.None);

            content.Should().Be(body);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_for_non_whitelisted_key()
    {
        var env = new TestHostEnvironment();
        var reader = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);

        var content = await reader.ReadAsync("NotASection", CancellationToken.None);

        content.Should().BeNull();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "sections")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Humans.Application.Tests";
        public string ContentRootPath { get; set; } = RepoRoot();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(RepoRoot());
    }
}
