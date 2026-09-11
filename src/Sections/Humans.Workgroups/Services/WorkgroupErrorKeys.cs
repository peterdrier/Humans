namespace Humans.Workgroups.Services;

/// <summary>
/// The resource keys <see cref="WorkgroupRuleException"/> throws. Named constants
/// rather than literals so a renamed key is a build error, not a raw key on the page.
/// </summary>
internal static class WorkgroupErrorKeys
{
    public const string NotFound = "Workgroups_Error_NotFound";
    public const string NotAMember = "Workgroups_Error_NotAMember";
    public const string AlreadyAMember = "Workgroups_Error_AlreadyAMember";
    public const string Frozen = "Workgroups_Error_Frozen";
    public const string NameRequired = "Workgroups_Error_NameRequired";
    public const string PurposeRequired = "Workgroups_Error_PurposeRequired";
    public const string DeliverableRequired = "Workgroups_Error_DeliverableRequired";
    public const string ReasonsRequired = "Workgroups_Error_ReasonsRequired";
    public const string NameTaken = "Workgroups_Error_NameTaken";
    public const string CoordinatorCount = "Workgroups_Error_CoordinatorCount";
    public const string CoordinatorsMustBeMembers = "Workgroups_Error_CoordinatorsMustBeMembers";
    public const string LastCoordinatorNeedsReplacement = "Workgroups_Error_LastCoordinatorNeedsReplacement";
    public const string StatusRequestOnCooldown = "Workgroups_Error_StatusRequestOnCooldown";
    public const string RootFolderNotConfigured = "Workgroups_Error_RootFolderNotConfigured";
    public const string DriveFolderCreationFailed = "Workgroups_Error_DriveFolderCreationFailed";
    public const string WrongStatus = "Workgroups_Error_WrongStatus";
    public const string SystemLogEntry = "Workgroups_Error_SystemLogEntry";
    public const string BodyRequired = "Workgroups_Error_BodyRequired";
    public const string DocumentFrozen = "Workgroups_Error_DocumentFrozen";
    public const string NotPublished = "Workgroups_Error_NotPublished";
    public const string CategoriesRequired = "Workgroups_Error_CategoriesRequired";
    public const string WindowInvalid = "Workgroups_Error_WindowInvalid";
    public const string CommentsClosed = "Workgroups_Error_CommentsClosed";
    public const string CommentsStillOpen = "Workgroups_Error_CommentsStillOpen";
    public const string UnknownCategory = "Workgroups_Error_UnknownCategory";
    public const string NotDelivered = "Workgroups_Error_NotDelivered";
    public const string HideReasonRequired = "Workgroups_Error_HideReasonRequired";
    public const string DoneReasonInvalid = "Workgroups_Error_DoneReasonInvalid";
}
