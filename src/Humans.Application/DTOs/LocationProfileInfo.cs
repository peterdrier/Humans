namespace Humans.Application.DTOs;

public record LocationProfileInfo(
    Guid UserId,
    string BurnerName,
    string? ProfilePictureUrl,
    double Latitude,
    double Longitude,
    string? City,
    string? CountryCode);
