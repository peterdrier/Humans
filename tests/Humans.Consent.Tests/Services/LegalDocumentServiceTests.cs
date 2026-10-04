using Xunit;
using System.Text.Json;
using System.Net;
using System.Reflection;
using System.Text;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;
using Octokit;
using Octokit.Internal;
using AwesomeAssertions;
using Humans.Base.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Consent.Services;
using Humans.Consent.Contracts;

namespace Humans.Consent.Tests.Services;

public sealed class LegalDocumentServiceTests : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly FakeConnector _connector;
    private readonly LegalDocumentService _service;

    public LegalDocumentServiceTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _connector = new FakeConnector();

        _service = new LegalDocumentService(
            _cache,
            _connector,
            NullLogger<LegalDocumentService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public void GetAvailableDocuments_ReturnsRegisteredDefinitions()
    {
        var documents = _service.GetAvailableDocuments();

        documents.Should().HaveCount(2);

        var statutes = documents.Single(d => string.Equals(d.Slug, "statutes", StringComparison.Ordinal));
        statutes.DisplayName.Should().Be("Statutes");
        statutes.RepoFolder.Should().Be("Estatutos");
        statutes.FilePrefix.Should().Be("ESTATUTOS");

        var agentChat = documents.Single(d => string.Equals(d.Slug, "agent-chat", StringComparison.Ordinal));
        agentChat.DisplayName.Should().Be("Agent Chat Terms");
        agentChat.RepoFolder.Should().Be("AgentChat");
        agentChat.FilePrefix.Should().Be("AGENTCHAT");
    }

    [HumansFact]
    public void GetAvailableDocuments_ReturnsReadOnlyList()
    {
        var documents = _service.GetAvailableDocuments();

        documents.Should().BeAssignableTo<IReadOnlyList<LegalDocumentDefinition>>();
    }

    [HumansFact]
    public async Task GetDocumentContentAsync_UnknownSlug_ReturnsEmptyDictionary()
    {
        var result = await _service.GetDocumentContentAsync("nonexistent");

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDocumentContentAsync_GitHubFailure_ReturnsEmptyDictionary()
    {
        _connector.ThrowOnFetch = true;

        var result = await _service.GetDocumentContentAsync("statutes");

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetDocumentContentAsync_CachesResult()
    {
        _connector.Content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["es"] = "hola"
        };

        // First call — populates cache.
        var result1 = await _service.GetDocumentContentAsync("statutes");

        // Swap out underlying content — if second call still equals first, cache worked.
        _connector.Content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["es"] = "changed"
        };
        var result2 = await _service.GetDocumentContentAsync("statutes");

        result1.Should().ContainKey("es");
        result1["es"].Should().Be("hola");
        result2["es"].Should().Be("hola");
        _cache.TryGetValue(CacheKeys.LegalDocument("statutes"), out _).Should().BeTrue();
    }

    [HumansFact]
    public void CacheKey_LegalDocument_FormatsCorrectly()
    {
        CacheKeys.LegalDocument("statutes").Should().Be("Legal:statutes");
        CacheKeys.LegalDocument("privacy-policy").Should().Be("Legal:privacy-policy");
    }

    [HumansFact]
    public async Task PrefixDocumentRead_UsesConfiguredBranchForDirectoryAndContent()
    {
        using var handler = new LegalContentHandler();
        var connector = new GitHubLegalDocumentConnector(
            Options.Create(new GitHubSettings { Branch = "legal-preview" }),
            NullLogger<GitHubLegalDocumentConnector>.Instance);
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        typeof(GitHubLegalDocumentConnector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connector, client);

        var content = await connector.GetFolderContentByPrefixAsync(
            "Estatutos", "ESTATUTOS", Xunit.TestContext.Current.CancellationToken);

        content.Should().ContainKey("es").WhoseValue.Should().Be("hola");
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(uri => uri.Query == "?ref=legal-preview");
    }

    [HumansTheory]
    [InlineData(498, 500)]
    [InlineData(499, 499)]
    public async Task CommitSummary_TruncatesWithoutSplittingUnicode(int prefixLength, int expectedLength)
    {
        var message = new string('A', prefixLength) + "😀" + "remaining\nCommit body";
        using var handler = new LegalContentHandler(message);
        var connector = new GitHubLegalDocumentConnector(
            Options.Create(new GitHubSettings { Owner = "nobodies", Repository = "legal" }),
            NullLogger<GitHubLegalDocumentConnector>.Instance);
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        typeof(GitHubLegalDocumentConnector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connector, client);

        var summary = await connector.GetCommitMessageAsync("abc", TestContext.Current.CancellationToken);

        summary.Should().Be(message[..expectedLength]);
        new UTF8Encoding(false, true).GetBytes(summary!).Should().NotBeEmpty();
    }

    [HumansTheory]
    [InlineData("discovery", 1)]
    [InlineData("file", 1)]
    [InlineData("file", 2)]
    [InlineData("commit", 1)]
    [InlineData("prefix", 1)]
    [InlineData("prefix", 2)]
    public async Task GitHubRead_PropagatesCancellationAtEachFetch(string operation, int pauseRequest)
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new LegalContentHandler(string.Equals(operation, "commit", StringComparison.Ordinal) ? "summary" : null, pauseRequest);
        var connector = new GitHubLegalDocumentConnector(
            Options.Create(new GitHubSettings { Owner = "nobodies", Repository = "legal" }),
            NullLogger<GitHubLegalDocumentConnector>.Instance);
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        typeof(GitHubLegalDocumentConnector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connector, client);

        var read = ReadAsync();
        await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        handler.Release.SetResult();

        var act = () => read.WaitAsync(TestContext.Current.CancellationToken);
        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancelled.Token);
        handler.Requests.Should().HaveCount(pauseRequest);

        async Task ReadAsync()
        {
            switch (operation)
            {
                case "discovery":
                    await connector.DiscoverLanguageFilesAsync("Estatutos", cancelled.Token);
                    break;
                case "file":
                    await connector.GetFileContentAsync("Estatutos/ESTATUTOS.md", cancelled.Token);
                    break;
                case "commit":
                    await connector.GetCommitMessageAsync("abc", cancelled.Token);
                    break;
                case "prefix":
                    await connector.GetFolderContentByPrefixAsync("Estatutos", "ESTATUTOS", cancelled.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
    }

    private sealed class LegalContentHandler(string? commitMessage = null, int? pauseRequest = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Requests.Count == pauseRequest)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            const string directory = """
                [{"type":"file","name":"ESTATUTOS.md","path":"Estatutos/ESTATUTOS.md","sha":"abc"}]
                """;
            const string file = """
                {"type":"file","name":"ESTATUTOS.md","path":"Estatutos/ESTATUTOS.md","sha":"abc","content":"aG9sYQ==","encoding":"base64"}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(commitMessage is not null
                    ? JsonSerializer.Serialize(new { commit = new { message = commitMessage } })
                    : Requests.Count == 1 ? directory : file,
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeConnector : IGitHubLegalDocumentConnector
    {
        public Dictionary<string, string> Content { get; set; } =
            new(StringComparer.Ordinal);
        public bool ThrowOnFetch { get; set; }

        public Task<IReadOnlyDictionary<string, string>> DiscoverLanguageFilesAsync(
            string folderPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));

        public Task<GitHubFileContent?> GetFileContentAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<GitHubFileContent?>(null);

        public Task<string?> GetCommitMessageAsync(string sha, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetFolderContentByPrefixAsync(
            string folderPath, string filePrefix, CancellationToken ct = default)
        {
            if (ThrowOnFetch)
                throw new InvalidOperationException("simulated failure");
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Content);
        }
    }
}
