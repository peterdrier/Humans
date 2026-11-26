
namespace Humans.Users.Contracts;

public interface IContactFieldService
{
    /// <summary>Filtered to what <paramref name="viewerUserId"/> may see. Served from the <c>UserInfo</c> cache.</summary>
    Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid userId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Every field, for the owner's editor.</summary>
    Task<IReadOnlyList<ContactFieldEditDto>> GetAllContactFieldsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates phone prefixes before any write, returning a structured refusal on invalid input.
    /// Upserts and deletes the current fields. Historical email fields stay unless listed in
    /// <paramref name="removedLegacyContactFieldIds"/>.
    /// </summary>
    Task<ProfileEditResult> SaveContactFieldsAsync(
        Guid userId,
        IReadOnlyList<ContactFieldEditDto> fields,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? removedLegacyContactFieldIds = null);

    Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>What <paramref name="viewerUserId"/> may see of <paramref name="ownerUserId"/> on a profile card.</summary>
    Task<ProfileCardVisibility> GetProfileCardVisibilityAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>The privacy rules a profile card renders, decided by <see cref="IContactFieldService"/>.</summary>
/// <param name="CanViewLegalName">Legal name, emergency contact and Board notes: the owner and the Board only.</param>
/// <param name="AccessLevel">The most private contact visibility the viewer may see.</param>
/// <param name="CanSendMessage">Not the owner, no email visible to all profiles, and the owner accepts facilitated messages.</param>
public sealed record ProfileCardVisibility(bool CanViewLegalName, ContactFieldVisibility AccessLevel, bool CanSendMessage);
