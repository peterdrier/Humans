namespace Humans.Agent;

/// <summary>
/// Marker type for Agent's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Agent</c> —
/// <c>Humans.Agent.Resources</c> would make every Agent string fall back to its raw key at
/// runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence.
/// </remarks>
public class AgentResource;
