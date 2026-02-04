namespace Profiles.Application.DTOs;

/// <summary>
/// Request to submit a new membership application.
/// </summary>
public record ApplicationSubmitRequest(
    string Motivation,
    string? AdditionalInfo);
