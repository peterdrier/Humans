using NodaTime;

namespace Humans.EarlyEntry.Models;

internal sealed record EarlyEntryRosterViewModel(IReadOnlyList<EarlyEntryRosterRowVm> Rows);

internal sealed record EarlyEntryRosterRowVm(
    Guid UserId,
    string LegalName,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);
