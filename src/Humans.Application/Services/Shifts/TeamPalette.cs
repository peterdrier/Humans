using System.Security.Cryptography;
using System.Text;

namespace Humans.Application.Services.Shifts;

public static class TeamPalette
{
    // 20 distinct hues, all dark enough that white bold text reads on top.
    // Order matters only for determinism — changing it shifts every team's color.
    private static readonly string[] Palette =
    [
        "#1F77B4", "#FF7F0E", "#2CA02C", "#D62728", "#9467BD",
        "#8C564B", "#E377C2", "#7F7F7F", "#BCBD22", "#17BECF",
        "#393B79", "#637939", "#8C6D31", "#843C39", "#7B4173",
        "#3182BD", "#E6550D", "#31A354", "#756BB1", "#636363",
    ];

    public static string ColorFor(Guid teamId)
    {
        // Guid.ToString("D") locked by spec — see 2026-05-23-volunteer-tracking-export-design.md
        var idString = teamId.ToString("D");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idString));
        var index = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return Palette[index % (uint)Palette.Length];
    }
}
