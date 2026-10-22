using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Testing;

public static class UserEmailFixtures
{
    public static UserEmailRowSnapshot Row(
        Guid userId,
        string email,
        bool verified = true,
        Guid? id = null,
        bool isPrimary = false) =>
        new(
            id ?? Guid.NewGuid(),
            userId,
            email,
            verified,
            Provider: null,
            ProviderKey: null,
            isPrimary,
            Visibility: null,
            VerificationSentAt: null,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
}
