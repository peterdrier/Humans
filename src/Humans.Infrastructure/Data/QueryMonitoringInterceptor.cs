using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Infrastructure.Data;

/// <summary>
/// EF Core interceptor that tracks query execution counts and timings in memory,
/// grouped by operation type (SELECT/INSERT/UPDATE/DELETE) and table name.
/// </summary>
public partial class QueryMonitoringInterceptor : DbCommandInterceptor
{
    private readonly QueryStatistics _statistics;

    public QueryMonitoringInterceptor(QueryStatistics statistics)
    {
        _statistics = statistics;
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData,
        object? result)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        RecordExecution(command.CommandText, eventData.Duration);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void RecordExecution(string commandText, TimeSpan duration)
    {
        var (operation, table) = ParseCommand(commandText);
        if (operation is not null && table is not null)
        {
            _statistics.Record(operation, table, duration.TotalMilliseconds);
        }
    }

    internal static (string? Operation, string? Table) ParseCommand(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return (null, null);

        var trimmed = commandText.TrimStart();

        // Determine operation from the first SQL keyword
        string? operation = null;
        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            operation = "SELECT";
        else if (trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            operation = "INSERT";
        else if (trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            operation = "UPDATE";
        else if (trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            operation = "DELETE";

        if (operation is null)
            return (null, null);

        // Extract table name based on operation
        var table = operation switch
        {
            "SELECT" => ExtractFromClause(trimmed),
            "INSERT" => ExtractInsertTable(trimmed),
            "UPDATE" => ExtractUpdateTable(trimmed),
            "DELETE" => ExtractFromClause(trimmed),
            _ => null
        };

        if (table is null)
            return (null, null);

        // Strip schema prefix (e.g., "public.profiles" -> "profiles")
        var dotIndex = table.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex >= 0 && dotIndex < table.Length - 1)
            table = table[(dotIndex + 1)..];

        // Remove surrounding quotes
        table = table.Trim('"');

        return (operation, table);
    }

    private static string? ExtractFromClause(string sql)
    {
        var match = FromTablePattern().Match(sql);
        return match.Success ? match.Groups["table"].Value : null;
    }

    private static string? ExtractInsertTable(string sql)
    {
        var match = InsertTablePattern().Match(sql);
        return match.Success ? match.Groups["table"].Value : null;
    }

    private static string? ExtractUpdateTable(string sql)
    {
        var match = UpdateTablePattern().Match(sql);
        return match.Success ? match.Groups["table"].Value : null;
    }

    [GeneratedRegex(@"\bFROM\s+(?<table>""?[\w.]+""?)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FromTablePattern();

    [GeneratedRegex(@"\bINSERT\s+INTO\s+(?<table>""?[\w.]+""?)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex InsertTablePattern();

    [GeneratedRegex(@"\bUPDATE\s+(?<table>""?[\w.]+""?)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex UpdateTablePattern();
}
