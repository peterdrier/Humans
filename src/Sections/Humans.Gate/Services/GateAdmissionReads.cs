using Humans.Gate.Contracts;
using Humans.Gate.Data;

namespace Humans.Gate.Services;

/// <summary>Separate from <see cref="GateService"/>, which itself depends on Tickets: one class would be a DI cycle.</summary>
internal sealed class GateAdmissionReads(IGateRepository repository) : IGateAdmissions
{
    public async Task<IReadOnlySet<string>> GetAdmittedAsync(
        IReadOnlyCollection<string> barcodes, CancellationToken ct = default)
    {
        if (barcodes.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var byKey = barcodes.GroupBy(GateBarcode.Normalize, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var admitted = await repository.GetAdmittedKeysAsync(byKey.Keys.ToList(), ct);
        return admitted.SelectMany(k => byKey[k]).ToHashSet(StringComparer.Ordinal);
    }
}
