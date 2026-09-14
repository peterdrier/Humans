using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds <see cref="UserEmailRowSnapshot"/> rows for tests that stub
/// <c>IUserEmailService.FindByAddressAsync</c>. The record is positional with
/// twelve members and most stubs only care about the owner, the address and
/// whether it is verified.
/// </summary>
public static class UserEmailFixtures
{
    public static UserEmailRowSnapshot Row(
        Guid userId,
        string email,
        bool verified = true,
        Guid? id = null,
        bool isPrimary = false,
        bool isGoogle = false) =>
        new(
            id ?? Guid.NewGuid(),
            userId,
            email,
            verified,
            Provider: null,
            ProviderKey: null,
            isGoogle,
            isPrimary,
            Visibility: null,
            VerificationSentAt: null,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
}
