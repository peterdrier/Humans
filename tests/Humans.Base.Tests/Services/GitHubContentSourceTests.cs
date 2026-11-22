using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using Xunit;

namespace Humans.Base.Tests.Services;

public sealed class GitHubContentSourceTests
{
    public enum ReadOperation { File, Folder, Tree }

    [HumansTheory]
    [InlineData(ReadOperation.File)]
    [InlineData(ReadOperation.Folder)]
    [InlineData(ReadOperation.Tree)]
    public async Task PendingRead_CancellationStopsWaitingWithoutReturningAMiss(ReadOperation operation)
    {
        var client = Substitute.For<IGitHubClient>();
        client.Repository.Content.GetRawContentByRef("owner", "repo", "docs/page.md", "main")
            .Returns(new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        client.Repository.Content.GetAllContentsByRef("owner", "repo", "docs", "main")
            .Returns(new TaskCompletionSource<IReadOnlyList<RepositoryContent>>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        client.Git.Tree.GetRecursive("owner", "repo", "main")
            .Returns(new TaskCompletionSource<TreeResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var reader = new GitHubMarkdownReader(client, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        Task read = operation switch
        {
            ReadOperation.File => reader.GetMarkdownAsync("owner", "repo", "main", "docs/", "page", cancellation.Token),
            ReadOperation.Folder => reader.ListMarkdownStemsAsync("owner", "repo", "main", "docs/", cancellation.Token),
            ReadOperation.Tree => reader.ListMarkdownPathsAsync("owner", "repo", "main", cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        read.IsCompleted.Should().BeFalse();

        await cancellation.CancelAsync();

        var awaitRead = async () => await read.WaitAsync(TestContext.Current.CancellationToken);
        var error = await awaitRead.Should().ThrowAsync<OperationCanceledException>();
        error.Which.CancellationToken.Should().Be(cancellation.Token);
    }

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
