namespace Humans.Camps.Contracts;

internal interface ICampContactService : IApplicationService
{
    Task<CampContactResult> SendFacilitatedMessageAsync(
        Guid campId,
        string campContactEmail,
        string campDisplayName,
        Guid senderUserId,
        string senderDisplayName,
        string senderEmail,
        string message,
        bool includeContactInfo,
        IReadOnlyList<Guid> leadUserIds,
        string campDetailsUrl);
}

internal sealed record CampContactResult(bool Success, bool RateLimited);
