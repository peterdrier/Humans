using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Finance.Tests;

/// <summary>Purchase-document mirrors built from public read models.</summary>
internal static class TestPurchaseMirror
{
    /// <summary><paramref name="notes"/> gives each document's mirrored notes; null leaves them unread.</summary>
    public static HoldedPurchaseDocMirror Of(
        IEnumerable<HoldedPurchaseDocInfo> docs, Func<string, string?>? notes = null) =>
        new(Instant.FromUtc(2026, 1, 1, 0, 0), "Idle", null, 0, null, docs.Select(d => d with { Notes = notes?.Invoke(d.Id) })
            .ToList());

    public static HoldedPurchaseDocMirror NeverSynced { get; } = new(null, "Idle", null, 0, null, []);
}
