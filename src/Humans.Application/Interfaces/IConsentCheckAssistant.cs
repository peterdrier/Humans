using Humans.Application.DTOs;

namespace Humans.Application.Interfaces;

/// <summary>
/// LLM-backed assistant that evaluates whether a pending Consent Check entry
/// is safe to auto-approve. Implementations call an external service (Anthropic)
/// and therefore MUST enforce a short timeout and a single retry. Failures
/// (timeout, HTTP error, malformed JSON) are surfaced as exceptions so the
/// calling job can skip the entry and leave it in the manual queue.
/// </summary>
public interface IConsentCheckAssistant
{
    /// <summary>
    /// Ask the LLM to evaluate a single human's legal name.
    /// </summary>
    /// <param name="legalName">The human's legal first + last name (from Profile).</param>
    /// <param name="holdList">Admin-maintained list of names/aliases to flag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ConsentCheckVerdict"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the API is not configured.</exception>
    Task<ConsentCheckVerdict> EvaluateAsync(
        string legalName,
        IReadOnlyList<string> holdList,
        CancellationToken cancellationToken = default);
}
