using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Hosting;
using Humans.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Architecture;

/// <summary>Every <see cref="IEntityTypeConfiguration{TEntity}"/> is applied by exactly one context's model.</summary>
/// <remarks>
/// <para>
/// A configuration no context applies maps a table no migration will ever create, and
/// <c>has-pending-model-changes</c> stays green. One applied by two contexts gives a table two migration chains.
/// </para>
/// <para>
/// Contexts come from what DI registers, not an assembly anchor, so a section that moves projects stays covered.
/// Models are built on the real Npgsql provider, so this sees what <c>dotnet ef</c> sees.
/// </para>
/// </remarks>
public class DbContextEntityOwnershipTests
{
    // Never opened: building the model resolves type mappings, it does not connect.
    private const string DesignTimeConnectionString =
        "Host=localhost;Database=humans_design_time;Username=humans;Password=humans";

    [HumansFact]
    public void EveryEntityConfiguration_IsMappedBy_ExactlyOneDbContext()
    {
        var contextTypes = RegisteredContextTypes();
        contextTypes.Should().HaveCountGreaterThan(1, "the section contexts must be discovered too");

        var configurationsByEntity = ConfigurationsByEntity(
            contextTypes.Select(t => t.Assembly).Distinct());
        configurationsByEntity.Should().NotBeEmpty(
            "the guard is meaningless if no IEntityTypeConfiguration is discovered");

        var contextsByEntity = new Dictionary<Type, List<string>>();
        foreach (var contextType in contextTypes)
        {
            using var db = CreateContext(contextType);
            foreach (var clrType in db.Model.GetEntityTypes().Select(e => e.ClrType).Distinct())
            {
                if (!contextsByEntity.TryGetValue(clrType, out var contexts))
                    contextsByEntity[clrType] = contexts = [];
                contexts.Add(contextType.Name);
            }
        }

        var offenders = new List<string>();
        foreach (var (entity, configurations) in configurationsByEntity.OrderBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            List<string> owners = contextsByEntity.TryGetValue(entity, out var found) ? found : [];
            if (owners.Count == 1)
                continue;

            var configured = string.Join(", ", configurations.Order(StringComparer.Ordinal));
            offenders.Add(owners.Count == 0
                ? $"{entity.Name} ({configured}) is mapped by NO DbContext — no section context applies its configuration"
                : $"{entity.Name} ({configured}) is mapped by {owners.Count} DbContexts ({string.Join(", ", owners.Order(StringComparer.Ordinal))}) — a table must have exactly one owning context");
        }

        offenders.Should().BeEmpty(
            "every IEntityTypeConfiguration must be applied by exactly one DbContext");
    }

    private static Dictionary<Type, List<string>> ConfigurationsByEntity(IEnumerable<Assembly> assemblies)
    {
        var byEntity = new Dictionary<Type, List<string>>();
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
                     .Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var iface in type.GetInterfaces()
                         .Where(i => i.IsGenericType &&
                                     i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)))
            {
                var entity = iface.GetGenericArguments()[0];
                if (!byEntity.TryGetValue(entity, out var configurations))
                    byEntity[entity] = configurations = [];
                configurations.Add(type.Name);
            }
        }

        return byEntity;
    }

    /// <summary>The list the startup migrator uses, read off the DI descriptors without building a provider.</summary>
    private static IReadOnlyList<Type> RegisteredContextTypes()
    {
        var services = new ServiceCollection()
            .AddHumansPersistence()
            .AddDiscoveredSections(new ConfigurationBuilder().Build());

        return
        [
            .. services.Select(d => d.ImplementationInstance)
                .OfType<SectionDbContextRegistration>()
                .Select(r => r.ContextType)
                .OrderBy(t => t.Name, StringComparer.Ordinal),
        ];
    }

    private static DbContext CreateContext(Type contextType)
    {
        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        optionsBuilder.UseNpgsql(DesignTimeConnectionString, npgsql => npgsql.UseNodaTime());
        return (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;
    }
}
