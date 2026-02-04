using NodaTime;

namespace Profiles.Application.DTOs;

/// <summary>
/// Request to update a member profile.
/// </summary>
public record ProfileUpdateRequest(
    string FirstName,
    string LastName,
    LocalDate? DateOfBirth,
    string? PhoneNumber,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    string? CountryCode,
    string? Bio);
