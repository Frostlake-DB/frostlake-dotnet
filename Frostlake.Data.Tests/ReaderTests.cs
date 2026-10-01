using System.Data.Common;
using System.Data;
using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>Reader behaviour driven from wire-shaped JSON; no engine required.</summary>
public class ReaderTests
{
    [Fact]
    public void FieldTypeMatchesTheValueTypeForEveryRow()
    {
        // An INTEGER column arrives as NUMBER(38,0), so the type has to follow the data, not the precision.
        foreach (var (type, rows) in new[]
                 {
                     ("NUMBER", "[[1],[2],[3]]"),
                     ("NUMBER", "[[99999999999999999999999999999999999999]]"),
                     ("NUMBER", "[[1],[99999999999999999999999999999999999999]]"),
                     ("FLOAT", "[[1.5],[2.5]]"),
                     ("BOOLEAN", "[[true],[false]]"),
                     ("VARCHAR", "[[\"a\"],[\"b\"]]"),
                     ("DATE", "[[\"2026-01-02\"]]"),
                     ("BINARY", "[[\"CAFE\"]]"),
                 })
        {
            using var reader = TestWire.Column(type, rows);
            var declared = reader.GetFieldType(0);
            var seen = 0;
            do
            {
                Assert.Equal(declared, reader.GetValue(0).GetType());
                seen++;
            }
            while (reader.Read());
            Assert.True(seen > 0);
        }
    }

    [Fact]
    public void PlainIntegersStayLongAndOversizedOnesWiden()
    {
        using var small = TestWire.Column("NUMBER", "[[42]]");
        Assert.Equal(typeof(long), small.GetFieldType(0));
        Assert.Equal(42L, small.GetValue(0));

        using var huge = TestWire.Column("NUMBER", "[[99999999999999999999999999999999999999]]");
        Assert.Equal(typeof(string), huge.GetFieldType(0));
        Assert.Equal("99999999999999999999999999999999999999", huge.GetValue(0));

        using var wide = TestWire.Column("NUMBER", "[[123456789012345678901234]]");
        Assert.Equal(typeof(decimal), wide.GetFieldType(0));
        Assert.Equal(123456789012345678901234m, wide.GetValue(0));
    }

    [Fact]
    public void ScaledNumbersReadAsDecimal()
    {
        using var reader = TestWire.Column("NUMBER", "[[1.50]]", scale: 2, precision: 10);
        Assert.Equal(typeof(decimal), reader.GetFieldType(0));
        Assert.Equal(1.50m, reader.GetValue(0));
        Assert.Equal(1.5d, reader.GetDouble(0));
        Assert.Equal(2, reader.GetInt32(0));
    }

    [Fact]
    public void NoResultSetReportsEmptyRatherThanThrowing()
    {
        using var reader = TestWire.Reader();
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
        Assert.False(reader.NextResult());
        Assert.Equal(-1, reader.RecordsAffected);
    }

