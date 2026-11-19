using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

/// <summary>One account dropdown. A <paramref name="Selected"/> account missing from <paramref name="Choices"/> still
/// shows, flagged, so the gap is visible; saving it is refused.</summary>
internal sealed record AccountPickerModel(
    string Name, string Id, string AriaLabel, int? Selected, IReadOnlyList<AccountChoice> Choices);
