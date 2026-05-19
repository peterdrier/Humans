using NSubstitute;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Fluent builder for the <see cref="IServiceProvider"/> substitutes that service
/// tests assemble to satisfy services-with-locator dependencies. Replaces the
/// repeated pattern:
/// <code>
/// var serviceProvider = Substitute.For&lt;IServiceProvider&gt;();
/// serviceProvider.GetService(typeof(IFoo)).Returns(_foo);
/// serviceProvider.GetService(typeof(IBar)).Returns(Substitute.For&lt;IBar&gt;());
/// </code>
/// with:
/// <code>
/// var serviceProvider = new ServiceLocatorBuilder()
///     .With(_foo)
///     .With&lt;IBar&gt;()
///     .Build();
/// </code>
/// The <see cref="With{T}()"/> overload allocates a bare substitute; pass an
/// instance to <see cref="With{T}(T)"/> when the test needs to assert on it
/// or pre-configure behavior.
///
/// <para><b>Inference footgun:</b> <c>.With(instance)</c> infers <c>T</c> from
/// the argument's static type — if you pass a concrete class (e.g.
/// <c>RoleAssignmentService</c>), the registration is under the concrete type
/// and <c>GetRequiredService&lt;IRoleAssignmentService&gt;()</c> will fail.
/// Always spell out the interface when passing a concrete instance:
/// <c>.With&lt;IRoleAssignmentService&gt;(roleAssignmentService)</c>.</para>
/// </summary>
public sealed class ServiceLocatorBuilder
{
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public ServiceLocatorBuilder With<T>(T instance) where T : class
    {
        _provider.GetService(typeof(T)).Returns(instance);
        return this;
    }

    public ServiceLocatorBuilder With<T>() where T : class =>
        With(Substitute.For<T>());

    public IServiceProvider Build() => _provider;
}
