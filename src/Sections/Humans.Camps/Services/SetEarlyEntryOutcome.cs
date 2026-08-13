namespace Humans.Camps.Services;

internal enum SetEarlyEntryOutcome
{
    Success,
    NoChange,
    SlotCapExceeded,
    MemberNotActive,
    MemberNotFound,
    MemberAlreadyEntered,
}
