using NodaTime;

namespace Humans.Auth.Domain;

internal sealed class RoleAssignment
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public string RoleName { get; set; } = string.Empty;

    public Instant ValidFrom { get; init; }

    /// <summary>Null means open-ended: the assignment has not been ended.</summary>
    public Instant? ValidTo { get; set; }

    public string? Notes { get; set; }

    public Instant CreatedAt { get; init; }

    public Guid CreatedByUserId { get; init; }

    public bool IsActive(Instant asOf)
    {
        if (asOf < ValidFrom)
        {
            return false;
        }

        if (ValidTo.HasValue && asOf >= ValidTo.Value)
        {
            return false;
        }

        return true;
    }
}
