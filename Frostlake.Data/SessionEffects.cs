namespace Frostlake.Data;

/// <summary>
/// Reads a request's SQL for what it leaves on the engine session: a moved context or session
/// setting, and a transaction opened or ended. The connection keeps count, so that when the engine
/// loses the session it knows whether a fresh one on the connection string's scope can stand in
/// for it. Literals, quoted identifiers, <c>$$…$$</c> bodies and comments are skipped the way the
/// engine skips them.
/// </summary>
internal static class SessionEffects
{
    /// <summary>The words that may sit between CREATE/DROP/ALTER and the kind of object being named.</summary>
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "OR", "REPLACE", "TRANSIENT", "TEMPORARY", "TEMP", "VOLATILE", "LOCAL", "GLOBAL", "SECURE", "IF",
        "NOT", "EXISTS", "PUBLIC", "PRIVATE", "ICEBERG", "DYNAMIC", "HYBRID", "EVENT", "RECURSIVE",
        "MATERIALIZED", "EXTERNAL",
    };

    private static readonly HashSet<string> Temporary = new(StringComparer.Ordinal) { "TEMPORARY", "TEMP", "VOLATILE" };

    /// <summary>
    /// The request split on its top-level semicolons; one inside a literal, a quoted identifier, a
    /// <c>$$</c> body or a comment does not split. Blank pieces are dropped. A scripting block is
    /// split along with everything else, which only makes the checks below more willing to flag a
    /// request: the safe direction to be wrong in.
    /// </summary>
    public static List<string> Statements(string sql)
    {
        var pieces = new List<string>();
        var start = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var past = SkipNonCode(sql, i);
            if (past >= 0)
            {
                i = past;
            }
            else if (sql[i] == ';')
            {
                AddPiece(pieces, sql[start..i]);
                start = i + 1;
                i++;
            }
            else
            {
                i++;
            }
        }
        AddPiece(pieces, sql[Math.Min(start, sql.Length)..]);
        return pieces;
    }

    /// <summary>
    /// Whether a statement leaves behind state a fresh session would not have: a moved scope (USE,
    /// CREATE or DROP of a DATABASE or SCHEMA), a session variable or setting (SET, UNSET, ALTER
    /// SESSION), or a temporary object. CREATE TABLE and its kind leave the session as it was.
    /// </summary>
    public static bool TouchesSession(string statement)
    {
        var words = LeadingWords(statement, 16);
        if (words.Count == 0)
        {
            return false;
        }
        var verb = words[0];
        if (verb is "USE" or "SET" or "UNSET")
        {
            return true;
        }
        var modifiers = 0;
        while (1 + modifiers < words.Count && Modifiers.Contains(words[1 + modifiers]))
        {
            modifiers++;
        }
        var kind = 1 + modifiers < words.Count ? words[1 + modifiers] : null;
        if (verb == "ALTER")
        {
            return kind == "SESSION";
        }
        if (verb is not ("CREATE" or "DROP"))
        {
            return false;
        }
        if (kind is "DATABASE" or "SCHEMA")
        {
            return true;
        }
        if (verb == "CREATE")
        {
            for (var j = 1; j <= modifiers; j++)
            {
                if (Temporary.Contains(words[j]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a statement opens or ends a transaction. <c>BEGIN</c> on its own (or with
    /// TRANSACTION, WORK or NAME) opens one; <c>BEGIN</c> followed by a statement opens a scripting
    /// block instead.
    /// </summary>
    public static TransactionEffect TransactionEffectOf(string statement)
    {
        var words = LeadingWords(statement, 2);
        if (words.Count == 0)
        {
            return TransactionEffect.None;
        }
        if (words[0] == "BEGIN" && (words.Count == 1 || words[1] is "TRANSACTION" or "WORK" or "NAME"))
        {
            return TransactionEffect.Begins;
        }
        if (words[0] == "START" && words.Count == 2 && words[1] == "TRANSACTION")
        {
            return TransactionEffect.Begins;
        }
        return words[0] is "COMMIT" or "ROLLBACK" ? TransactionEffect.Ends : TransactionEffect.None;
    }

    /// <summary>
    /// Up to <paramref name="limit"/> leading words of a statement, upper-cased, skipping whitespace
    /// and comments and stopping at the first thing that is not a word.
    /// </summary>
    internal static List<string> LeadingWords(string statement, int limit)
    {
        var words = new List<string>();
        var i = 0;
        while (words.Count < limit && i < statement.Length)
        {
            var c = statement[i];
            var next = i + 1 < statement.Length ? statement[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if ((c == '-' && next == '-') || (c == '/' && next == '/'))
            {
                i = SkipLine(statement, i);
            }
            else if (c == '/' && next == '*')
            {
                i = SkipBlockComment(statement, i);
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < statement.Length && IsWordChar(statement[i]))
                {
                    i++;
                }
                words.Add(statement[start..i].ToUpperInvariant());
            }
            else
            {
                break;
            }
        }
        return words;
    }

    private static void AddPiece(List<string> pieces, string piece)
    {
        if (!string.IsNullOrWhiteSpace(piece))
        {
            pieces.Add(piece);
        }
    }

    /// <summary>A character of an unquoted identifier. <c>$</c> is one, so <c>A$$B</c> is a name, not a <c>$$</c> body.</summary>
    private static bool IsWordChar(char c)
    {
        return c == '_' || c == '$' || char.IsLetterOrDigit(c);
    }

    /// <summary>
    /// Index just past the literal, quoted identifier, <c>$$</c> body or comment starting at
    /// <paramref name="i"/>, or -1 when <paramref name="i"/> is code.
    /// </summary>
    private static int SkipNonCode(string sql, int i)
    {
        var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
        switch (sql[i])
        {
            case '\'':
                return SkipString(sql, i);
            case '"':
                return SkipQuoted(sql, i);
            case '$' when next == '$' && (i == 0 || !IsWordChar(sql[i - 1])):
                var close = sql.IndexOf("$$", i + 2, StringComparison.Ordinal);
                return close < 0 ? sql.Length : close + 2;
            case '-' when next == '-':
            case '/' when next == '/':
                return SkipLine(sql, i);
            case '/' when next == '*':
                return SkipBlockComment(sql, i);
            default:
                return -1;
        }
    }

    /// <summary>A doubled quote and a backslash escape both stay inside the literal: backslash always escapes.</summary>
    private static int SkipString(string sql, int start)
    {
        var j = start + 1;
        while (j < sql.Length)
        {
            if (sql[j] == '\\')
            {
                j += 2;
            }
            else if (sql[j] == '\'')
            {
                if (j + 1 < sql.Length && sql[j + 1] == '\'')
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
        return sql.Length;
    }

    private static int SkipQuoted(string sql, int start)
    {
        var j = start + 1;
        while (j < sql.Length)
        {
            if (sql[j] == '"')
            {
                if (j + 1 < sql.Length && sql[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            j++;
        }
        return sql.Length;
    }

    private static int SkipLine(string sql, int start)
    {
        var j = sql.IndexOf('\n', start);
        return j < 0 ? sql.Length : j + 1;
    }

    /// <summary>An unterminated comment swallows the rest of the input, as it does on the server.</summary>
    private static int SkipBlockComment(string sql, int start)
    {
        var j = sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return j < 0 ? sql.Length : j + 2;
    }
}
