using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using JetBrains.Annotations;

namespace Humans.Shifts.Contracts;

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
public sealed record ShiftsPageBannerArgs(Guid UserId);

/// <summary>
/// A banner another section shows above the volunteer's Shifts pages (browse, My Shifts). The component decides
/// for itself whether to render; having no contributor is fine.
/// </summary>
public interface IShiftsPageBanner : ISectionContribution
{
    [ViewComponentSlot(typeof(ShiftsPageBannerArgs))]
    Type Banner { get; }
}
