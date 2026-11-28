using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Base.Interfaces;

/// <summary>
/// The base of every section's <c>Section</c> entry point: the section's identity and the helpers its contributions
/// share. Seams stay opt-in interfaces on the derived class, so its header still lists what the section contributes.
/// </summary>
public abstract class SectionBase : ISection
{
    private const string AssemblyPrefix = "Humans.";
    private const string ControllerSuffix = "Controller";

    /// <summary>
    /// The section's assembly name without <c>Humans.</c>; the default for <see cref="DisplayName"/>.
    /// </summary>
    protected string Name => GetType().Assembly.GetName().Name![AssemblyPrefix.Length..];

    /// <summary>
    /// The name people see: the admin sidebar group label. Title Case; override where it differs from
    /// <see cref="Name"/> (<c>LowIncome</c> shows as <c>Low Income</c>).
    /// </summary>
    public virtual string DisplayName => Name;

    /// <inheritdoc />
    public virtual bool IsActive => true;

    /// <inheritdoc />
    public abstract void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>The section's own admin sidebar group, labelled <see cref="DisplayName"/>.</summary>
    protected AdminNavGroup AdminGroup(IReadOnlyList<AdminNavItem> items) => new(DisplayName, items);

    /// <summary>
    /// An admin nav item that names its controller by type, so renaming the controller fails the build instead of
    /// the nav. Pass the action as <c>nameof(TController.Action)</c>.
    /// </summary>
    protected static AdminNavItem AdminLink<TController>(
        string label,
        string action,
        string iconCssClass,
        string? policy,
        Func<IServiceProvider, ValueTask<int?>>? pillCount = null,
        Func<IWebHostEnvironment, bool>? environmentGate = null,
        int weight = 0)
        where TController : ControllerBase =>
        new(label, ControllerName<TController>(), action, null, null, iconCssClass, policy,
            PillCount: pillCount, EnvironmentGate: environmentGate, Weight: weight);

    private static string ControllerName<TController>() where TController : ControllerBase
    {
        var name = typeof(TController).Name;
        return name.EndsWith(ControllerSuffix, StringComparison.Ordinal) ? name[..^ControllerSuffix.Length] : name;
    }
}
