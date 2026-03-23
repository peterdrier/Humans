using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class LegalDocumentServiceTests : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly LegalDocumentService _service;

    public LegalDocumentServiceTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        var gitHubSettings = Options.Create(new GitHubSettings
        {
            Owner = "test-owner",
            Repository = "test-repo",
        });

        _service = new LegalDocumentService(
            _cache,
            NullLogger<LegalDocumentService>.Instance,
            gitHubSettings);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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

    [Fact]
    public void GetAvailableDocuments_ReturnsReadOnlyList()
    {
        var documents = _service.GetAvailableDocuments();

        documents.Should().BeAssignableTo<IReadOnlyList<LegalDocumentDefinition>>();
    }

    [Fact]
    public async Task GetDocumentContentAsync_UnknownSlug_ReturnsEmptyDictionary()
    {
        var result = await _service.GetDocumentContentAsync("nonexistent");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDocumentContentAsync_GitHubFailure_ReturnsEmptyDictionary()
    {
        // With invalid owner/repo and no real GitHub connection, this should fail gracefully
        var result = await _service.GetDocumentContentAsync("statutes");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDocumentContentAsync_CachesResult()
    {
        // First call - will fail but cache the empty result
        var result1 = await _service.GetDocumentContentAsync("statutes");

        // Second call - should return cached result
        var result2 = await _service.GetDocumentContentAsync("statutes");

        result1.Should().BeEmpty();
        result2.Should().BeEmpty();

        // Verify cache key exists
        _cache.TryGetValue(CacheKeys.LegalDocument("statutes"), out _).Should().BeTrue();
    }

    [Fact]
    public void CacheKey_LegalDocument_FormatsCorrectly()
    {
        CacheKeys.LegalDocument("statutes").Should().Be("Legal:statutes");
        CacheKeys.LegalDocument("privacy-policy").Should().Be("Legal:privacy-policy");
    }
}
