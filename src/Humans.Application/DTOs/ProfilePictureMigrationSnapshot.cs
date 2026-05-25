using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Issue nobodies-collective/Humans#702: snapshot for the profile-picture
/// DB→FS migration verification page. Captures, for the small set of profiles
/// that still have a non-null <c>ProfilePictureContentType</c> in the DB,
/// which of them already have the expected file on the filesystem and which
/// do not (the "at risk" laggards that block Phase 2 — dropping the DB column).
/// </summary>
/// <param name="TotalCount">
/// Total profiles with a custom picture (<c>ProfilePictureContentType IS NOT NULL</c>).
/// </param>
/// <param name="OnFilesystemCount">
/// Of those, how many have the expected file on disk under the
/// profile-picture storage key path.
/// </param>
/// <param name="DbOnlyRows">
/// Rows present in the DB but missing on the filesystem. Empty list when
/// every DB row has been migrated.
/// </param>
public sealed record ProfilePictureMigrationSnapshot(
    int TotalCount,
    int OnFilesystemCount,
    IReadOnlyList<ProfilePictureMigrationRow> DbOnlyRows)
{
    public int DbOnlyCount => DbOnlyRows.Count;
}

/// <summary>
/// One DB-only profile picture row in <see cref="ProfilePictureMigrationSnapshot"/>.
/// </summary>
public sealed record ProfilePictureMigrationRow(
    Guid ProfileId,
    Guid UserId,
    string DisplayName,
    string ContentType,
    Instant UpdatedAt);
