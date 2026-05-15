namespace Humans.Application.Interfaces.Auth;

public interface IAdminAuthorizationService : IApplicationService
{
    Task RequireCurrentUserIsAdminAsync(CancellationToken cancellationToken = default);
}
