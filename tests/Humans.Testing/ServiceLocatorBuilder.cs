using NSubstitute;

namespace Humans.Testing;

/// <summary>
/// Builds <see cref="IServiceProvider"/> substitutes. Trap: <c>.With(instance)</c> registers under the argument's
/// static type, so pass a concrete instance as <c>.With&lt;IFoo&gt;(foo)</c>.
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
