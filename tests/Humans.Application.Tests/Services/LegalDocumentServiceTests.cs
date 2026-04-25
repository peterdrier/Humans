using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Application;
using Humans.Application.Services.Legal;
using Xunit;
using Humans.Application.Interfaces.Legal;

namespace Humans.Application.Tests.Services;

public class LegalDocumentServiceTests : IDisposable
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
    public void GetAvailableDocuments_ReturnsStatutes()
    {
        var documents = _service.GetAvailableDocuments();

        documents.Should().HaveCount(1);
        var statutes = documents[0];
        statutes.Slug.Should().Be("statutes");
        statutes.DisplayName.Should().Be("Statutes");
        statutes.RepoFolder.Should().Be("Estatutos");
        statutes.FilePrefix.Should().Be("ESTATUTOS");
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

        public Task<string?> GetLatestCommitShaAsync(string path, CancellationToken ct = default) =>
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
