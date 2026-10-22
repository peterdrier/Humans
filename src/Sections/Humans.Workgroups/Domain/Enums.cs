namespace Humans.Workgroups.Domain;

/// <summary>Every transition out of <see cref="Applied"/> is a human action by the Secretary or the Board.</summary>
internal enum WorkgroupStatus
{
    /// <summary>Waiting on the Secretary. The fourteen-day clock runs from AppliedAt.</summary>
    Applied,

    /// <summary>A refusal ground may apply; the Board decides at its next meeting.</summary>
    Referred,

    Active,

    Refused,

    /// <summary>Withdrawn by the Board, with written reasons.</summary>
    Withdrawn,

    /// <summary>Ended. Roster and documents stay readable; the Drive folder goes read-only.</summary>
    Dormant
}

internal enum WorkgroupDormantReason
{
    Delivered,

    /// <summary>A member marked it done without delivering.</summary>
    Abandoned,

    /// <summary>Closed by the Secretary for silence; never a member's own ending.</summary>
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

internal enum WorkgroupAudience
{
    Board,
    Assembly,
    Community
}

/// <summary>Not a permission level: every member may edit. Only membership decisions are the coordinators'.</summary>
internal enum WorkgroupMemberRole
{
    Member,
    Coordinator
}

/// <summary>
/// Stored as strings: never rename or drop a member. System kinds are written by the section and never edited;
/// member kinds are written, edited and deleted by members.
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
    BudgetSet,

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

internal enum WorkgroupDisposition
{
    Accepted,
    Declined,
    Deferred,
    Noted
}

internal enum WorkgroupCommentDisposition
{
    Pending,
    Accepted,
    Rejected,
    Incorporated,
    Noted
}
