namespace Humans.Auth.Contracts;

public interface ICurrentUserContext
{
    Guid? UserId { get; }
}
