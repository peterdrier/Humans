namespace Humans.Domain.Enums;

/// <summary>
/// Types of legal documents that require member consent.
/// </summary>
public enum DocumentType
{
    /// <summary>
    /// Privacy policy document (GDPR).
    /// </summary>
    PrivacyPolicy = 0,

    /// <summary>
    /// Terms and conditions of membership.
    /// </summary>
    TermsAndConditions = 1,

    /// <summary>
    /// Code of conduct for members.
    /// </summary>
    CodeOfConduct = 2,

    /// <summary>
    /// Data processing agreement.
    /// </summary>
    DataProcessingAgreement = 3,

    /// <summary>
    /// Association statutes/bylaws.
    /// </summary>
    Statutes = 4
}
