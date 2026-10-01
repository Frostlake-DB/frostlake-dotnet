using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Frostlake.Data.Testkit;

/// <summary>
/// A cell the provider decoded, written back as the text the engine sent for it. The corpus records
/// the wire's text, so this is how its expectations compare against what the provider actually
/// handed a caller: a <c>long</c>, <c>decimal</c>, <c>double</c>, <c>bool</c>, <c>DateTime</c>,
/// <c>DateTimeOffset</c>, <c>TimeSpan</c> or <c>byte[]</c> read through <see cref="DbDataReader.GetValue"/>
/// is rendered in the engine's own spelling for its declared type, and a string is taken as it came.
/// </summary>
internal static class WireText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The cell at <paramref name="ordinal"/> of the reader's current row, or null for SQL NULL.</summary>
    public static string? Cell(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        var value = reader.GetValue(ordinal);
        var text = value switch
        {
            string plain => plain,
            bool flag => flag ? "true" : "false",
            long whole => whole.ToString(Invariant),
            decimal exact => exact.ToString(Invariant),
            double approximate => Float(approximate),
            byte[] bytes => Convert.ToHexString(bytes),
            DateTime clock => IsDate(reader.GetDataTypeName(ordinal))
                ? clock.ToString("yyyy-MM-dd", Invariant)
                : clock.ToString("yyyy-MM-dd HH:mm:ss", Invariant) + Fraction(clock.Ticks, 3),
            DateTimeOffset zoned => zoned.ToString("yyyy-MM-dd HH:mm:ss", Invariant)
                                    + Fraction(zoned.Ticks, 3) + " " + Offset(zoned.Offset),
            TimeSpan time => time.ToString(@"hh\:mm\:ss", Invariant) + Fraction(time.Ticks, 0),
            _ => Convert.ToString(value, Invariant),
        };
        return IsSemiStructured(reader.GetDataTypeName(ordinal)) ? SemiStructuredValue(text) : text;
    }

    private static string Float(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }
        return value.ToString("R", Invariant);
    }

    private static bool IsDate(string dataType)
    {
        return string.Equals(dataType, "DATE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fraction of a second as the engine writes it: three, six or nine digits, as many as the value
    /// needs; a timestamp never fewer than three, a TIME none at all when it has no fraction.
    /// </summary>
    private static string Fraction(long ticks, int minimumDigits)
    {
        var nanos = (int)(ticks % TimeSpan.TicksPerSecond) * 100;
        if (nanos == 0 && minimumDigits == 0)
        {
            return "";
        }
        var nine = nanos.ToString("D9", Invariant);
        if (nanos % 1_000_000 == 0)
        {
            return "." + nine[..3];
        }
        return "." + (nanos % 1_000 == 0 ? nine[..6] : nine);
    }

    /// <summary>A UTC offset as the engine writes it: <c>+HHMM</c>.</summary>
    private static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return sign + Math.Abs(offset.Hours).ToString("00", Invariant) + Math.Abs(offset.Minutes).ToString("00", Invariant);
    }

    /// <summary>Whether a column carries semi-structured values, read from the type the engine declared.</summary>
    private static bool IsSemiStructured(string dataType)
    {
        return dataType.ToUpperInvariant() is "VARIANT" or "OBJECT" or "ARRAY";
    }

    /// <summary>
    /// The value a semi-structured cell carries, as the corpus records it. A VARIANT, OBJECT or ARRAY
    /// cell reaches a caller as its JSON text — a string's own quotes included — which is what the
    /// account's own drivers do; the corpus records the value (<c>a</c>, not <c>"a"</c>). So the cell is
    /// decoded one level: a JSON string becomes its content, which for a whole object or array is the
    /// object's own text, and anything else — a number, a boolean, text that is not JSON at all — is left
    /// exactly as it came.
    /// </summary>
    private static string? SemiStructuredValue(string? cell)
    {
        if (cell is null)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(cell);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : cell;
        }
        catch (JsonException)
        {
            // Text that is not JSON at all is a value in its own right.
            return cell;
        }
    }
}
