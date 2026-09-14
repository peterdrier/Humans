namespace Humans.Rideshare.Domain;

/// <summary>
/// The coarse "stuff" axis, shared by offers (capacity) and requests (load):
/// a bag or two, a few bags, a trunkful, a van-load.
/// </summary>
internal enum LuggageSize
{
    Minimal,
    Moderate,
    Lots,
    Huge
}
