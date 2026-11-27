using Humans.Backdoor.Services;
using Humans.Base.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humans.Backdoor.Mcp;

/// <summary>
/// Denies by default, where the SDK allows: a tool is listed and callable only when it declares a
/// <see cref="BackdoorScopeAttribute"/> and that scope admits the caller, read fresh on every request.
/// </summary>
internal static class McpToolScopeGate
{
    public static void Install(McpServerOptions options)
    {
        options.Filters.Request.ListToolsFilters.Add(next => async (context, ct) =>
        {
            var result = await next(context, ct);
            var admitted = new List<Tool>(result.Tools.Count);
            foreach (var tool in result.Tools)
            {
                var primitive = options.ToolCollection is { } tools && tools.TryGetPrimitive(tool.Name, out var found)
                    ? found
                    : null;
                if (await AdmitsAsync(primitive, context, ct))
                    admitted.Add(tool);
            }

            result.Tools = admitted;
            return result;
        });

        options.Filters.Request.CallToolFilters.Add(next => async (context, ct) =>
        {
            if (context.MatchedPrimitive is not null && !await AdmitsAsync(context.MatchedPrimitive, context, ct))
                throw new McpProtocolException("You may not use this tool.", McpErrorCode.InvalidRequest);

            return await next(context, ct);
        });
    }

    private static async Task<bool> AdmitsAsync<TParams>(
        IMcpServerPrimitive? primitive, RequestContext<TParams> context, CancellationToken ct)
    {
        if (primitive?.Metadata.OfType<BackdoorScopeAttribute>().FirstOrDefault() is not { } scope)
            return false;
        if (context.User.GetUserId() is not { } userId || context.Services is null)
            return false;

        var keys = context.Services.GetRequiredService<IBackdoorApiKeyService>();
        return await keys.AdmitsAsync(userId, scope.Scope, ct);
    }
}
