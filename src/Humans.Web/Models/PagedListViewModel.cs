namespace Humans.Web.Models;

public abstract class PagedListViewModel(int defaultPageSize = 20)
{
    private int _pageNumber = 1;

    public int TotalCount { get; set; }

    public int PageNumber
    {
        get => _pageNumber;
        set => _pageNumber = value;
    }

    public int Page
    {
        get => _pageNumber;
        set => _pageNumber = value;
    }

    public int PageSize { get; set; } = defaultPageSize;

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
