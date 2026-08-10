namespace Humans.Expenses.Domain;

/// <summary>Spanish per-diem (dieta) kind, selecting the tax-exempt daily rate. Not persisted —
/// a parameter to the per-diem wizard only.</summary>
internal enum PerDiemKind
{
    DayTrip = 0,
    Overnight
}
