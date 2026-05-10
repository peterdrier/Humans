namespace Humans.Application.Interfaces;

/// <summary>
/// Marker for application service interfaces. New read methods on these interfaces
/// must not expose EF/domain aggregate entities; existing violations are ratcheted
/// in <c>Baselines/ApplicationServiceEntityReadReturns.baseline.txt</c>.
/// </summary>
public interface IApplicationService
{
}
