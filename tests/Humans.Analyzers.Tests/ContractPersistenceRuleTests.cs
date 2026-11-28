using AwesomeAssertions;
using Xunit;

namespace Humans.Analyzers.Tests;

public class ContractPersistenceRuleTests
{
    private const string Stubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }
            public class DbSet<T> { }
        }
        namespace Microsoft.EntityFrameworkCore.ChangeTracking
        {
            public class EntityEntry<T> { }
        }
        namespace Microsoft.AspNetCore.Identity
        {
            public class IdentityUser<T> { }
        }
        namespace Microsoft.AspNetCore.Identity.EntityFrameworkCore
        {
            public class IdentityDbContext<T> : Microsoft.EntityFrameworkCore.DbContext { }
        }
        namespace Persistence
        {
            public class LocalContext : Microsoft.EntityFrameworkCore.DbContext { }
            public interface LocalQuery : System.Linq.IQueryable<string> { }
        }
        """;

    [HumansTheory]
    [InlineData("Microsoft.EntityFrameworkCore.DbContext")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet<string>")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<string>")]
    [InlineData("Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<string>")]
    [InlineData("Persistence.LocalContext")]
    [InlineData("Persistence.LocalQuery")]
    [InlineData("System.Linq.IQueryable<string>")]
    public async Task Analyze_NestedContractReturnType_Fires(string persistenceType)
    {
        var source = $$"""
            namespace Humans.Test.Contracts
            {
                public interface IRead
                {
                    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<{{persistenceType}}[]>> Read();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test", source, Stubs);

        diagnostics.Should().ContainSingle(d => d.Id == "HUM0037" && d.GetMessage().Contains(persistenceType, StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Analyze_ContractLeafProject_Fires()
    {
        const string source = """
            namespace Api
            {
                public interface IRead { Microsoft.EntityFrameworkCore.DbContext Read(); }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test.Contracts", source, Stubs);

        diagnostics.Should().ContainSingle(d => d.Id == "HUM0037");
    }

    [HumansFact]
    public async Task Analyze_ContractFolderOtherNamespace_Fires()
    {
        const string source = """
            namespace Api
            {
                public interface IRead { Microsoft.EntityFrameworkCore.DbContext Read(); }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test", source, Stubs,
            sourcePath: "/repo/src/Sections/Humans.Test/Contracts/IRead.cs");

        diagnostics.Should().ContainSingle(d => d.Id == "HUM0037");
    }

    [HumansFact]
    public async Task Analyze_ContractImplementationBody_Fires()
    {
        const string source = """
            namespace Humans.Test.Contracts
            {
                public class Helper
                {
                    public object Read() => new Microsoft.EntityFrameworkCore.DbContext();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test", source, Stubs);

        diagnostics.Should().ContainSingle(d => d.Id == "HUM0037");
    }

    [HumansFact]
    public async Task Analyze_PersistenceOutsideContracts_IsSilent()
    {
        const string source = """
            namespace Humans.Test.Data
            {
                public class Repository { public Microsoft.EntityFrameworkCore.DbSet<string> Rows { get; } }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test", source, Stubs);

        diagnostics.Should().NotContain(d => d.Id == "HUM0037");
    }

    [HumansFact]
    public async Task Analyze_UserModelsOrProjections_IsSilent()
    {
        const string source = """
            namespace Humans.Test.Contracts
            {
                public class User : Microsoft.AspNetCore.Identity.IdentityUser<System.Guid> { }
                public record Info(System.Guid Id);
                public interface IRead
                {
                    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Info>> Read();
                    User Find();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(new SectionRulesAnalyzer(), "Humans.Test.Contracts", source, Stubs);

        diagnostics.Should().NotContain(d => d.Id == "HUM0037");
    }
}
