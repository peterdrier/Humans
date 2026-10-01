namespace Humans.MailerLite.Services.Dtos;

/// <summary>Where Humans' marketing preference and the MailerLite <c>Website</c> list disagree.</summary>
internal sealed record DriftReport(
    int HumansOptedOutMlActive,           // legal-trouble row
    int? HumansOptedInMlAbsent);          // service-quality row (never computed — a seam, see health.md)
