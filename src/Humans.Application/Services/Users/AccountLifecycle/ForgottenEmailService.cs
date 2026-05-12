using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Mailer;
using NodaTime;

namespace Humans.Application.Services.Users.AccountLifecycle;

public sealed class ForgottenEmailService : IForgottenEmailService
{
    private readonly IForgottenEmailRepository _repo;

    public ForgottenEmailService(IForgottenEmailRepository repo)
    {
        _repo = repo;
    }

    public async Task<int> RecordForgottenAsync(
        Guid userId, IReadOnlyCollection<string> emails,
        Instant anonymizedAt, CancellationToken ct = default)
    {
        if (emails.Count == 0) return 0;
        var hashes = emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(EmailHasher.Hash)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return await _repo.AddManyAsync(userId, hashes, anonymizedAt, ct);
    }

    public Task<bool> IsForgottenAsync(string email, CancellationToken ct = default)
        => _repo.ExistsByHashAsync(EmailHasher.Hash(email), ct);

    public async Task<IReadOnlySet<string>> GetForgottenAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        if (emails.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byHash = emails.ToDictionary(EmailHasher.Hash, e => e, StringComparer.Ordinal);
        var foundHashes = await _repo.GetExistingHashesAsync(byHash.Keys, ct);
        return foundHashes.Select(h => byHash[h]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _repo.CountAsync(ct);
}
