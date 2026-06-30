using NodaTime;

namespace Humans.Web.Models.EarlyEntry;

public sealed record EarlyEntryRosterViewModel(IReadOnlyList<EarlyEntryRosterRowVm> Rows);

public sealed record EarlyEntryRosterRowVm(
    Guid UserId,
    string LegalName,
    LocalDate EarliestEntryDate,
    IReadOnlyList<string> Sources,
    bool HasMultiple);
