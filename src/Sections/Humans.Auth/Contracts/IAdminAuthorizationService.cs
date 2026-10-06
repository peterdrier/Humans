using Humans.Base.Interfaces;
namespace Humans.Auth.Contracts;

public interface IAdminAuthorizationService : IApplicationService
{
    Task RequireCurrentUserIsAdminAsync(CancellationToken cancellationToken = default);
}
