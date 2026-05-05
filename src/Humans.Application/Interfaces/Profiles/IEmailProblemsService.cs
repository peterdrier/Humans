using Humans.Application.DTOs.EmailProblems;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Scans every UserEmail invariant violation surface for the
/// <c>/Profile/Admin/EmailProblems</c> page. Consumes only existing section
/// services — never any <c>I*Repository</c> or <c>DbContext</c>.
/// </summary>
public interface IEmailProblemsService
{
    Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default);
}
