using Humans.Users.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users.Contracts;

/// <summary>
/// The one line of Shell wiring the section cannot register itself:
/// <c>AddIdentity&lt;User, …&gt;()</c> returns an <see cref="IdentityBuilder"/> that has to be
/// told which <c>DbContext</c> holds the Identity tables, and only the section knows.
/// </summary>
/// <remarks>
/// It was <c>InfrastructureServiceCollectionExtensions.AddHumansEntityFrameworkStores</c> —
/// "typed wrapper so Web never references UsersDbContext directly" — and moved in with
/// <c>UsersDbContext</c> at the section move (nobodies-collective/Humans#866, G5 lane 2, PR B).
/// The wrapper's whole point survives the move: <c>UsersDbContext</c> is <c>internal sealed</c>
/// and Shell still never names it.
/// </remarks>
public static class UsersIdentityExtensions
{
    /// <summary>Points ASP.NET Identity's stores at the section's own <c>DbContext</c>.</summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<UsersDbContext>();
}
