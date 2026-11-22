#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false

// Lists every GET page URL the app routes, by reflection over a built Humans.Web output dir (no app boot).
// Usage: dotnet run tools/look-audit/routes/Routes.cs -- <binDir> <out.json>
// Pass absolute paths: dotnet run executes file-based apps from the script's folder.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;

if (args.Length != 2) { Console.Error.WriteLine("usage: Routes.cs -- <binDir> <out.json>"); return 2; }
var binDir = Path.GetFullPath(args[0]);

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var path = Path.Combine(binDir, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};

string[] excludedPrefixes = ["/api", "/dev", "/hangfire", "/health", "/signin-", "/signout-"];

var routes = new List<Route>();
var dropped = new List<Dropped>();
var failed = new List<string>();
var controllerCount = 0;

foreach (var dll in Directory.GetFiles(binDir, "Humans.*.dll").OrderBy(f => f, StringComparer.Ordinal))
{
    Type[] types;
    try { types = Assembly.LoadFrom(dll).GetTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; failed.Add($"{Path.GetFileName(dll)}: partial type load"); }
    catch (Exception ex) { failed.Add($"{Path.GetFileName(dll)}: {ex.GetType().Name}"); continue; }

    foreach (var type in types.Where(IsController))
    {
        controllerCount++;
        try { Process(type); }
        catch (Exception ex) { failed.Add($"{type.FullName}: {ex.GetType().Name} {ex.Message}"); }
    }
}

// Routing is case-insensitive, so two spellings of one template are one URL.
var kept = new List<Route>();
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var r in routes)
{
    if (seen.Add(r.template)) kept.Add(r);
    else dropped.Add(new Dropped(r.template, r.controller, r.action, "duplicate"));
}
kept.Sort((a, b) => string.Compare(a.template, b.template, StringComparison.OrdinalIgnoreCase));
dropped.Sort((a, b) => string.Compare(a.template, b.template, StringComparison.OrdinalIgnoreCase));

var json = JsonSerializer.Serialize(
    new { generatedAt = DateTimeOffset.UtcNow.ToString("O"), routes = kept, dropped },
    new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], json);

Console.WriteLine($"controllers={controllerCount} routes={kept.Count} withParams={kept.Count(r => r.hasParams)} dropped={dropped.Count}");
foreach (var g in dropped.GroupBy(d => d.reason.Split(':')[0]).OrderByDescending(g => g.Count()))
    Console.WriteLine($"  dropped {g.Key}: {g.Count()}");
foreach (var f in failed) Console.WriteLine($"  FAILED {f}");
return 0;

// The shell's SectionControllerFeatureProvider: name ends in Controller or [Controller], non-public allowed.
static bool IsController(Type t) =>
    t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
    && typeof(ControllerBase).IsAssignableFrom(t)
    && !t.IsDefined(typeof(NonControllerAttribute))
    && (t.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) || t.IsDefined(typeof(ControllerAttribute)));

