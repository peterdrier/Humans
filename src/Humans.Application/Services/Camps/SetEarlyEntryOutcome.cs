namespace Humans.Application.Services.Camps;

public enum SetEarlyEntryOutcome
{
    Success,
    NoChange,
    SlotCapExceeded,
    MemberNotActive,
    MemberNotFound,
}
