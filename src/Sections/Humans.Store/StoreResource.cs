namespace Humans.Store;

/// <summary>
/// Marker type for Store's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Store</c> — <c>Humans.Store.Resources</c> would make every
/// Store string fall back to its raw key at runtime (design §3).
/// </summary>
public class StoreResource;
