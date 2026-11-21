using Humans.Base.Interfaces;

namespace Humans.Google.Contracts;

public interface IGoogleSyncService : IApplicationService
{
    /// <summary>Lets a section refuse a folder <see cref="IGoogleSyncWrite.CreateSubfolderAsync"/> would later 404 on.</summary>
    Task<bool> IsFolderReachableAsync(string folderId, CancellationToken cancellationToken = default);
}
