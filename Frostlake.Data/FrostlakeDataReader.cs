using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Frostlake.Data;

/// <summary>
/// A forward-only reader over the response's result sets (<see cref="NextResult"/> walks
/// multi-statement responses).
/// <para>
/// A column's CLR type is decided once from the values the result actually carries, so
/// <see cref="GetFieldType"/> always matches what <see cref="GetValue"/> returns for every
/// row of that column. Integral NUMBER reads as <c>long</c>, widening to <c>decimal</c> and
/// then to the exact text when a value in the column does not fit; scaled NUMBER →
/// <c>decimal</c>, FLOAT → <c>double</c>, BOOLEAN → <c>bool</c>, DATE/TIMESTAMP →
/// <c>DateTime</c>, TIME → <c>TimeSpan</c>, BINARY → <c>byte[]</c>, everything else
/// (including VARIANT/OBJECT/ARRAY as their JSON text) → <c>string</c>.
/// </para>
/// </summary>
public sealed class FrostlakeDataReader : DbDataReader
{
    private readonly List<ResultSetDto> _resultSets;
    private readonly FrostlakeConnection? _connection;
    private readonly bool _closeConnection;
    private int _setIndex;
    private int _rowIndex = -1;
    private bool _closed;
    private Type[]? _columnTypes;

    internal FrostlakeDataReader(List<ResultSetDto> resultSets)
        : this(resultSets, null, closeConnection: false)
    {
    }

    internal FrostlakeDataReader(
        List<ResultSetDto> resultSets,
        FrostlakeConnection? connection,
        bool closeConnection)
    {
        _resultSets = resultSets;
        _connection = connection;
        _closeConnection = closeConnection;
    }

    /// <summary>The set being read, or null once the reader has walked past the last one.</summary>
    private ResultSetDto? CurrentOrNull => _setIndex < _resultSets.Count ? _resultSets[_setIndex] : null;

    private ResultSetDto Current => CurrentOrNull ?? throw new FrostlakeException("no result set");

    public override int Depth => 0;

    public override int FieldCount => CurrentOrNull?.Columns.Count ?? 0;

    public override bool HasRows => CurrentOrNull is { Rows.Count: > 0 };

    public override bool IsClosed => _closed;

    /// <summary>The number of rows the whole response reported changed, or -1 when none did.</summary>
    public override int RecordsAffected
    {
        get
        {
            var total = -1;
            foreach (var set in _resultSets)
            {
                var count = FrostlakeCommand.DmlCountOf(set);
                if (count is not null)
                {
                    total = total < 0 ? count.Value : total + count.Value;
                }
            }
            return total;
        }
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        EnsureOpen();
        var set = CurrentOrNull;
        if (set is null || _rowIndex >= set.Rows.Count)
        {
            return false;
        }
        _rowIndex++;
        return _rowIndex < set.Rows.Count;
    }

    public override bool NextResult()
    {
        EnsureOpen();
        if (_setIndex >= _resultSets.Count)
        {
            return false;
        }
        _setIndex++;
        _rowIndex = -1;
        _columnTypes = null;
        return _setIndex < _resultSets.Count;
    }

