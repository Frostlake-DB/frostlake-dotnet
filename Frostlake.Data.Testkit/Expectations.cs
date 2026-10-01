using System.Text;
using System.Text.Json;

namespace Frostlake.Data.Testkit;

/// <summary>
/// Checks one step's <c>expect</c> block against what the statement produced, in the reference
/// runner's order and wording: an expected refusal first, then an unexpected one, then <c>value</c>,
/// <c>rows</c>, <c>rowCount</c>, <c>columns</c> and <c>updateCount</c>. The first mismatch decides.
/// </summary>
internal static class Expectations
{
    /// <summary>
    /// The one capability this transport lacks: the engine reports a refusal as a message, with no
    /// error code or SQLSTATE beside it. A check that needs them is recorded, not failed.
    /// </summary>
    public const string MissingErrorCode = "ERROR_CODE: cannot check error code/sqlState (backend reports message only)";

    private const char CellSeparator = '\u001f';

    public static Verdict Check(JsonElement? expect, StepOutcome outcome)
    {
        var expected = expect is { ValueKind: JsonValueKind.Object } block ? block : (JsonElement?)null;
        if (expected is { } withError && withError.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object)
        {
            return CheckRefusal(error, outcome);
        }
        if (outcome.Failed)
        {
            return Verdict.Failure("unexpected error: " + outcome.Error);
        }
        if (expected is not { } checks)
        {
            return Verdict.Success;
        }
        if (checks.TryGetProperty("value", out var value))
        {
            var wanted = Text(value);
            var actual = FirstCell(outcome.Rows);
            if (Normalization.Norm(wanted) != Normalization.Norm(actual))
            {
                return Verdict.Failure($"value [{actual ?? "null"}] != expected [{wanted ?? "null"}]");
            }
        }
        if (checks.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            var ordered = checks.TryGetProperty("ordered", out var flag) && flag.ValueKind == JsonValueKind.True;
            var diff = GridDiff(ExpectedGrid(rows), outcome.Rows ?? Array.Empty<IReadOnlyList<string?>>(), ordered);
            if (diff is not null)
            {
                return Verdict.Failure(diff);
            }
        }
        if (checks.TryGetProperty("rowCount", out var rowCount) && rowCount.ValueKind == JsonValueKind.Number)
        {
            var got = outcome.Rows?.Count ?? 0;
            var wanted = Whole(rowCount);
            if (got != wanted)
            {
                return Verdict.Failure($"rowCount {got} != expected {wanted}");
            }
        }
        if (checks.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            var mismatch = ColumnMismatch(columns, outcome.Columns);
            if (mismatch is not null)
            {
                return Verdict.Failure(mismatch);
            }
        }
        if (checks.TryGetProperty("updateCount", out var updateCount) && updateCount.ValueKind == JsonValueKind.Number
            && outcome.UpdateCount != Whole(updateCount))
        {
            return Verdict.Failure($"updateCount {outcome.UpdateCount} != expected {Whole(updateCount)}");
        }
        return Verdict.Success;
    }

    /// <summary>The statement is expected to fail: check that it did, and with the named message.</summary>
    private static Verdict CheckRefusal(JsonElement error, StepOutcome outcome)
    {
        if (!outcome.Failed)
        {
            return Verdict.Failure("expected an error, statement succeeded");
        }
        if (error.TryGetProperty("messageContains", out var contains) && contains.ValueKind != JsonValueKind.Null)
        {
            var wanted = Text(contains) ?? "";
            if (!outcome.Error!.ToLowerInvariant().Contains(wanted.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return Verdict.Failure($"error message [{outcome.Error}] does not contain [{wanted}]");
            }
        }
        var wantsCode = error.TryGetProperty("code", out var code) && code.ValueKind != JsonValueKind.Null;
        var wantsState = error.TryGetProperty("sqlState", out var state) && state.ValueKind != JsonValueKind.Null;
        return wantsCode || wantsState ? Verdict.SuccessMissing(MissingErrorCode) : Verdict.Success;
    }

    /// <summary>An expected cell as text, the way the reference reads it: JSON null stays SQL NULL.</summary>
    private static string? Text(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText(),
        };
    }

    /// <summary>A count as the reference reads one: the number's integral part.</summary>
    private static long Whole(JsonElement number)
    {
        return number.TryGetInt64(out var whole) ? whole : (long)decimal.Truncate(number.GetDecimal());
    }

    private static string? FirstCell(IReadOnlyList<IReadOnlyList<string?>>? rows)
    {
        return rows is { Count: > 0 } && rows[0].Count > 0 ? rows[0][0] : null;
    }

    private static List<IReadOnlyList<string?>> ExpectedGrid(JsonElement rows)
    {
        var grid = new List<IReadOnlyList<string?>>();
        foreach (var row in rows.EnumerateArray())
        {
            var cells = new List<string?>();
            if (row.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in row.EnumerateArray())
                {
                    cells.Add(Text(cell));
                }
            }
            grid.Add(cells);
        }
        return grid;
    }

    private static string? ColumnMismatch(JsonElement expected, IReadOnlyList<string> actual)
    {
        var wanted = new List<string>();
        foreach (var column in expected.EnumerateArray())
        {
            wanted.Add(Text(column) ?? "null");
        }
        if (wanted.Count != actual.Count)
        {
            return $"column count {actual.Count} != expected {wanted.Count} [{string.Join(", ", actual)}]";
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!string.Equals(wanted[i], actual[i], StringComparison.OrdinalIgnoreCase))
            {
                return $"column[{i}] [{actual[i]}] != expected [{wanted[i]}]";
            }
        }
        return null;
    }

    private static string? GridDiff(IReadOnlyList<IReadOnlyList<string?>> want, IReadOnlyList<IReadOnlyList<string?>> got,
        bool ordered)
    {
        var expected = Canonical(want);
        var actual = Canonical(got);
        if (!ordered)
        {
            expected.Sort(StringComparer.Ordinal);
            actual.Sort(StringComparer.Ordinal);
        }
        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return null;
        }
        return $"rows differ: expected [{string.Join(", ", expected)}] got [{string.Join(", ", actual)}]";
    }

    /// <summary>Each row as one string of normalized cells, every cell closed by the unit separator.</summary>
    private static List<string> Canonical(IReadOnlyList<IReadOnlyList<string?>> grid)
    {
        var lines = new List<string>(grid.Count);
        foreach (var row in grid)
        {
            var line = new StringBuilder();
            foreach (var cell in row)
            {
                line.Append(Normalization.Norm(cell)).Append(CellSeparator);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }
}
