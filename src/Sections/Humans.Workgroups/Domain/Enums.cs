namespace Humans.Workgroups.Domain;

/// <summary>
/// Register lifecycle (design §6). Registration is administrative recognition only:
/// every transition out of <see cref="Applied"/> is a human action by the Secretary
/// (a Board member) or the Board.
/// </summary>
internal enum WorkgroupStatus
{
    /// <summary>Applied for; waiting on the Secretary. The fourteen-day clock runs from AppliedAt.</summary>
    Applied,

    /// <summary>A refusal ground may apply; the Board decides at its next meeting.</summary>
    Referred,

    /// <summary>Registered. Drive folder created, page and log live.</summary>
    Active,

    /// <summary>Registration refused, with written reasons.</summary>
    Refused,

    /// <summary>Registration withdrawn by the Board, with written reasons.</summary>
    Withdrawn,

    /// <summary>Ended. Roster and documents stay readable; the Drive folder goes read-only.</summary>
    Dormant
}

/// <summary>Why a group is <see cref="WorkgroupStatus.Dormant"/>. Null unless Dormant.</summary>
internal enum WorkgroupDormantReason
{
    /// <summary>The group delivered its output.</summary>
    Delivered,

    /// <summary>A member marked it done without delivering.</summary>
    Abandoned,

    /// <summary>Closed by the Secretary after the two-month silence.</summary>
    Quiet
}

/// <summary>The artefact a group promises to deliver (the guidance's deliverable sentence).</summary>
internal enum WorkgroupDeliverableKind
{
    Recommendation,
    DraftPolicy,
    DecisionBrief,
    Report,
    AssemblyProposal,
    ResolutionProposal,
    DepartmentRegistration,
    Consultation,
    Event,
    Other
}

/// <summary>Who the deliverable is for.</summary>
internal enum WorkgroupAudience
{
    Board,
    Assembly,
    Community
}

/// <summary>
/// Register-facing role, not a permission level: in v1 every member may edit
/// (design §5). Coordinators are the people named on the register and the
/// addressees of notifications.
/// </summary>
internal enum WorkgroupMemberRole
{
    Member,
    Coordinator
}

/// <summary>
/// One dated line in a group's history. <c>Applied</c> through <c>SurveySent</c> are
/// <b>system</b> kinds — written by the section, never edited. <c>Update</c> through
/// <c>Note</c> are <b>member</b> kinds — written, edited and deleted by members.
/// </summary>
internal enum WorkgroupLogKind
{
    // System kinds.
    Applied,
    Registered,
    Referred,
    Refused,
    Withdrawn,
    Ended,
    Reactivated,
    CoordinatorChanged,
    ScopeChanged,
    MemberJoined,
    MemberLeft,
    DormancyInquiry,
    DocumentPublished,
    CommentPeriodOpened,
    CommentPeriodClosed,
    Delivered,
    DispositionRecorded,
    SurveySubmitted,
    SurveySent,

    // Member kinds.
    Update,
    Disclosure,
    StatusRequested,
    Note
}

/// <summary>What a document is. An <see cref="AnnualReport"/> published in a year satisfies clause 5.</summary>
internal enum WorkgroupDocumentKind
{
    Deliverable,
    AnnualReport,
    Other
}

/// <summary>Document life. <see cref="Delivered"/> freezes the body.</summary>
internal enum WorkgroupDocumentStatus
{
    Draft,
    Published,
    Delivered
}

/// <summary>The Board's written reply to a delivered document.</summary>
internal enum WorkgroupDisposition
{
    Accepted,
    Declined,
    Deferred,
    Noted
}

/// <summary>The group's answer to one comment.</summary>
internal enum WorkgroupCommentDisposition
{
    Pending,
    Accepted,
    Rejected,
    Incorporated,
    Noted
}
