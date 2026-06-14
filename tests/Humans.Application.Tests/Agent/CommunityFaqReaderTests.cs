using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services.Preload;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class CommunityFaqReaderTests
{
    private const string GeneralBody =
        "# General & Community — NCA\nLast updated: 2026-02-01 · windows merged through 2026-02-01\n\n## Overview\nWhat the NCA is and how to join.\n\n## FAQ\n**Q?**\nA.";

    [HumansFact]
    public async Task ListTopicsAsync_parses_title_date_and_overview_summary()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries.Should().ContainSingle();
        var e = entries[0];
        e.Topic.Should().Be("FAQ-general");
        e.Title.Should().Be("General & Community — NCA");
        e.LastUpdated.Should().Contain("2026-02-01");
        e.Summary.Should().Be("What the NCA is and how to join.");
    }

    [HumansFact]
    public async Task ListTopicsAsync_falls_back_to_title_when_no_overview()
    {
        var source = new FakeSource { Files = { ["bare"] = "# Bare Title\n\nNo overview here." } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries[0].Summary.Should().Be("Bare Title");
    }

    [HumansFact]
    public async Task ListTopicsAsync_reads_the_explicit_keywords_section()
    {
        // The Overview alone hides terms like "urinals"/"VIPee" the router needs. Coverage is made
        // legible via an explicit `## Keywords` line the KB generator emits — the app does not
        // derive keywords from prose. Multi-line sections collapse to one space-joined string.
        const string body =
            "# Leave No Trace\nLast updated: 2026-06-14\n\n## Overview\nKeeping the site clean.\n\n" +
            "## Keywords\ntoilets, TAP, PMS, urinals, vulva urinals, VIPee, Octopee, grey water\n\n" +
            "## FAQ\n**Q?**\nA.";
        var source = new FakeSource { Files = { ["lnt"] = body } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries[0].Keywords.Should().Be("toilets, TAP, PMS, urinals, vulva urinals, VIPee, Octopee, grey water");
        // The Overview paragraph remains the Summary, separate from keywords.
        entries[0].Summary.Should().Be("Keeping the site clean.");
    }

    [HumansFact]
    public async Task ListTopicsAsync_yields_no_keywords_when_no_keywords_section()
    {
        var source = new FakeSource { Files = { ["bare"] = "# Bare Title\n\n## Overview\nNo keywords here." } };
        var reader = MakeReader(source);

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries[0].Keywords.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReadAsync_returns_body_for_a_discovered_topic()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);

        body.Should().Be(GeneralBody);
    }

    [HumansFact]
    public async Task ReadAsync_returns_null_for_unknown_topic()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync("does-not-exist", TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task ReadAsync_rejects_unsafe_topic_keys(string topic)
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync(topic, TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }

    [HumansFact]
    public async Task ReadAsync_caches_the_doc_fetch()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        await reader.ListTopicsAsync(TestContext.Current.CancellationToken); // warms via index parse
        await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);
        await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);

        // One raw fetch per file across list + reads.
        source.RawFetches["FAQ-general"].Should().Be(1);
    }

    [HumansFact]
    public void WrapWithProvenance_prepends_an_unofficial_header_with_the_date()
    {
        var wrapped = CommunityFaqReader.WrapWithProvenance(GeneralBody);

        wrapped.Should().StartWith("SOURCE: community Discord FAQ");
        wrapped.Should().Contain("NOT official");
        wrapped.Should().Contain("2026-02-01");
        wrapped.Should().Contain("not be official");
        wrapped.Should().Contain(GeneralBody);
    }

    [HumansFact]
    public async Task ReloadAsync_refetches_and_swaps_in_new_content()
    {
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);
        await reader.ListTopicsAsync(TestContext.Current.CancellationToken); // initial fetch + cache

        source.Files["FAQ-general"] = "# New Title\nLast updated: 2026-07-01\n\n## Overview\nFresh content.";
        await reader.ReloadAsync(TestContext.Current.CancellationToken);

        var body = await reader.ReadAsync("FAQ-general", TestContext.Current.CancellationToken);
        body.Should().Contain("Fresh content.");

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);
        entries[0].Title.Should().Be("New Title");

        source.RawFetches["FAQ-general"].Should().Be(2); // one initial list, one reload
    }

    [HumansFact]
    public async Task ListTopicsAsync_returns_empty_when_folder_absent()
    {
        var reader = MakeReader(new FakeSource()); // no files

        var entries = await reader.ListTopicsAsync(TestContext.Current.CancellationToken);

        entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReadAsync_resolves_caller_casing_to_canonical_stem()
    {
        // LLMs routinely lowercase the topic key; the reader must fetch the canonical
        // (case-sensitive) filename stem, not the caller's casing.
        var source = new FakeSource { Files = { ["FAQ-general"] = GeneralBody } };
        var reader = MakeReader(source);

        var body = await reader.ReadAsync("faq-general", TestContext.Current.CancellationToken);

        body.Should().Be(GeneralBody);
        source.RawFetches.Should().NotContainKey("faq-general"); // no case-mismatched fetch/404
    }

    private static CommunityFaqReader MakeReader(FakeSource source) =>
        new(source, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CommunityFaqReader>.Instance);

    private sealed class FakeSource : IGuideContentSource
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> RawFetches { get; } = new(StringComparer.Ordinal);

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
        {
            if (!Files.TryGetValue(fileStem, out var body))
                throw new NotFoundException("missing", System.Net.HttpStatusCode.NotFound);
            RawFetches[fileStem] = RawFetches.GetValueOrDefault(fileStem) + 1;
            return Task.FromResult(body);
        }

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Files.Keys.ToList());
    }
}
