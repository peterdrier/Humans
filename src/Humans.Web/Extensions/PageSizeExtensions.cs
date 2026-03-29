namespace Humans.Web.Extensions;

public static class PageSizeExtensions
{
    public static int ClampPageSize(this int pageSize, int min = 1, int max = 250) =>
        Math.Clamp(pageSize, min, max);
}
