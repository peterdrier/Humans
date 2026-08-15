namespace Humans.Teams.Models;

internal sealed record LocationProfileInfo(
    Guid UserId,
    string DisplayName,
    string? ProfilePictureUrl,
    double Latitude,
    double Longitude,
    string? City,
    string? CountryCode);
