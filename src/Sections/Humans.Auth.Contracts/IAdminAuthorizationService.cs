using Humans.Application.Interfaces;
namespace Humans.Auth.Contracts;

public interface IAdminAuthorizationService : IApplicationService
{
    Task RequireCurrentUserIsAdminAsync(CancellationToken cancellationToken = default);
}
