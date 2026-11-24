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
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/NORMAS-en.md", "R/NORMAS-DE.md", "R/notes-en.md", "R/logo.png"]).Files
            .Should().BeEquivalentTo([("R/NORMAS.md", "es"), ("R/NORMAS-en.md", "en"), ("R/NORMAS-DE.md", "de")]);
    }

    [HumansFact]
    public void Each_unusable_folder_names_its_own_problem()
    {
        GitHubTicketRulesSource.RulesFiles(["R/README.md", "R/NORMAS.md", "R/NORMAS-en.md"]).Problem
            .Should().Be("It has more than one .md without a language suffix: R/README.md, R/NORMAS.md.");
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/OTHER-en.md"]).Problem
            .Should().Be("It has no English file (NORMAS-en.md).");
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS-en.md"]).Problem
            .Should().Be("It has no Spanish file (a .md without a language suffix).");
    }

    [HumansFact]
    public async Task A_github_failure_keeps_the_last_good_rules_and_an_invalid_folder_drops_them()
    {
        var source = new ScriptedSource();
        var good = new TicketRules("R", "abc123", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "es", ["en"] = "en" });

        source.Next = () => Task.FromResult(new TicketRulesRead(good, null));
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeSameAs(good);

        source.Next = () => throw new HttpRequestException("down");
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeSameAs(good);
        source.Next = () => throw new TaskCanceledException("timeout");
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeSameAs(good);

        source.Next = () => Task.FromResult(TicketRulesRead.Failed("It has no English file (R-en.md)."));
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeNull();
        source.Next = () => throw new HttpRequestException("down");
        (await source.GetAsync("R", refresh: true)).Should().Be(TicketRulesRead.Failed("GitHub could not be reached."));
    }

    [HumansFact]
    public async Task A_folder_never_read_gives_no_rules_when_github_fails()
    {
        var source = new ScriptedSource { Next = () => throw new HttpRequestException("down") };
        (await source.GetAsync("R")).Should().Be(TicketRulesRead.Failed("GitHub could not be reached."));
    }

    private sealed class ScriptedSource() : GitHubTicketRulesSource(
        Options.Create(new GitHubSettings()), new MemoryCache(new MemoryCacheOptions()), NullLogger<GitHubTicketRulesSource>.Instance)
    {
        public Func<Task<TicketRulesRead>> Next { get; set; } = () => Task.FromResult(TicketRulesRead.Failed("empty"));

        internal override Task<TicketRulesRead> LoadAsync(string folder, CancellationToken ct) => Next();
    }
}
