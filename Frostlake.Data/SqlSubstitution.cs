using System.Collections;
using System.Globalization;
using System.Text;

namespace Frostlake.Data;

/// <summary>
/// Client-side parameter binding. <c>?</c> placeholders are filled positionally and
/// <c>@name</c>/<c>:name</c> placeholders by name; placeholders inside string literals
/// (both <c>''</c> and <c>\'</c> escapes), quoted identifiers, <c>$$…$$</c> bodies and
/// <c>--</c>/<c>//</c>/<c>/* */</c> comments are left untouched.
/// </summary>
internal static class SqlSubstitution
{
    public static string Substitute(string sql, IReadOnlyList<object?> binds)
    {
        var named = new List<BindValue>(binds.Count);
        foreach (var value in binds)
        {
            named.Add(new BindValue(null, value));
        }
        return Substitute(sql, named);
    }

    public static string Substitute(string sql, IReadOnlyList<BindValue> binds)
    {
        var output = new StringBuilder(sql.Length + 16 * binds.Count);
        var consumed = new bool[binds.Count];
        var next = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                i = CopySpan(sql, output, i, SkipString(sql, i));
            }
            else if (ch == '"')
            {
                i = CopySpan(sql, output, i, SkipQuoted(sql, i));
            }
            else if (ch == '$' && i + 1 < sql.Length && sql[i + 1] == '$')
            {
                // Snowflake dollar-quoting: the whole body is opaque, ? and @name inside it are text.
                i = CopySpan(sql, output, i, SkipDollarQuoted(sql, i));
            }
            else if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i = CopySpan(sql, output, i, SkipLine(sql, i));
            }
            else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = CopySpan(sql, output, i, end < 0 ? sql.Length : end + 2);
            }
            else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                i = CopySpan(sql, output, i, SkipLine(sql, i));
            }
            else if (ch == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                // The cast operator, never a bind — even when a parameter is named like a type.
                output.Append("::");
                i++;
            }
            else if ((ch == '@' || ch == ':')
                     && !FollowsIdentifier(sql, i)
                     && TryReadName(sql, i + 1, out var name, out var after)
                     && TryFindNamed(binds, name, out var bound))
            {
                // Only substituted when a parameter of that name was supplied and the marker does
                // not continue an identifier, so stage references (@my_stage), casts (v::date) and
                // VARIANT paths (col:field) survive untouched.
                output.Append(FormatLiteral(bound));
                i = after - 1;
            }
            else if (ch == '?')
            {
                if (next >= binds.Count)
                {
                    throw new FrostlakeException(
                        $"not enough bind values for placeholders: {binds.Count} supplied");
                }
                consumed[next] = true;
                output.Append(FormatLiteral(binds[next++].Value));
            }
            else
            {
                output.Append(ch);
            }
        }
        var stranded = 0;
        for (var i = 0; i < binds.Count; i++)
        {
            // A named parameter may go unused - Dapper hands over every property of the parameter
            // object. A positional one that no ? consumed means the caller and the statement
            // disagree, and silently dropping it re-sends a stale value on the next execute.
            if (binds[i].Name is null && !consumed[i])
            {
                stranded++;
            }
        }
        if (stranded > 0)
        {
            throw new FrostlakeException(
                $"{stranded} positional bind value(s) left over: the statement has {next} '?' placeholders");
        }
        return output.ToString();
    }

    /// <summary>True when the marker continues an identifier, as in <c>v:field</c> or <c>v::date</c>.</summary>
    private static bool FollowsIdentifier(string sql, int index)
    {
        if (index == 0)
        {
            return false;
        }
        var previous = sql[index - 1];
        return char.IsLetterOrDigit(previous) || previous is '_' or '$' or '"' or ')' or ']';
    }

    private static int CopySpan(string sql, StringBuilder output, int start, int end)
    {
        var stop = Math.Min(end, sql.Length);
        output.Append(sql, start, stop - start);
        return stop - 1;
    }

    private static bool TryReadName(string s, int start, out string name, out int after)
    {
        var j = start;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$'))
        {
            j++;
        }
        name = s[start..j];
        after = j;
        return name.Length > 0 && !char.IsAsciiDigit(name[0]);
    }

    private static bool TryFindNamed(IReadOnlyList<BindValue> binds, string name, out object? value)
    {
        foreach (var bind in binds)
        {
            if (bind.Name is not null && string.Equals(bind.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = bind.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static int SkipString(string s, int i)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == '\\')
            {
                j += 2; // backslash always escapes
            }
            else if (s[j] == '\'')
            {
                if (j + 1 < s.Length && s[j + 1] == '\'')
                {
                    j += 2;
                }
                else
                {
                    return j + 1;
                }
            }
            else
            {
                j++;
            }
        }
        return Math.Min(j, s.Length); // unterminated literal: consume the rest
    }

    private static int SkipQuoted(string s, int i)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == '"')
            {
                if (j + 1 < s.Length && s[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            j++;
        }
        return j;
    }

    private static int SkipDollarQuoted(string s, int i)
    {
        var end = s.IndexOf("$$", i + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + 2;
    }

    private static int SkipLine(string s, int i)
    {
        var j = s.IndexOf('\n', i);
        return j < 0 ? s.Length : j + 1;
    }

    private static string EncodeString(string text)
    {
        return "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'";
    }

    public static string FormatLiteral(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return "NULL";
            case bool b:
                return b ? "TRUE" : "FALSE";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case float f:
                return float.IsFinite(f)
                    ? f.ToString("R", CultureInfo.InvariantCulture)
                    : throw new FrostlakeException($"non-finite number {f}");
            case double d:
                return double.IsFinite(d)
                    ? d.ToString("R", CultureInfo.InvariantCulture)
                    : throw new FrostlakeException($"non-finite number {d}");
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case string s:
                return EncodeString(s);
            case char c:
                return EncodeString(c.ToString());
            case Guid g:
                return EncodeString(g.ToString());
            case DateTime dt:
                return $"'{dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'::TIMESTAMP_NTZ";
            case DateTimeOffset dto:
                return $"'{dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)}'::TIMESTAMP_TZ";
            case DateOnly dateOnly:
                return $"'{dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'::DATE";
            case TimeOnly timeOnly:
                return $"'{timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'::TIME";
            case TimeSpan span:
                return $"'{span.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture)}'::TIME";
            case byte[] bytes:
                return "X'" + Convert.ToHexString(bytes) + "'";
            case IEnumerable enumerable:
                var parts = new List<string>();
                foreach (var element in enumerable)
                {
                    parts.Add(FormatLiteral(element));
                }
                return "[" + string.Join(", ", parts) + "]";
            default:
                throw new FrostlakeException($"unsupported bind type {value.GetType().Name}");
        }
    }
}
