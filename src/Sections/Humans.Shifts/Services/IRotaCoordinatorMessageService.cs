using Humans.Base.Interfaces;
namespace Humans.Shifts.Services;

/// <summary>
/// Recipient counts are successful send requests; Email may still suppress them under saved preferences.
/// </summary>
internal interface IRotaCoordinatorMessageService : IApplicationService
{
    /// <summary>
    /// One email per user with a Pending or Confirmed signup on the rota. <paramref name="senderUserId"/> is the
    /// audit actor and the Reply-To; <paramref name="includeShifts"/> adds the recipient's own shift list.
    /// </summary>
    Task<RotaMessageDispatchResult> SendRotaMessageAsync(
        Guid rotaId,
        Guid senderUserId,
        string messageText,
        bool includeShifts,
        CancellationToken ct = default);

    /// <summary>
    /// Deduped across the team's rotas that <paramref name="filter"/> admits, so each human gets one email, listing
    /// their shifts by rota, each in its own timezone. <paramref name="senderUserId"/> is the audit actor and Reply-To.
    /// </summary>
    Task<TeamRotasMessageDispatchResult> SendTeamRotasMessageAsync(
        Guid teamId,
        Guid senderUserId,
        string messageText,
        bool includeShifts,
        TeamRotasAudienceFilter filter,
        CancellationToken ct = default);

    /// <summary>Same audience as <see cref="SendTeamRotasMessageAsync"/>; nothing is sent.</summary>
    Task<TeamRotasRecipientPreview> GetTeamRotasRecipientPreviewAsync(
        Guid teamId,
        TeamRotasAudienceFilter filter,
        CancellationToken ct = default);
}

internal sealed record TeamRotasMessageDispatchResult(
    bool Succeeded,
    int RecipientCount,
    int RotaCount,
    string? TeamName,
    string? Error)
{
    internal static TeamRotasMessageDispatchResult Success(int recipientCount, int rotaCount, string teamName) =>
        new(true, recipientCount, rotaCount, teamName, null);

    internal static TeamRotasMessageDispatchResult Failure(string error) =>
        new(false, 0, 0, null, error);
}

internal sealed record TeamRotasRecipientPreview(
    int RotaCount,
    IReadOnlyList<string> RecipientNames);

internal sealed record RotaMessageDispatchResult(
    bool Succeeded,
    int RecipientCount,
    string? RotaName,
    string? Error)
{
    internal static RotaMessageDispatchResult Success(int recipientCount, string rotaName) =>
        new(true, recipientCount, rotaName, null);

    internal static RotaMessageDispatchResult Failure(string error) =>
        new(false, 0, null, error);
}
