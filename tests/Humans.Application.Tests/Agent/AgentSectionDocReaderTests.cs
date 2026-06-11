using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octokit;
using Xunit;

namespace Humans.Application.Tests.Agent;

/// <summary>
/// Guards the case-insensitive whitelist + canonical-cased path resolution
/// (nobodies-collective/Humans#789). LLMs routinely lowercase the section key
/// (e.g. "shifts"); the reader must canonicalize it to the on-GitHub filename
/// ("Shifts.md") because GitHub paths are case-sensitive.
/// </summary>
public class AgentSectionDocReaderTests
{
    [HumansTheory]
    [InlineData("Shifts")]
    [InlineData("shifts")]
    [InlineData("SHIFTS")]
    public async Task ReadAsync_resolves_any_casing_to_the_canonical_section_file(string key)
    {
        var source = new FakeSource();
        var reader = MakeReader(source);

        var content = await reader.ReadAsync(key, TestContext.Current.CancellationToken);

        content.Should().NotBeNullOrEmpty();
        source.LastFolder.Should().Be(AgentSectionDocReader.FolderPath);
        source.LastStem.Should().Be("Shifts", "the reader must canonicalize the key, not pass caller casing");
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_for_non_whitelisted_key()
    {
        var reader = MakeReader(new FakeSource());

        var content = await reader.ReadAsync("NotASection", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_when_github_returns_not_found()
    {
        var source = new FakeSource { FailWith = new NotFoundException("missing", System.Net.HttpStatusCode.NotFound) };
        var reader = MakeReader(source);

        var content = await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_on_transient_github_failure()
    {
        var source = new FakeSource { FailWith = new InvalidOperationException("network down") };
        var reader = MakeReader(source);

        var content = await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_caches_successful_fetch()
    {
        var source = new FakeSource();
        var reader = MakeReader(source);

        await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);
        await reader.ReadAsync("Shifts", TestContext.Current.CancellationToken);

        source.CallCount.Should().Be(1);
    }

    private static AgentSectionDocReader MakeReader(FakeSource source) =>
        new(
            source,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new GuideSettings { CacheTtlHours = 6 }),
            NullLogger<AgentSectionDocReader>.Instance);

    private sealed class FakeSource : IGuideContentSource
    {
        public int CallCount { get; private set; }
        public string? LastFolder { get; private set; }
        public string? LastStem { get; private set; }
        public Exception? FailWith { get; set; }

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Agent reader must use the folder-parameterized overload.");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastFolder = folderPath;
            LastStem = fileStem;
            if (FailWith is not null) throw FailWith;
            return Task.FromResult($"# {fileStem}\n\nBody.");
        }
    }
}
