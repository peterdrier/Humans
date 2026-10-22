namespace Humans.Onboarding.Contracts;

/// <summary><c>ErrorKey</c> names the failure for the caller to map to its own message.</summary>
public record OnboardingResult(bool Success, string? ErrorKey = null);
