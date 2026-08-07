namespace Humans.Web.Models.Tables;

public enum TableMode
{
    /// <summary>All rows rendered; sorting/filtering happen in the browser (site.js). The default.</summary>
    Client,

    /// <summary>Sorting/filtering/paging round-trip via query params; renderer emits sort links + GET filter form.</summary>
    Server,
}

public enum CellFormat
{
    Text,
    Date,
    DateTime,
    Currency,
    Number,
    EnumBadge,
    BoolIcon,
    Template,
}

public enum ColumnFilterKind
{
    None,
    Text,
    Select,
}