    public override void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        if (_closeConnection)
        {
            _connection?.Close();
        }
    }

    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    public override string GetName(int ordinal)
    {
        return Column(ordinal).Name;
    }

    public override int GetOrdinal(string name)
    {
        var columns = Current.Columns;
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        throw new IndexOutOfRangeException($"no column named {name}");
    }

    public override string GetDataTypeName(int ordinal)
    {
        return Column(ordinal).DataType ?? "VARCHAR";
    }

    public override Type GetFieldType(int ordinal)
    {
        return ColumnType(ordinal);
    }

    /// <summary>Describes the current result set the way <c>DataTable.Load</c> and data adapters expect.</summary>
    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schema.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schema.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add(SchemaTableColumn.ProviderType, typeof(string));
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        var set = CurrentOrNull;
        if (set is null)
        {
            return schema;
        }
        for (var i = 0; i < set.Columns.Count; i++)
        {
            var column = set.Columns[i];
            var row = schema.NewRow();
            row[SchemaTableColumn.ColumnName] = column.Name;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.ColumnSize] = column.Precision is > 0 ? column.Precision.Value : -1;
            row[SchemaTableColumn.NumericPrecision] = (short)(column.Precision ?? 0);
            row[SchemaTableColumn.NumericScale] = (short)(column.Scale ?? 0);
            row[SchemaTableColumn.DataType] = ColumnType(i);
            row[SchemaTableColumn.ProviderType] = column.DataType ?? "VARCHAR";
            row[SchemaTableColumn.IsLong] = false;
            row[SchemaTableColumn.AllowDBNull] = column.Nullable;
            row[SchemaTableColumn.IsUnique] = false;
            row[SchemaTableColumn.IsKey] = false;
            row[SchemaTableColumn.IsAliased] = false;
            row[SchemaTableColumn.IsExpression] = false;
            schema.Rows.Add(row);
        }
        return schema;
    }

    public override bool IsDBNull(int ordinal)
    {
        return IsNull(Cell(ordinal));
    }

    public override object GetValue(int ordinal)
    {
        var cell = Cell(ordinal);
        if (IsNull(cell))
        {
            return DBNull.Value;
        }
        var type = ColumnType(ordinal);
        if (type == typeof(long))
        {
            return cell.GetInt64();
        }
        if (type == typeof(decimal))
        {
            return cell.ValueKind == JsonValueKind.Number
                ? cell.GetDecimal()
                : decimal.Parse(AsText(cell), CultureInfo.InvariantCulture);
        }
        if (type == typeof(double))
        {
            return cell.ValueKind == JsonValueKind.Number
                ? cell.GetDouble()
                : double.Parse(AsText(cell), CultureInfo.InvariantCulture);
        }
        if (type == typeof(bool))
        {
            return cell.GetBoolean();
        }
        if (type == typeof(DateTime))
        {
            ParseDateTime(AsText(cell), out var dateTime);
            return dateTime;
        }
        if (type == typeof(TimeSpan))
        {
            TimeSpan.TryParse(TrimFraction(AsText(cell), 7), CultureInfo.InvariantCulture, out var span);
            return span;
        }
        if (type == typeof(byte[]))
        {
            return Convert.FromHexString(AsText(cell));
        }
        return AsText(cell);
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }

    public override bool GetBoolean(int ordinal)
    {
        return Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override byte GetByte(int ordinal)
    {
        return Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = GetValue(ordinal) as byte[] ?? throw new FrostlakeException("column is not BINARY");
        if (buffer is null)
        {
            return bytes.Length;
        }
        var count = ChunkLength(bytes.Length, dataOffset, length, buffer.Length, bufferOffset);
        if (count > 0)
        {
            Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, count);
        }
        return count;
    }

    public override char GetChar(int ordinal)
    {
        var text = GetString(ordinal);
        return text.Length > 0
            ? text[0]
            : throw new FrostlakeException($"column {GetName(ordinal)} is empty; no character to read");
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }
        var count = ChunkLength(text.Length, dataOffset, length, buffer.Length, bufferOffset);
        if (count > 0)
        {
            text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        }
        return count;
    }

    /// <summary>
    /// How much <see cref="GetBytes"/>/<see cref="GetChars"/> may copy. An offset past the end
    /// of the value copies nothing rather than throwing, as the ADO.NET contract expects.
    /// </summary>
    private static int ChunkLength(int available, long dataOffset, int length, int bufferLength, int bufferOffset)
    {
        if (dataOffset < 0 || dataOffset >= available || length <= 0)
        {
            return 0;
        }
        if (bufferOffset < 0 || bufferOffset > bufferLength)
        {
            throw new FrostlakeException($"buffer offset {bufferOffset} is outside the buffer");
        }
        var count = Math.Min(length, available - (int)dataOffset);
        return Math.Max(0, Math.Min(count, bufferLength - bufferOffset));
    }

    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            string text when ParseDateTime(text, out var parsed) => parsed,
            _ => throw new FrostlakeException($"cannot read {value.GetType().Name} as DateTime"),
        };
    }

    public override decimal GetDecimal(int ordinal)
    {
        return Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override double GetDouble(int ordinal)
    {
        return Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override float GetFloat(int ordinal)
    {
        return Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override Guid GetGuid(int ordinal)
    {
        return GetValue(ordinal) switch
        {
            Guid guid => guid,
            byte[] bytes => new Guid(bytes),
            var other => Guid.Parse(Convert.ToString(other, CultureInfo.InvariantCulture)!),
        };
    }

    public override short GetInt16(int ordinal)
    {
        return Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override int GetInt32(int ordinal)
    {
        return Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override long GetInt64(int ordinal)
    {
        return Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public override string GetString(int ordinal)
    {
        var value = GetValue(ordinal);
        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new FrostlakeException("the reader is closed");
        }
    }

    private ColumnDto Column(int ordinal)
    {
        EnsureOpen();
        var columns = Current.Columns;
        if (ordinal < 0 || ordinal >= columns.Count)
        {
            throw new IndexOutOfRangeException($"no column at ordinal {ordinal}");
        }
        return columns[ordinal];
    }

    private JsonElement Cell(int ordinal)
    {
        EnsureOpen();
        var set = Current;
        if (ordinal < 0 || ordinal >= set.Columns.Count)
        {
            throw new IndexOutOfRangeException($"no column at ordinal {ordinal}");
        }
        if (_rowIndex < 0 || _rowIndex >= set.Rows.Count)
        {
            throw new FrostlakeException("no current row; call Read() first");
        }
        var row = set.Rows[_rowIndex];
        if (ordinal >= row.Count)
        {
            throw new FrostlakeException(
                $"row {_rowIndex} carries {row.Count} value(s) but the result declares {set.Columns.Count} column(s)");
        }
        return row[ordinal];
    }

    /// <summary>
    /// The CLR type every value in the column reads as. Decided once per result set from the
    /// values present, so the answer holds for all rows.
    /// </summary>
    private Type ColumnType(int ordinal)
    {
        var column = Column(ordinal);
        var set = Current;
        _columnTypes ??= new Type[set.Columns.Count];
        var cached = _columnTypes[ordinal];
        if (cached is not null)
        {
            return cached;
        }
        var resolved = ResolveColumnType(set, column, ordinal);
        _columnTypes[ordinal] = resolved;
        return resolved;
    }

    private static Type ResolveColumnType(ResultSetDto set, ColumnDto column, int ordinal)
    {
        switch (TypeFamily(column))
        {
            case Family.Integral:
            {
                var allLong = true;
                var allDecimal = true;
                foreach (var cell in Cells(set, ordinal))
                {
                    if (cell.ValueKind != JsonValueKind.Number)
                    {
                        return typeof(string);
                    }
                    allLong &= cell.TryGetInt64(out _);
                    allDecimal &= cell.TryGetDecimal(out _);
                }
                return allLong ? typeof(long) : allDecimal ? typeof(decimal) : typeof(string);
            }
            case Family.Decimal:
            {
                var allDecimal = true;
                foreach (var cell in Cells(set, ordinal))
                {
                    if (cell.ValueKind != JsonValueKind.Number)
                    {
                        return typeof(string);
                    }
                    allDecimal &= cell.TryGetDecimal(out _);
                }
                return allDecimal ? typeof(decimal) : typeof(double);
            }
            case Family.Double:
                foreach (var cell in Cells(set, ordinal))
                {
                    if (cell.ValueKind != JsonValueKind.Number)
                    {
                        return typeof(string);
                    }
                }
                return typeof(double);
            case Family.Boolean:
                foreach (var cell in Cells(set, ordinal))
                {
                    if (cell.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return typeof(string);
                    }
                }
                return typeof(bool);
            case Family.Date:
            case Family.Timestamp:
                foreach (var cell in Cells(set, ordinal))
                {
                    if (!ParseDateTime(AsText(cell), out _))
                    {
                        return typeof(string);
                    }
                }
                return typeof(DateTime);
            case Family.Time:
                foreach (var cell in Cells(set, ordinal))
                {
                    if (!TimeSpan.TryParse(TrimFraction(AsText(cell), 7), CultureInfo.InvariantCulture, out _))
                    {
                        return typeof(string);
                    }
                }
                return typeof(TimeSpan);
            case Family.Binary:
                foreach (var cell in Cells(set, ordinal))
                {
                    if (!IsHex(AsText(cell)))
                    {
                        return typeof(string);
                    }
                }
                return typeof(byte[]);
            default:
                return typeof(string);
        }
    }

    /// <summary>Every non-null cell of one column.</summary>
    private static IEnumerable<JsonElement> Cells(ResultSetDto set, int ordinal)
    {
        foreach (var row in set.Rows)
        {
            if (ordinal >= row.Count)
            {
                continue;
            }
            var cell = row[ordinal];
            if (!IsNull(cell))
            {
                yield return cell;
            }
        }
    }

    private static bool IsNull(JsonElement cell)
    {
        return cell.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }

    private static bool IsHex(string text)
    {
        if (text.Length % 2 != 0)
        {
            return false;
        }
        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static string AsText(JsonElement cell)
    {
        return cell.ValueKind == JsonValueKind.String ? cell.GetString()! : cell.GetRawText();
    }

    private static bool ParseDateTime(string text, out DateTime result)
    {
        return DateTime.TryParse(
            TrimFraction(text, 7),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
    }

    /// <summary>The engine emits up to nanosecond fractions; .NET parses at most 7 digits.</summary>
    private static string TrimFraction(string text, int maxDigits)
    {
        var dot = text.IndexOf('.');
        if (dot < 0)
        {
            return text;
        }
        var end = dot + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }
        var keep = Math.Min(end - dot - 1, maxDigits);
        return text[..(dot + 1 + keep)] + text[end..];
    }

    private static Family TypeFamily(ColumnDto column)
    {
        var type = (column.DataType ?? "").ToUpperInvariant();
        switch (type)
        {
            case "NUMBER":
            case "DECIMAL":
            case "NUMERIC":
            case "FIXED":
                return (column.Scale ?? 0) == 0 ? Family.Integral : Family.Decimal;
            case "INT":
            case "INTEGER":
            case "BIGINT":
            case "SMALLINT":
            case "TINYINT":
            case "BYTEINT":
                return Family.Integral;
            case "FLOAT":
            case "FLOAT4":
            case "FLOAT8":
            case "DOUBLE":
            case "DOUBLE PRECISION":
            case "REAL":
                return Family.Double;
            case "BOOLEAN":
                return Family.Boolean;
            case "DATE":
                return Family.Date;
            case "TIME":
                return Family.Time;
            case "DATETIME":
            case "TIMESTAMP":
            case "TIMESTAMP_NTZ":
            case "TIMESTAMP_LTZ":
            case "TIMESTAMP_TZ":
                return Family.Timestamp;
            case "BINARY":
            case "VARBINARY":
                return Family.Binary;
            default:
                return Family.Text;
        }
    }

    private enum Family
    {
        Integral,
        Decimal,
        Double,
        Boolean,
        Date,
        Time,
        Timestamp,
        Binary,
        Text,
    }
}
