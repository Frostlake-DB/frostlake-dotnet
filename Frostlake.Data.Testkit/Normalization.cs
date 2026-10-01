using System.Globalization;
using System.Numerics;
using System.Text;

namespace Frostlake.Data.Testkit;

/// <summary>
/// The corpus's value normalization, applied to both sides before they compare: NULL and the empty
/// string alike, booleans case-insensitive, anything numeric rounded to ten significant digits, and
/// everything else the trimmed text. It follows the reference runner's rules to the letter, so that
/// <c>2</c> and <c>2.000000</c>, or <c>1E+20</c> and <c>100000000000000000000</c>, compare equal.
/// </summary>
internal static class Normalization
{
    private const int SignificantDigits = 10;

    public static string Norm(string? raw)
    {
        if (raw is null)
        {
            return "NULL";
        }
        var value = TrimControl(raw);
        if (value.Length == 0 || value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return "NULL";
        }
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "TRUE";
        }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "FALSE";
        }
        return TryParseNumber(value, out var unscaled, out var scale) ? Plain(unscaled, scale) : value;
    }

    /// <summary>
    /// Trims what the reference trims: every character up to and including the space, at both ends.
    /// <see cref="string.Trim()"/> would also take Unicode spaces that the reference keeps.
    /// </summary>
    private static string TrimControl(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }
        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary>
    /// Reads a decimal numeral — an optional sign, digits with an optional point, an optional exponent —
    /// as an unscaled integer and a scale, so that nothing is lost to a binary float. NaN, Infinity and
    /// anything else that is not such a numeral stay text.
    /// </summary>
    private static bool TryParseNumber(string text, out BigInteger unscaled, out int scale)
    {
        unscaled = BigInteger.Zero;
        scale = 0;
        var i = 0;
        var negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }
        var digits = new StringBuilder();
        var fractionDigits = 0;
        var seenPoint = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                if (seenPoint)
                {
                    fractionDigits++;
                }
            }
            else if (c == '.' && !seenPoint)
            {
                seenPoint = true;
            }
            else
            {
                break;
            }
        }
        if (digits.Length == 0)
        {
            return false;
        }
        long exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            var exponentNegative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                exponentNegative = text[i] == '-';
                i++;
            }
            var exponentStart = i;
            for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            {
                exponent = exponent * 10 + (text[i] - '0');
                if (exponent > int.MaxValue)
                {
                    return false;
                }
            }
            if (i == exponentStart)
            {
                return false;
            }
            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }
        if (i != text.Length)
        {
            return false;
        }
        var magnitude = BigInteger.Parse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        unscaled = negative ? -magnitude : magnitude;
        var combined = fractionDigits - exponent;
        if (combined is > int.MaxValue or < int.MinValue)
        {
            return false;
        }
        scale = (int)combined;
        return true;
    }

    /// <summary>
    /// The value rounded to ten significant digits (half away from zero), trailing zeros stripped and
    /// written without an exponent. Zero is always <c>0</c>.
    /// </summary>
    private static string Plain(BigInteger unscaled, int scale)
    {
        if (unscaled.IsZero)
        {
            return "0";
        }
        var negative = unscaled.Sign < 0;
        var magnitude = BigInteger.Abs(unscaled);
        var length = magnitude.ToString(CultureInfo.InvariantCulture).Length;
        if (length > SignificantDigits)
        {
            var drop = length - SignificantDigits;
            var divisor = BigInteger.Pow(10, drop);
            var quotient = BigInteger.DivRem(magnitude, divisor, out var remainder);
            magnitude = remainder * 2 >= divisor ? quotient + 1 : quotient;
            scale -= drop;
        }
        while (!magnitude.IsZero && (magnitude % 10).IsZero)
        {
            magnitude /= 10;
            scale--;
        }
        var text = magnitude.ToString(CultureInfo.InvariantCulture);
        string plain;
        if (scale <= 0)
        {
            plain = text + new string('0', -scale);
        }
        else if (text.Length > scale)
        {
            plain = text[..^scale] + "." + text[^scale..];
        }
        else
        {
            plain = "0." + new string('0', scale - text.Length) + text;
        }
        return negative ? "-" + plain : plain;
    }
}
