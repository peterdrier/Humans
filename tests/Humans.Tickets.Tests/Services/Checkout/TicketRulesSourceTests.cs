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
    public void Read_SuffixLessFile_IsSpanish()
    {
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/NORMAS-en.md", "R/NORMAS-DE.md", "R/notes-en.md", "R/logo.png"]).Files
            .Should().BeEquivalentTo([("R/NORMAS.md", "es"), ("R/NORMAS-en.md", "en"), ("R/NORMAS-DE.md", "de")]);
    }

    [HumansFact]
    public void RulesFiles_UnusableFolder_NamesItsProblem()
    {
        GitHubTicketRulesSource.RulesFiles(["R/README.md", "R/NORMAS.md", "R/NORMAS-en.md"]).Problem
            .Should().Be("It has more than one .md without a language suffix: R/README.md, R/NORMAS.md.");
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS.md", "R/OTHER-en.md"]).Problem
            .Should().Be("It has no English file (NORMAS-en.md).");
        GitHubTicketRulesSource.RulesFiles(["R/NORMAS-en.md"]).Problem
            .Should().Be("It has no Spanish file (a .md without a language suffix).");
    }

    [HumansFact]
    public async Task Read_GithubFails_ReplacesCachedRules()
    {
        var source = new ScriptedSource();
        var good = new TicketRules("R", "abc123", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "es", ["en"] = "en" });

        foreach (var (failure, problem) in new (Exception, string)[]
                 {
                     (new HttpRequestException("down"), "GitHub could not be reached."),
                     (new TaskCanceledException("timeout"), "GitHub timed out."),
                     (new Octokit.ApiException("unavailable", System.Net.HttpStatusCode.ServiceUnavailable), "GitHub refused the request (503).")
                 })
        {
            source.Next = () => Task.FromResult(new TicketRulesRead(good, null));
            (await source.GetAsync("R", refresh: true)).Rules.Should().BeSameAs(good);

            source.Next = () => throw failure;
            (await source.GetAsync("R", refresh: true)).Should().Be(TicketRulesRead.Failed(problem));
            source.Next = () => throw new InvalidOperationException("The unavailable result should be cached.");
            (await source.GetAsync("R")).Should().Be(TicketRulesRead.Failed(problem));
        }

        source.Next = () => Task.FromResult(new TicketRulesRead(good, null));
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeSameAs(good);
        (await source.GetAsync("R")).Rules.Should().BeSameAs(good);

        source.Next = () => Task.FromResult(TicketRulesRead.Failed("It has no English file (R-en.md)."));
        (await source.GetAsync("R", refresh: true)).Rules.Should().BeNull();
        (await source.GetAsync("R")).Problem.Should().Be("It has no English file (R-en.md).");
    }

    [HumansFact]
    public async Task Read_GithubFailsNoCache_GivesNoRules()
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
