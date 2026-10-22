namespace Humans.Expenses.Domain;

/// <summary>Selects the tax-exempt daily rate. Not persisted.</summary>
internal enum PerDiemKind
{
    DayTrip = 0,
    Overnight
}
