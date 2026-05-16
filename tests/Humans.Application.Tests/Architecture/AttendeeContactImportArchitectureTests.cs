using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;

namespace Humans.Application.Tests.Architecture;

public class AttendeeContactImportArchitectureTests
{
    [HumansFact]
    public void Service_DependsOnExpectedAbstractions()
    {
        var ctor = typeof(AttendeeContactImportService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        paramTypes.Should().Contain(typeof(ITicketRepository));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(IAccountProvisioningService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
        paramTypes.Should().Contain(typeof(ITicketQueryService));
        paramTypes.Should().Contain(typeof(IAuditLogService));
    }
}
