using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Tickets.Services.Checkout;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Tests.Services.Checkout;

public class TicketRulesSourceTests
{
    [HumansFact]
    public void The_suffix_less_file_is_spanish_and_names_the_set()
    {
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/NORMAS-en.md", "R/NORMAS-DE.md", "R/notes-en.md", "R/logo.png"])
            .Should().BeEquivalentTo([("R/NORMAS.md", "es"), ("R/NORMAS-en.md", "en"), ("R/NORMAS-DE.md", "de")]);
    }

    [HumansFact]
    public void A_second_suffix_less_file_or_a_missing_language_gives_no_rules()
    {
        GitHubTicketRulesSource.RulesFiles(["R/README.md", "R/NORMAS.md", "R/NORMAS-en.md"]).Should().BeNull();
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/OTHER-en.md"]).Should().BeNull();
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS-en.md"]).Should().BeNull();
    }

    [HumansFact]
    public async Task A_github_failure_replaces_cached_rules_with_unavailable_until_a_successful_read()
    {
        var source = new ScriptedSource();
        var good = new TicketRules("R", "abc123", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "es", ["en"] = "en" });

        foreach (var failure in new Exception[]
                 {
                     new HttpRequestException("down"),
                     new TaskCanceledException("timeout"),
                     new Octokit.ApiException("unavailable", System.Net.HttpStatusCode.ServiceUnavailable)
                 })
        {
            source.Next = () => Task.FromResult<TicketRules?>(good);
            (await source.GetAsync("R", refresh: true)).Should().BeSameAs(good);

            source.Next = () => throw failure;
            (await source.GetAsync("R", refresh: true)).Should().BeNull();
            source.Next = () => throw new InvalidOperationException("The unavailable result should be cached.");
            (await source.GetAsync("R")).Should().BeNull();
        }

        source.Next = () => Task.FromResult<TicketRules?>(good);
        (await source.GetAsync("R", refresh: true)).Should().BeSameAs(good);
        (await source.GetAsync("R")).Should().BeSameAs(good);

        source.Next = () => Task.FromResult<TicketRules?>(null);
        (await source.GetAsync("R", refresh: true)).Should().BeNull();
        (await source.GetAsync("R")).Should().BeNull();
    }

    [HumansFact]
    public async Task A_folder_never_read_gives_no_rules_when_github_fails()
    {
        var source = new ScriptedSource { Next = () => throw new HttpRequestException("down") };
        (await source.GetAsync("R")).Should().BeNull();
    }

    private sealed class ScriptedSource() : GitHubTicketRulesSource(
        Options.Create(new GitHubSettings()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubTicketRulesSource>.Instance)
    {
        public Func<Task<TicketRules?>> Next { get; set; } = () => Task.FromResult<TicketRules?>(null);

        internal override Task<TicketRules?> LoadAsync(string folder, CancellationToken ct) => Next();
    }
}
