namespace Humans.Application.Interfaces;

/// <summary>
/// Marker carried by every cache-invalidator interface in the codebase.
/// A standalone <c>*Invalidator</c> existing is itself a smell — usually a
/// cross-section write or a flush the owning section's service + caching
/// decorator should have absorbed. The marker makes the family
/// <i>countable</i> so it can be ratcheted toward zero (HUM0028): new
/// interfaces extending <see cref="IInvalidator"/> fire an Error; existing
/// ones carry <c>[Grandfathered("HUM0028", …)]</c> and ride as Warning.
/// </summary>
public interface IInvalidator
{
}
