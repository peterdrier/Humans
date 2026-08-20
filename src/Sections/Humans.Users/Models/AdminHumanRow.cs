using Humans.Users.Contracts;
namespace Humans.Users.Models;

internal sealed record AdminHumanRow(
    Guid UserId,
    string Email,
    string DisplayName,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    UserState State)
{
    /// <summary>Status-column label. Read by the sort comparer and by <c>AdminList</c>'s rows.</summary>
    public string StatusLabel =>
        State == UserState.DeletePending ? "Delete Pending" : State.ToString();
}