    [Fact]
    public void FieldCountIsZeroOnceTheResultSetsAreExhausted()
    {
        using var reader = TestWire.Reader("""{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1]]}""");
        Assert.Equal(1, reader.FieldCount);
        while (reader.Read()) { }
        Assert.False(reader.NextResult());
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.HasRows);
    }

    [Fact]
    public void NextResultWalksEveryStatement()
    {
        using var reader = TestWire.Reader(
            """{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1]]}""",
            """{"columns":[{"name":"B","dataType":"VARCHAR"}],"rows":[["x"],["y"]]}""");
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetValue(0));
        Assert.True(reader.NextResult());
        Assert.Equal("B", reader.GetName(0));
        Assert.Equal(2, CountRows(reader));
        Assert.False(reader.NextResult());
    }

    private static int CountRows(FrostlakeDataReader reader)
    {
        var rows = 0;
        while (reader.Read())
        {
            rows++;
        }
        return rows;
    }

    [Fact]
    public void SchemaTableDescribesTheColumns()
    {
        using var reader = TestWire.Reader(
            """
            {"columns":[{"name":"A","dataType":"NUMBER","precision":10,"scale":2,"nullable":false},
                        {"name":"B","dataType":"VARCHAR","nullable":true}],
             "rows":[[1.5,"x"]]}
            """);
        var schema = reader.GetSchemaTable();
        Assert.Equal(2, schema.Rows.Count);
        Assert.Equal("A", schema.Rows[0][SchemaTableColumn.ColumnName]);
        Assert.Equal(0, schema.Rows[0][SchemaTableColumn.ColumnOrdinal]);
        Assert.Equal(typeof(decimal), schema.Rows[0][SchemaTableColumn.DataType]);
        Assert.Equal((short)2, schema.Rows[0][SchemaTableColumn.NumericScale]);
        Assert.False((bool)schema.Rows[0][SchemaTableColumn.AllowDBNull]!);
        Assert.True((bool)schema.Rows[1][SchemaTableColumn.AllowDBNull]!);
    }

    [Fact]
    public void DataTableLoadWorks()
    {
        using var reader = TestWire.Reader(
            """
            {"columns":[{"name":"ID","dataType":"NUMBER"},{"name":"NAME","dataType":"VARCHAR"}],
             "rows":[[1,"Ada"],[2,"Grace"]]}
            """);
        var table = new DataTable();
        table.Load(reader);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("Ada", table.Rows[0]["NAME"]);
    }

    [Fact]
    public void ClosedReaderRefusesFurtherWork()
    {
        var reader = TestWire.Reader("""{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1]]}""");
        reader.Close();
        Assert.True(reader.IsClosed);
        Assert.Throws<FrostlakeException>(() => reader.Read());
        Assert.Throws<FrostlakeException>(() => reader.GetValue(0));
        Assert.Throws<FrostlakeException>(() => reader.NextResult());
        reader.Close(); // idempotent
    }

    [Fact]
    public void ReadingBeforeReadFails()
    {
        using var reader = TestWire.Reader("""{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1]]}""");
        Assert.Throws<FrostlakeException>(() => reader.GetValue(0));
    }

    [Fact]
    public void GetBytesHandlesOffsetsOutsideTheValue()
    {
        using var reader = TestWire.Column("BINARY", """[["CAFE"]]""");
        Assert.Equal(2, reader.GetBytes(0, 0, null, 0, 0));
        var buffer = new byte[8];
        Assert.Equal(2, reader.GetBytes(0, 0, buffer, 0, 8));
        Assert.Equal(0xCA, buffer[0]);
        Assert.Equal(0, reader.GetBytes(0, 99, buffer, 0, 4));
        Assert.Equal(0, reader.GetBytes(0, -1, buffer, 0, 4));
        Assert.Equal(1, reader.GetBytes(0, 1, buffer, 0, 4));
    }

    [Fact]
    public void GetCharsHandlesOffsetsOutsideTheValue()
    {
        using var reader = TestWire.Column("VARCHAR", """[["hello"]]""");
        Assert.Equal(5, reader.GetChars(0, 0, null, 0, 0));
        var buffer = new char[8];
        Assert.Equal(5, reader.GetChars(0, 0, buffer, 0, 8));
        Assert.Equal('h', buffer[0]);
        Assert.Equal(0, reader.GetChars(0, 99, buffer, 0, 4));
        Assert.Equal(2, reader.GetChars(0, 3, buffer, 0, 4));
    }

    [Fact]
    public void GetCharOnAnEmptyStringFailsClearly()
    {
        using var reader = TestWire.Column("VARCHAR", """[[""]]""");
        var error = Assert.Throws<FrostlakeException>(() => reader.GetChar(0));
        Assert.Contains("empty", error.Message);
    }

    [Fact]
    public void NullsSurfaceAsDBNull()
    {
        using var reader = TestWire.Column("VARCHAR", """[[null]]""");
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
    }

    [Fact]
    public void OrdinalLookupIsCaseInsensitiveAndReportsMisses()
    {
        using var reader = TestWire.Reader(
            """{"columns":[{"name":"NAME","dataType":"VARCHAR"}],"rows":[["Ada"]]}""");
        Assert.Equal(0, reader.GetOrdinal("NAME"));
        Assert.Equal(0, reader.GetOrdinal("name"));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("nope"));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetName(5));
        reader.Read();
        Assert.Equal("Ada", reader["NAME"]);
        Assert.Equal("Ada", reader[0]);
    }

    [Fact]
    public void TypedGettersConvert()
    {
        using var reader = TestWire.Column("NUMBER", "[[42]]");
        Assert.Equal((short)42, reader.GetInt16(0));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal((byte)42, reader.GetByte(0));
        Assert.Equal(42m, reader.GetDecimal(0));
        Assert.Equal(42d, reader.GetDouble(0));
        Assert.Equal(42f, reader.GetFloat(0));
        Assert.Equal("42", reader.GetString(0));
        Assert.Equal("NUMBER", reader.GetDataTypeName(0));
        Assert.Equal(0, reader.Depth);
        var values = new object[1];
        Assert.Equal(1, reader.GetValues(values));
        Assert.Equal(42L, values[0]);
    }

    [Fact]
    public void DateTimeAndTimeColumnsParse()
    {
        using var date = TestWire.Column("DATE", """[["2026-01-02"]]""");
        Assert.Equal(new DateTime(2026, 1, 2), date.GetDateTime(0));

        using var stamp = TestWire.Column("TIMESTAMP_NTZ", """[["2026-01-02 03:04:05.123456789"]]""");
        Assert.Equal(typeof(DateTime), stamp.GetFieldType(0));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567), stamp.GetDateTime(0));

        using var time = TestWire.Column("TIME", """[["03:04:05"]]""");
        Assert.Equal(typeof(TimeSpan), time.GetFieldType(0));
        Assert.Equal(new TimeSpan(3, 4, 5), time.GetValue(0));

        using var unparseable = TestWire.Column("DATE", """[["not a date"]]""");
        Assert.Equal(typeof(string), unparseable.GetFieldType(0));
        Assert.Equal("not a date", unparseable.GetValue(0));
    }

    [Fact]
    public void ZonedTimestampsReadAsDateTimeOffsetAtTheirOwnOffset()
    {
        using var tz = TestWire.Column("TIMESTAMP_TZ", """[["2026-01-02 03:04:05.123456789 +0200"]]""");
        Assert.Equal(typeof(DateTimeOffset), tz.GetFieldType(0));
        var value = Assert.IsType<DateTimeOffset>(tz.GetValue(0));
        // DateTimeOffset equality compares instants only, so the offset is checked on its own
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1234567), value);
        Assert.Equal(TimeSpan.FromHours(2), value.Offset);
        Assert.Equal(value, tz.GetFieldValue<DateTimeOffset>(0));
        Assert.Throws<FrostlakeException>(() => tz.GetDateTime(0));

        using var negative = TestWire.Column("TIMESTAMP_TZ", """[["2026-01-02 03:04:05.000 -0530"]]""");
        Assert.Equal(new TimeSpan(-5, -30, 0), negative.GetFieldValue<DateTimeOffset>(0).Offset);

        // an LTZ arrives at the session zone's offset; GetDateTime reads its wall clock there
        using var ltz = TestWire.Column("TIMESTAMP_LTZ", """[["2026-07-02 03:04:05.500 -0400"]]""");
        Assert.Equal(typeof(DateTimeOffset), ltz.GetFieldType(0));
        var local = ltz.GetFieldValue<DateTimeOffset>(0);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 3, 4, 5, 500, TimeSpan.FromHours(-4)), local);
        Assert.Equal(TimeSpan.FromHours(-4), local.Offset);
        var wallClock = ltz.GetDateTime(0);
        Assert.Equal(new DateTime(2026, 7, 2, 3, 4, 5, 500), wallClock);
        Assert.Equal(DateTimeKind.Local, wallClock.Kind);
    }

    [Fact]
    public void AZonedTimestampDotNetCannotHoldReadsAsText()
    {
        // DateTimeOffset stops at year 1 in UTC, so this instant has no .NET value
        using var early = TestWire.Column("TIMESTAMP_TZ", """[["0001-01-01 00:00:00.000 +0100"]]""");
        Assert.Equal(typeof(string), early.GetFieldType(0));
        Assert.Equal("0001-01-01 00:00:00.000 +0100", early.GetValue(0));
        Assert.Throws<FrostlakeException>(() => early.GetDateTime(0));
    }

    [Fact]
    public void BinaryColumnsDecodeHexAndFallBackToText()
    {
        using var binary = TestWire.Column("BINARY", """[["CAFE"]]""");
        Assert.Equal(typeof(byte[]), binary.GetFieldType(0));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, binary.GetValue(0));

        using var notHex = TestWire.Column("BINARY", """[["zz"]]""");
        Assert.Equal(typeof(string), notHex.GetFieldType(0));
        Assert.Equal("zz", notHex.GetValue(0));
    }

    [Fact]
    public void VariantColumnsArriveAsJsonText()
    {
        using var reader = TestWire.Column("VARIANT", """[[{"a":1}]]""");
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Contains("\"a\"", reader.GetString(0));
    }

    [Fact]
    public void RecordsAffectedSumsTheDmlCounts()
    {
        // UPDATE always carries the "multi-joined" second column; it is not a
        // "number of rows …" column and must stay out of the sum.
        using var reader = TestWire.Reader(
            """{"columns":[{"name":"number of rows inserted","dataType":"NUMBER"}],"rows":[[2]]}""",
            """
            {"columns":[{"name":"number of rows updated","dataType":"NUMBER"},
                        {"name":"number of multi-joined rows updated","dataType":"NUMBER"}],
             "rows":[[3,7]]}
            """);
        Assert.Equal(5, reader.RecordsAffected);
    }

    [Fact]
    public void MergeCountsSumAcrossTheActionColumns()
    {
        using var reader = TestWire.Reader(
            """
            {"columns":[{"name":"number of rows inserted","dataType":"NUMBER"},
                        {"name":"number of rows updated","dataType":"NUMBER"},
                        {"name":"number of rows deleted","dataType":"NUMBER"}],
             "rows":[[2,3,1]]}
            """);
        Assert.Equal(6, reader.RecordsAffected);
    }

    [Fact]
    public void OrdinaryResultsDoNotLookLikeDmlCounts()
    {
        // A one-row SELECT is not a DML count, a multi-row one even less so, and a
        // count-named column carrying text must be ignored rather than throw.
        using var select = TestWire.Reader(
            """{"columns":[{"name":"N","dataType":"NUMBER"}],"rows":[[5]]}""");
        Assert.Equal(-1, select.RecordsAffected);

        using var multiRow = TestWire.Reader(
            """{"columns":[{"name":"number of rows inserted","dataType":"NUMBER"}],"rows":[[1],[2]]}""");
        Assert.Equal(-1, multiRow.RecordsAffected);

        using var textual = TestWire.Reader(
            """{"columns":[{"name":"number of rows inserted","dataType":"VARCHAR"}],"rows":[["many"]]}""");
        Assert.Equal(-1, textual.RecordsAffected);
    }

    [Fact]
    public void ARowShorterThanItsColumnListFailsClearly()
    {
        using var reader = TestWire.Reader(
            """{"columns":[{"name":"A","dataType":"NUMBER"},{"name":"B","dataType":"VARCHAR"}],"rows":[[1]]}""");
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetValue(0));
        var error = Assert.Throws<FrostlakeException>(() => reader.GetValue(1));
        Assert.Contains("column(s)", error.Message);
    }

    [Fact]
    public void SchemaTableIsEmptyOnceTheResultSetsAreExhausted()
    {
        using var reader = TestWire.Reader("""{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1]]}""");
        while (reader.Read()) { }
        Assert.False(reader.NextResult());
        Assert.Empty(reader.GetSchemaTable().Rows);
    }

    [Fact]
    public void EnumeratorWalksRows()
    {
        using var reader = TestWire.Reader(
            """{"columns":[{"name":"A","dataType":"NUMBER"}],"rows":[[1],[2]]}""");
        var seen = 0;
        foreach (var _ in reader)
        {
            seen++;
        }
        Assert.Equal(2, seen);
    }

    /// <summary>
    /// A text or binary column reports its own length as ColumnSize; a number reports its precision;
    /// a type with neither reports -1. The wire sends the length for text and binary only.
    /// </summary>
    [Fact]
    public void ColumnSizeIsTheLengthForTextAndBinary()
    {
        using var reader = TestWire.Reader("""
            {"columns":[{"name":"S","dataType":"VARCHAR","precision":0,"scale":0,"length":9},
                        {"name":"B","dataType":"BINARY","precision":0,"scale":0,"length":5},
                        {"name":"BIG","dataType":"VARCHAR","precision":0,"scale":0,"length":16777216},
                        {"name":"N","dataType":"NUMBER","precision":10,"scale":2},
                        {"name":"F","dataType":"FLOAT","precision":0,"scale":0}],
             "rows":[["a","CAFE","b",1.5,2.5]]}
            """);
        var schema = reader.GetSchemaTable()!;
        Assert.Equal(9, schema.Rows[0][SchemaTableColumn.ColumnSize]);
        Assert.Equal(5, schema.Rows[1][SchemaTableColumn.ColumnSize]);
        Assert.Equal(16777216, schema.Rows[2][SchemaTableColumn.ColumnSize]);
        // A number is untouched: its precision still answers, and its scale with it.
        Assert.Equal(10, schema.Rows[3][SchemaTableColumn.ColumnSize]);
        Assert.Equal((short)2, schema.Rows[3][SchemaTableColumn.NumericScale]);
        // Neither a length nor a precision: -1, ADO.NET's "not applicable".
        Assert.Equal(-1, schema.Rows[4][SchemaTableColumn.ColumnSize]);
    }
}
