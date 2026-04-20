using NodaTime;

namespace Humans.Application;

/// <summary>
/// Slim projection of a volunteer-history entry, as included in
/// <see cref="FullProfile"/>. Date is rendered as "MMM'yy" in the UI.
/// </summary>
public record CVEntry(LocalDate Date, string EventName, string? Description);
