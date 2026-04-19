using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ITeamMembershipRepository"/>.
/// </summary>
public sealed class TeamMembershipRepository : ITeamMembershipRepository
{
    private readonly HumansDbContext _dbContext;

    public TeamMembershipRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<TeamMember>> GetActiveByUserIdAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .ToListAsync(ct);
}
