namespace Humans.Domain.Enums;

public enum HoldedMatchStatus
{
    Matched = 0,
    NoTags = 1,
    UnknownTag = 2,
    MultiMatchConflict = 3,
    NoBudgetYearForDate = 4,
    UnsupportedCurrency = 5,
}
