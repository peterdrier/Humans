namespace Humans.Gate.Services;

/// <summary>
/// Applied to both the scanned value and the stored barcode, so a keyboard-wedge scan and manual entry match. Also the
/// admit dedupe key.
/// </summary>
internal static class GateBarcode
{
    public static string Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToUpperInvariant();
}
