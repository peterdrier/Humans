namespace Humans.Web.Models;

public abstract class PagedListViewModel
{
    private int _pageNumber = 1;

    protected PagedListViewModel(int defaultPageSize = 20)
    {
        PageSize = defaultPageSize;
    }

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

    public int PageSize { get; set; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
