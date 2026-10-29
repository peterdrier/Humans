namespace Humans.Preapprovals.Services;

/// <summary>Constants rather than literals, so a renamed key is a build error, not a raw key on the page.</summary>
internal static class PreapprovalErrorKeys
{
    public const string NotFound = "Preapprovals_Error_NotFound";
    public const string NameRequired = "Preapprovals_Error_NameRequired";
    public const string PerPersonAmountRequired = "Preapprovals_Error_PerPersonAmountRequired";
    public const string PerPersonAmountNotAllowed = "Preapprovals_Error_PerPersonAmountNotAllowed";
    public const string AmountPositive = "Preapprovals_Error_AmountPositive";
    public const string AmountPrecision = "Preapprovals_Error_AmountPrecision";
    public const string AmountTooLarge = "Preapprovals_Error_AmountTooLarge";
    public const string AmountNotAllowed = "Preapprovals_Error_AmountNotAllowed";
    public const string AmountRequired = "Preapprovals_Error_AmountRequired";
    public const string AccountNotFound = "Preapprovals_Error_AccountNotFound";
    public const string KindLocked = "Preapprovals_Error_KindLocked";
    /// <summary>{0} = remaining headroom.</summary>
    public const string CeilingExceeded = "Preapprovals_Error_CeilingExceeded";
    /// <summary>{0} = what is already committed.</summary>
    public const string CeilingBelowCommitted = "Preapprovals_Error_CeilingBelowCommitted";
    public const string ProgramClosed = "Preapprovals_Error_ProgramClosed";
    public const string NotAcceptingApplications = "Preapprovals_Error_NotAcceptingApplications";
    public const string DeadlinePassed = "Preapprovals_Error_DeadlinePassed";
    public const string WrongStatus = "Preapprovals_Error_WrongStatus";
    public const string NoBeneficiaries = "Preapprovals_Error_NoBeneficiaries";
    public const string TextTooLong = "Preapprovals_Error_TextTooLong";
    public const string IssueManyFixedOnly = "Preapprovals_Error_IssueManyFixedOnly";
}
