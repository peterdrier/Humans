using System.Reflection;
using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Humans.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Guards the per-section DbContext split (nobodies-collective/Humans#858):
/// every <see cref="IEntityTypeConfiguration{TEntity}"/> must be applied by exactly
/// one context's model.
/// </summary>
/// <remarks>
/// <para>
/// The failure this catches: a configuration added under a section namespace
/// but never applied by that section's context (every context applies its
/// configurations explicitly). The entity is then mapped
/// by <b>no</b> context — no migration is ever generated for it and
/// <c>has-pending-model-changes</c> stays green, because no model describes the
/// table. The mirror failure, an entity mapped by two contexts, means two
/// migration chains own the same table.
/// </para>
/// <para>
/// Models are built against the real Npgsql provider (no database is contacted —
/// <c>DbContext.Model</c> only needs a provider to resolve type mappings), so
/// this sees exactly the model <c>dotnet ef</c> sees.
/// </para>
/// <para>
/// Contexts and configurations are discovered from what DI actually registers, not
/// by reflecting over <c>typeof(UsersDbContext).Assembly</c>: a section that moves
/// into its own project (nobodies-collective/Humans#866, G5) takes its context and
/// its configurations with it, and an assembly-anchored scan would drop that section
/// from the guard without failing.
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

    /// <summary>
    /// The authoritative context list — the one the startup migrator uses. Section
    /// contexts arrive via <c>AddSectionDbContext</c>, whether that call sits in
    /// <c>AddHumansPersistence</c> or in a moved section's <c>ISection.Register</c>.
    /// No provider is built: the registrations are read straight off the descriptors.
    /// </summary>
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
