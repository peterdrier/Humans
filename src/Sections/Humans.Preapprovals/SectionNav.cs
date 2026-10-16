using Humans.Base.Interfaces;
using Humans.Preapprovals.Authorization;

namespace Humans.Preapprovals;

/// <summary>
/// The member nav entry, beside Budget. A dropdown: "My pre-approvals" for anyone the
/// <see cref="PreapprovalsPolicies.Access"/> policy admits (grant holders, applicants-to-be,
/// managers, admins) and "Manage pre-approvals" for those passing
/// <see cref="PreapprovalsPolicies.Manage"/>. Shell hides the dropdown when every child is.
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new("Nav_Preapprovals", Policy: PreapprovalsPolicies.Access, Weight: 71,
            Children:
            [
                new("Nav_Preapprovals_Mine", Controller: "Preapprovals", Action: "Index", Policy: PreapprovalsPolicies.Access),
                new("", Divider: true, Policy: PreapprovalsPolicies.Manage),
                new("Nav_Preapprovals_Manage", Controller: "PreapprovalsAdmin", Action: "Index", Policy: PreapprovalsPolicies.Manage)
            ])
    ];
}
