using Humans.Base.Hosting;

namespace Humans.Web.Extensions.Sections;

internal static class AdminSectionExtensions
{
    internal static IServiceCollection AddAdminSection(this IServiceCollection services)
    {
        services.AddAdminDatabaseDiagnostics();

        return services;
    }
}
