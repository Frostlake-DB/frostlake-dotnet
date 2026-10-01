using System.Globalization;

namespace Frostlake.Data.Testkit;

/// <summary>
/// What one statement produced, in the shape the corpus checks: the first result's column names and
/// its grid as wire text (a null cell is SQL NULL, a null grid means no result at all), the DML count,
/// or the engine's refusal.
/// </summary>
internal sealed class StepOutcome
{
    private StepOutcome(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>>? rows, string? error)
    {
        Columns = columns;
        Rows = rows;
        Error = error;
        UpdateCount = DeriveUpdateCount(columns, rows);
    }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<string?>>? Rows { get; }

    /// <summary>The DML count, or -1 when the result is not a count grid.</summary>
    public long UpdateCount { get; }

    /// <summary>The engine's refusal, or null when the statement succeeded.</summary>
    public string? Error { get; }

    public bool Failed => Error is not null;

    public static StepOutcome Grid(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>>? rows)
    {
        return new StepOutcome(columns, rows, null);
    }

    public static StepOutcome Refused(string message)
    {
        return new StepOutcome(Array.Empty<string>(), null, message);
    }

    /// <summary>
    /// The engine reports a DML count as a result grid ("number of rows inserted" …), and the corpus
    /// derives the count from it the way the reference runner does: a single row whose columns are all
    /// named "number of …" counts its first cell.
    /// </summary>
    private static long DeriveUpdateCount(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>>? rows)
    {
        if (columns.Count == 0 || rows is not { Count: 1 } || rows[0].Count == 0)
        {
            return -1;
        }
        foreach (var column in columns)
        {
            if (!column.StartsWith("number of", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
        }
        return long.TryParse(rows[0][0]?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
            out var count)
            ? count
            : -1;
    }
}