void Process(Type type)
{
    var controllerName = type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ? type.Name[..^"Controller".Length] : type.Name;
    var section = type.Assembly.GetName().Name!.Replace("Humans.", "", StringComparison.Ordinal);
    var area = type.GetCustomAttribute<AreaAttribute>()?.RouteValue ?? "";
    var isApi = type.IsDefined(typeof(ApiControllerAttribute), inherit: true);
    var controllerRoutes = type.GetCustomAttributes<RouteAttribute>(inherit: true).Select(a => a.Template ?? "").ToList();

    var controllerAuth = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
    var controllerAnon = type.IsDefined(typeof(AllowAnonymousAttribute), inherit: true);

    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition
            && m.DeclaringType != typeof(object) && !IsFrameworkType(m.DeclaringType!)
            && !string.Equals(m.Name, "Dispose", StringComparison.Ordinal));

    foreach (var m in methods)
    {
        var actionName = m.GetCustomAttribute<ActionNameAttribute>()?.Name ?? m.Name;
        var fullController = type.FullName!;

        if (m.IsDefined(typeof(NonActionAttribute), inherit: true)) { dropped.Add(new Dropped("", fullController, actionName, "NonAction")); continue; }

        var attrs = m.GetCustomAttributes(inherit: true).ToList();
        var providers = attrs.OfType<IRouteTemplateProvider>().ToList();
        var verbProviders = attrs.OfType<IActionHttpMethodProvider>().ToList();
        var silentVerbs = verbProviders.Where(v => v is not IRouteTemplateProvider { Template: not null }).ToList();

        // Mirrors MVC's selector rules: templated providers each make a route; otherwise one silent selector.
        var selectors = new List<(string? template, IEnumerable<string>? verbs)>();
        foreach (var p in providers.Where(p => p.Template is not null))
            selectors.Add((p.Template, p is IActionHttpMethodProvider own ? own.HttpMethods : Union(silentVerbs)));
        if (selectors.Count == 0)
            selectors.Add((null, Union(silentVerbs)));

        var auth = controllerAuth.Concat(attrs.OfType<AuthorizeAttribute>())
            .Select(a => new Auth(a.Policy, a.Roles)).Distinct().ToList();
        var anon = controllerAnon || attrs.OfType<IAllowAnonymous>().Any();
        var returnType = Unwrap(m.ReturnType);

        foreach (var (template, verbs) in selectors)
        {
            if (verbs is not null && !verbs.Contains("GET", StringComparer.OrdinalIgnoreCase)) continue;

            var urls = new List<string>();
            if (template is null && controllerRoutes.Count == 0)
            {
                // Conventional fallback {controller=Home}/{action=Index}/{id?}
                urls.Add(controllerName.Equals("Home", StringComparison.OrdinalIgnoreCase) && actionName.Equals("Index", StringComparison.OrdinalIgnoreCase) ? "/"
                    : actionName.Equals("Index", StringComparison.OrdinalIgnoreCase) ? $"/{controllerName}" : $"/{controllerName}/{actionName}");
            }
            else if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
                urls.Add(template);
            else
                foreach (var c in controllerRoutes.DefaultIfEmpty(""))
                {
                    urls.Add(Join(c, template ?? ""));
                }

            foreach (var raw in urls)
            {
                var url = Normalize(raw.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                    .Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase)
                    .Replace("[area]", area, StringComparison.OrdinalIgnoreCase), out var ps);

                var reason = DropReason(url, isApi, returnType);
                if (reason is not null) { dropped.Add(new Dropped(url, fullController, actionName, reason)); continue; }
                routes.Add(new Route(url, fullController, actionName, section, ps.Count > 0, ps, anon, auth));
            }
        }
    }
}

string? DropReason(string url, bool isApi, Type returnType)
{
    if (isApi) return "ApiController";
    foreach (var p in excludedPrefixes)
        if (url.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return $"excluded prefix {p}";
    if (url.Contains("webhook", StringComparison.OrdinalIgnoreCase)) return "webhook";
    if (typeof(FileResult).IsAssignableFrom(returnType)) return "non-HTML return: FileResult";
    if (typeof(JsonResult).IsAssignableFrom(returnType)) return "non-HTML return: JsonResult";
    if (typeof(ContentResult).IsAssignableFrom(returnType)) return "non-HTML return: ContentResult";
    return null;
}

static IEnumerable<string>? Union(List<IActionHttpMethodProvider> ps) =>
    ps.Count == 0 ? null : ps.SelectMany(p => p.HttpMethods ?? []).ToList();

static string Join(string a, string b)
{
    a = a.TrimStart('~');
    return string.IsNullOrEmpty(b) ? a : string.IsNullOrEmpty(a) ? b : a.TrimEnd('/') + "/" + b.TrimStart('/');
}

static bool IsFrameworkType(Type t) => t.Assembly.GetName().Name!.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.Ordinal);

static Type Unwrap(Type t)
{
    while (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Task<>) || t.GetGenericTypeDefinition() == typeof(ValueTask<>)
        || t.GetGenericTypeDefinition() == typeof(ActionResult<>)))
        t = t.GetGenericArguments()[0];
    return t;
}

// Leading slash, no trailing slash; optional/defaulted params dropped, constraints stripped, catch-all {*p} -> {p}.
static string Normalize(string template, out List<string> ps)
{
    ps = [];
    var segments = new List<string>();
    foreach (var seg in template.TrimStart('~', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        var m = Regex.Match(seg, @"^\{(\*{0,2})([^:=?}]+)([^}]*)\}$");
        if (m.Success)
        {
            if (m.Groups[3].Value.Contains('?') || m.Groups[3].Value.Contains('=')) continue;
            ps.Add(m.Groups[2].Value);
            segments.Add("{" + m.Groups[2].Value + "}");
        }
        else segments.Add(seg);
    }
    return "/" + string.Join('/', segments);
}

record Auth(string? policy, string? roles);
record Route(string template, string controller, string action, string section, bool hasParams, List<string> @params, bool anonymous, List<Auth> authorize);
record Dropped(string template, string controller, string action, string reason);
