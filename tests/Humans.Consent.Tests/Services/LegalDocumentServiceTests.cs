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

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDocumentContentAsync_CancelledFetchDoesNotPopulateFailureCache(bool returnAfterCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        _connector.CancelDuringFetch = cancellation;
        _connector.ReturnAfterCancellation = returnAfterCancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.GetDocumentContentAsync("statutes", cancellation.Token));

        _cache.TryGetValue(CacheKeys.LegalDocument("statutes"), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetDocumentContentAsync_FetchTimeoutReturnsEmptyDictionary()
    {
        _connector.CancelFetch = true;

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
    public async Task PrefixDocumentRead_ConfiguredBranch_IsUsed()
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
            "Estatutos", "ESTATUTOS", TestContext.Current.CancellationToken);

        content.Should().ContainKey("es").WhoseValue.Should().Be("hola");
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(uri => uri.Query == "?ref=legal-preview");
    }

    [HumansFact]
    public async Task FileRead_BranchAdvances_ReadsDiscoveredSha()
    {
        using var handler = new LegalContentHandler(branchAdvances: true);
        var connector = new GitHubLegalDocumentConnector(
            Options.Create(new GitHubSettings { Owner = "nobodies", Repository = "legal", Branch = "legal-preview" }),
            NullLogger<GitHubLegalDocumentConnector>.Instance);
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        typeof(GitHubLegalDocumentConnector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connector, client);

        var file = await connector.GetFileContentAsync("Estatutos/ESTATUTOS.md", TestContext.Current.CancellationToken);

        file.Should().NotBeNull();
        file.Sha.Should().Be("abc");
        file.Content.Should().Be("Consentimiento: sí 😀");
        file.CommitSha.Should().Be("commit-1");
        handler.Requests.Should().HaveCount(3);
        Uri.UnescapeDataString(handler.Requests[0].Query).Should()
            .Contain("sha=legal-preview").And.Contain("path=Estatutos/ESTATUTOS.md");
        handler.Requests[1].Query.Should().Be("?ref=commit-1");
        handler.Requests[2].AbsolutePath.Should().Be("/repos/nobodies/legal/git/blobs/abc");
    }

    [HumansFact]
    public async Task FileRead_ProvidesTheCommitReferenceForItsSummary()
    {
        using var handler = new LegalContentHandler("Update policy\nDetails", branchAdvances: true);
        var connector = new GitHubLegalDocumentConnector(
            Options.Create(new GitHubSettings { Owner = "nobodies", Repository = "legal", Branch = "legal-preview" }),
            NullLogger<GitHubLegalDocumentConnector>.Instance);
        var client = new GitHubClient(new Connection(
            new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        typeof(GitHubLegalDocumentConnector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connector, client);

        var file = await connector.GetFileContentAsync("Estatutos/ESTATUTOS.md", TestContext.Current.CancellationToken);
        var summary = await connector.GetCommitMessageAsync(file!.CommitSha, TestContext.Current.CancellationToken);

        summary.Should().Be("Update policy");
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
        new UTF8Encoding(false, true).GetBytes(summary).Should().NotBeEmpty();
    }

    private sealed class LegalContentHandler(string? commitMessage = null, bool branchAdvances = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/commits", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"sha\":\"commit-1\"}]", Encoding.UTF8, "application/json")
                });
            if (branchAdvances && request.RequestUri!.AbsolutePath.EndsWith("/commits/commit-1", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { commit = new { message = commitMessage } }), Encoding.UTF8, "application/json")
                });
            if (branchAdvances && request.RequestUri!.AbsolutePath.EndsWith("/commits/abc", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"message\":\"No commit found\"}", Encoding.UTF8, "application/json")
                });
            if (branchAdvances)
            {
                var body = request.RequestUri!.AbsolutePath.Contains("/contents/", StringComparison.Ordinal)
                    ? """{"type":"file","name":"ESTATUTOS.md","path":"Estatutos/ESTATUTOS.md","sha":"abc"}"""
                    : request.RequestUri!.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal)
                        ? JsonSerializer.Serialize(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes("Consentimiento: sí 😀")), encoding = "base64", sha = "abc" })
                        : "Changed after metadata was read";
                var mediaType = request.RequestUri!.AbsolutePath.Contains("/contents/", StringComparison.Ordinal) || request.RequestUri!.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal)
                    ? "application/json" : "application/octet-stream";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
            }
            const string directory = """
                [{"type":"file","name":"ESTATUTOS.md","path":"Estatutos/ESTATUTOS.md","sha":"abc"}]
                """;
            const string file = """
                {"type":"file","name":"ESTATUTOS.md","path":"Estatutos/ESTATUTOS.md","sha":"abc","content":"aG9sYQ==","encoding":"base64"}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(commitMessage is not null
                    ? JsonSerializer.Serialize(new { commit = new { message = commitMessage } })
                    : Requests.Count == 1 ? directory : file,
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeConnector : IGitHubLegalDocumentConnector
    {
        public Dictionary<string, string> Content { get; set; } =
            new(StringComparer.Ordinal);
        public bool ThrowOnFetch { get; set; }
        public bool CancelFetch { get; set; }
        public CancellationTokenSource? CancelDuringFetch { get; set; }
        public bool ReturnAfterCancellation { get; set; }

        public Task<IReadOnlyDictionary<string, string>> DiscoverLanguageFilesAsync(
            string folderPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));

        public Task<GitHubFileContent?> GetFileContentAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<GitHubFileContent?>(null);

        public Task<string?> GetCommitMessageAsync(string sha, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public async Task<IReadOnlyDictionary<string, string>> GetFolderContentByPrefixAsync(
            string folderPath, string filePrefix, CancellationToken ct = default)
        {
            if (CancelDuringFetch is not null)
            {
                await CancelDuringFetch.CancelAsync();
                if (!ReturnAfterCancellation) ct.ThrowIfCancellationRequested();
            }
            if (CancelFetch)
                throw new OperationCanceledException();
            if (ThrowOnFetch)
                throw new InvalidOperationException("simulated failure");
            return Content;
        }
    }
}
