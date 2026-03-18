namespace Humans.Web.Models;

public abstract class PagedListViewModel
{
    protected PagedListViewModel(int defaultPageSize = 20)
    {
        PageSize = defaultPageSize;
    }

    public int TotalCount { get; set; }

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
