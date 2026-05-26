namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Orchestrator for the "Email a rota" coordinator action: enumerates current
/// (Pending/Confirmed) signups on a rota, groups them by user, and queues one
/// personalised email per recipient through <c>IEmailService</c>.
/// </summary>
/// <remarks>
/// One method, one responsibility. Lives separate from
/// <c>IShiftSignupService</c> / <c>IShiftManagementService</c> because the
/// shape — fan-out enqueue across the Email and Users sections — does not
/// belong on either existing shift surface.
/// </remarks>
public interface IRotaCoordinatorMessageService : IApplicationService
{
    /// <summary>
    /// Queues one personalised email per recipient on the given rota.
    /// Recipients = distinct users with a Pending or Confirmed signup on any
    /// shift in the rota. Each email body contains the sender's free-text
    /// <paramref name="messageText"/> plus the recipient's own chronologically
    /// ordered shift list on this rota.
    /// </summary>
    /// <param name="rotaId">Rota whose signups receive the message.</param>
    /// <param name="senderUserId">Coordinator sending the message. Used for
    /// audit attribution and the Reply-To header.</param>
    /// <param name="messageText">Free-text body composed by the coordinator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Outcome with recipient count and rota name for the
    /// coordinator-facing success message; failure shape on misconfiguration
    /// (rota missing, no active event, etc.).</returns>
    Task<RotaMessageDispatchResult> SendRotaMessageAsync(
        Guid rotaId,
        Guid senderUserId,
        string messageText,
        CancellationToken ct = default);

    /// <summary>
    /// Queues one personalised email per recipient across every current/upcoming
    /// rota in the given team in the active event. Recipients = distinct users with
    /// a Pending or Confirmed signup on any shift in any included rota — deduped
    /// across rotas so each human receives one email. Body lists the recipient's
    /// own shifts grouped by rota, each rota in its own timezone.
    /// </summary>
    /// <param name="teamId">Team whose rotas' signups receive the message.</param>
    /// <param name="senderUserId">Coordinator sending the message. Used for
    /// audit attribution and the Reply-To header.</param>
    /// <param name="messageText">Free-text body composed by the coordinator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Outcome with recipient + rota counts and team name for the
    /// coordinator-facing success message; failure shape on misconfiguration
    /// (no active event, team has no upcoming rotas, etc.).</returns>
    Task<TeamRotasMessageDispatchResult> SendTeamRotasMessageAsync(
        Guid teamId,
        Guid senderUserId,
        string messageText,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the recipient names + counts the team-level compose form needs to
    /// preview audience size — same audience definition as
    /// <see cref="SendTeamRotasMessageAsync"/>, but no email is enqueued.
    /// </summary>
    Task<TeamRotasRecipientPreview> GetTeamRotasRecipientPreviewAsync(
        Guid teamId,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IRotaCoordinatorMessageService.SendTeamRotasMessageAsync"/>.
/// </summary>
public sealed record TeamRotasMessageDispatchResult(
    bool Succeeded,
    int RecipientCount,
    int RotaCount,
    string? TeamName,
    string? Error)
{
    public static TeamRotasMessageDispatchResult Success(int recipientCount, int rotaCount, string teamName) =>
        new(true, recipientCount, rotaCount, teamName, null);

    public static TeamRotasMessageDispatchResult Failure(string error) =>
        new(false, 0, 0, null, error);
}

/// <summary>
/// Preview payload for the team-level compose form: who would receive the message
/// and across how many rotas, without enqueuing anything.
/// </summary>
public sealed record TeamRotasRecipientPreview(
    int RotaCount,
    IReadOnlyList<string> RecipientNames);

/// <summary>
/// Outcome of <see cref="IRotaCoordinatorMessageService.SendRotaMessageAsync"/>.
/// </summary>
public sealed record RotaMessageDispatchResult(
    bool Succeeded,
    int RecipientCount,
    string? RotaName,
    string? Error)
{
    public static RotaMessageDispatchResult Success(int recipientCount, string rotaName) =>
        new(true, recipientCount, rotaName, null);

    public static RotaMessageDispatchResult Failure(string error) =>
        new(false, 0, null, error);
}
