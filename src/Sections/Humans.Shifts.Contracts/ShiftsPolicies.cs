namespace Humans.Shifts.Contracts;

/// <summary>Shifts' authorization policies, registered by its <c>Section</c>. Board passes the reads and the audited reveal, never a write.</summary>
public static class ShiftsPolicies
{
    /// <summary>Shifts Admin, Shifts NoInfo, Board or Admin: the volunteer dashboards, workload and tracking pages.</summary>
    public const string Read = "ShiftsRead";

    /// <summary>Shifts Admin, Shifts NoInfo or Admin: voluntelling from the dashboard.</summary>
    public const string Manage = "ShiftsManage";

    /// <summary>
    /// Shifts Admin or Admin: rota moves and the volunteer-tracking grid. Excludes Shifts NoInfo, who approves signups
    /// but manages no rotas.
    /// </summary>
    public const string ManageVolunteers = "ShiftsManageVolunteers";

    /// <summary>Read plus the coordinator of any team: the shift dashboard and the shift summaries.</summary>
    public const string DepartmentRead = "ShiftsDepartmentRead";

    /// <summary>Shifts NoInfo or Admin: approves signups that need a privileged approver.</summary>
    public const string PrivilegedSignupApprover = "ShiftsPrivilegedSignupApprover";

    /// <summary>Shifts NoInfo, Board or Admin: the audited medical reveal. Every reveal is audited, which is what lets Board in.</summary>
    public const string MedicalDataViewer = "ShiftsMedicalDataViewer";
}
