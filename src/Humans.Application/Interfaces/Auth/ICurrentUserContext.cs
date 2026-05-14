namespace Humans.Application.Interfaces.Auth;

public interface ICurrentUserContext
{
    Guid? UserId { get; }
}
