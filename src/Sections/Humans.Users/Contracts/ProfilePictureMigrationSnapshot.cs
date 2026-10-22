using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>Profiles with a database content type, split by whether the file is on disk yet.</summary>
/// <param name="TotalCount">Profiles with a custom picture.</param>
/// <param name="OnFilesystemCount">Of those, how many have the file at the storage key.</param>
/// <param name="DbOnlyRows">Missing on disk; empty once all are migrated.</param>
public sealed record ProfilePictureMigrationSnapshot(
    int TotalCount,
    int OnFilesystemCount,
    IReadOnlyList<ProfilePictureMigrationRow> DbOnlyRows)
{
    public int DbOnlyCount => DbOnlyRows.Count;
}

public sealed record ProfilePictureMigrationRow(
    Guid ProfileId,
    Guid UserId,
    string DisplayName,
    string ContentType,
    Instant UpdatedAt);
