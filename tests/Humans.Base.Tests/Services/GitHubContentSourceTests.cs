using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Humans.Base.Tests.Services;

public sealed class GitHubContentSourceTests
{
    [HumansTheory]
    [InlineData(false, "default")]
    [InlineData(false, "file")]
    [InlineData(false, "folder")]
    [InlineData(false, "tree")]
    [InlineData(true, "file")]
    [InlineData(true, "folder")]
    [InlineData(true, "tree")]
    public async Task CancelledRead_StopsBeforeStartingGitHubRequest(bool community, string operation)
    {
        // Invalid repository coordinates ensure a regression fails without sending a network request.
        IGuideContentSource source = community
            ? new GitHubCommunityKbContentSource(
                Options.Create(new CommunityKbSettings { Owner = "", Repository = "" }),
                Options.Create(new GitHubSettings()), NullLogger<GitHubCommunityKbContentSource>.Instance)
            : new GitHubGuideContentSource(
                Options.Create(new GuideSettings { Owner = "", Repository = "" }),
                Options.Create(new GitHubSettings()), NullLogger<GitHubGuideContentSource>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> read = async () =>
        {
            switch (operation)
            {
                case "default": await source.GetMarkdownAsync("page", cancelled.Token); break;
                case "file": await source.GetMarkdownAsync("docs", "page", cancelled.Token); break;
                case "folder": await source.ListMarkdownStemsAsync("docs", cancelled.Token); break;
                case "tree": await source.ListMarkdownPathsAsync(cancelled.Token); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        };

        var thrown = await read.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancelled.Token);
    }
}
