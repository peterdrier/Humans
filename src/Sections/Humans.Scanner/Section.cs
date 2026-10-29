using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Scanner;

/// <summary>
/// <c>Register</c> is empty, but the type must stay: implementing <c>ISection</c> is what makes this assembly a
/// section. Without it the pages 404 with a green build.
/// </summary>
public sealed class Section : ISection, IIssueQueueOwner, ISectionAdminNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }

    string IIssueQueueOwner.QueueKey => "Scanner";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Scanner";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["scanner"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [TicketsRoles.TicketsAdmin, RoleNames.Board];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Scanner", [
            new("Scanner", "Scanner", "Index", null, null, "fa-solid fa-qrcode", PolicyNames.ScannerAccess)
        ])
    ];
}
