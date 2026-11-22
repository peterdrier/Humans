using Humans.Base.Interfaces;

namespace Humans.Preapprovals.Contracts;

public interface IPreapprovalService : IApplicationService
{
    /// <summary>
    /// Every grant of every program, in any state. Few enough to read whole; the caller picks a person's or a
    /// manager's grants by comparing every id that person has held against <see cref="GrantOption.HolderUserId"/>
    /// and <see cref="GrantOption.ManagerUserIds"/>.
    /// </summary>
    Task<IReadOnlyList<GrantOption>> GetGrantsAsync(CancellationToken ct);
}
