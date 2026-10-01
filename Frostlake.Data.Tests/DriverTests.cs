using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>
/// End-to-end tests against a real engine. Each is a <see cref="RequiresServerFactAttribute"/>,
/// so without <c>FROSTLAKE_CLASSPATH</c> they report as skipped rather than quietly passing.
/// </summary>
public class DriverTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public DriverTests(ServerFixture fixture)
    {
        _fixture = fixture;
    }

    private string ConnectionString =>
        _fixture.ConnectionString ?? throw new InvalidOperationException("the fixture has no server");

    private FrostlakeConnection Open(string database)
    {
        var connection = new FrostlakeConnection(ConnectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE OR REPLACE DATABASE {database}";
            command.ExecuteNonQuery();
        }
        connection.ChangeDatabase(database);
        return connection;
    }

    private static int Run(FrostlakeConnection connection, string sql, params object?[] binds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var bind in binds)
        {
            command.Parameters.AddWithValue("", bind);
        }
        return command.ExecuteNonQuery();
    }

    [RequiresServerFact]
    public void DdlDmlAndTypedQuery()
    {
        using var connection = Open("net_test_db");
        Run(connection, "CREATE TABLE people (id INTEGER, name VARCHAR, score FLOAT, ok BOOLEAN)");
        var inserted = Run(connection,
            "INSERT INTO people VALUES (?, ?, ?, ?), (?, ?, ?, ?)",
            1, "Ada O'Hara \\ Byron", 9.5, true, 2, "Grace", 8.25, false);
        Assert.Equal(2, inserted);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, score, ok FROM people WHERE id = ?";
        command.Parameters.AddWithValue("", 1);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("Ada O'Hara \\ Byron", reader.GetString(1));
        Assert.Equal(9.5, reader.GetDouble(2));
        Assert.True(reader.GetBoolean(3));
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.False(reader.Read());
    }

    [RequiresServerFact]
    public void ExecuteScalarAndNulls()
    {
        using var connection = Open("net_scalar_db");
        Run(connection, "CREATE TABLE t (a INTEGER, b VARCHAR)");
        Run(connection, "INSERT INTO t VALUES (1, NULL), (2, 'x')");

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(2L, count.ExecuteScalar());

        using var empty = connection.CreateCommand();
        empty.CommandText = "SELECT b FROM t WHERE 1 = 0";
        Assert.Null(empty.ExecuteScalar());

        // no rows is null; a NULL cell is DBNull — the ADO.NET distinction callers rely on
        using var nullCell = connection.CreateCommand();
        nullCell.CommandText = "SELECT b FROM t WHERE a = 1";
        Assert.Equal(DBNull.Value, nullCell.ExecuteScalar());

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT b FROM t ORDER BY a";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.True(reader.Read());
        Assert.Equal("x", reader.GetString(0));
    }

    [RequiresServerFact]
    public void TransactionRollback()
    {
        using var connection = Open("net_tx_db");
        Run(connection, "CREATE TABLE acc (n INTEGER)");
        Run(connection, "INSERT INTO acc VALUES (1)");
        using (var transaction = connection.BeginTransaction())
        {
            Run(connection, "INSERT INTO acc VALUES (2)");
            transaction.Rollback();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM acc";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [RequiresServerFact]
    public void TransactionCommitKeepsTheRows()
    {
        using var connection = Open("net_commit_db");
        Run(connection, "CREATE TABLE acc (n INTEGER)");
        using (var transaction = connection.BeginTransaction())
        {
            Run(connection, "INSERT INTO acc VALUES (1)");
            Run(connection, "INSERT INTO acc VALUES (2)");
            transaction.Commit();
            Assert.Throws<FrostlakeException>(() => transaction.Commit());
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM acc";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [RequiresServerFact]
    public void DisposingAnUnfinishedTransactionRollsBack()
    {
        using var connection = Open("net_txdispose_db");
        Run(connection, "CREATE TABLE acc (n INTEGER)");
        using (connection.BeginTransaction())
        {
            Run(connection, "INSERT INTO acc VALUES (1)");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM acc";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [RequiresServerFact]
    public void TransactionsAreGuarded()
    {
        using var connection = Open("net_txguard_db");
        using var other = new FrostlakeConnection(ConnectionString);
        other.Open();
        using var transaction = connection.BeginTransaction();

        Assert.Throws<FrostlakeException>(() => connection.BeginTransaction());
        Assert.Throws<FrostlakeException>(
            () => connection.BeginTransaction(IsolationLevel.Serializable));

        using var foreignCommand = other.CreateCommand();
        foreignCommand.Transaction = transaction;
        foreignCommand.CommandText = "SELECT 1";
        Assert.Throws<FrostlakeException>(() => foreignCommand.ExecuteScalar());
        transaction.Rollback();
    }

    [RequiresServerFact]
    public void ErrorSurfaceCarriesTheEngineMessage()
    {
        using var connection = Open("net_err_db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FROM nowhere";
        var error = Assert.Throws<FrostlakeException>(() => command.ExecuteReader());
        Assert.Contains("SQL compilation error", error.Message);
    }

    [RequiresServerFact]
    public void AnEmptyStatementIsRefusedByTheEngine()
    {
        using var connection = Open("net_empty_db");
        var engineWords = EngineAtLeast(connection, 0, 1);
        // The empty text reaches the engine like a blank one, rather than being turned away here
        // with words of the provider's own.
        foreach (var text in new[] { "", "   ", null })
        {
            using var command = connection.CreateCommand();
            command.CommandText = text;
            var error = Assert.Throws<FrostlakeException>(() => command.ExecuteNonQuery());
            if (engineWords)
            {
                Assert.Contains("Empty SQL statement.", error.Message);
            }
        }
    }

    [RequiresServerFact]
    public void DateTimeRoundTrip()
    {
        using var connection = Open("net_ts_db");
        Run(connection, "CREATE TABLE stamps (id INTEGER, moment TIMESTAMP_NTZ)");
        var moment = new DateTime(2026, 8, 13, 12, 34, 56, 789);
        Run(connection, "INSERT INTO stamps VALUES (?, ?)", 1, moment);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT moment FROM stamps WHERE id = 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(moment, reader.GetDateTime(0));
    }

    /// <summary>Whether the engine's <c>CURRENT_VERSION()</c> is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
    private static bool EngineAtLeast(FrostlakeConnection connection, int major, int minor)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CURRENT_VERSION()";
        var parts = ((string)command.ExecuteScalar()!).Split('.', '-');
        var version = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        return version.CompareTo((major, minor)) >= 0;
    }

    [RequiresServerFact]
    public void ZonedTimestampsKeepTheirOffset()
    {
        using var connection = Open("net_tz_db");
        var keepsOffsets = EngineAtLeast(connection, 0, 1);
        Run(connection, "CREATE TABLE zoned (id INTEGER, tz TIMESTAMP_TZ)");
        var moment = new DateTimeOffset(2026, 8, 13, 12, 34, 56, 789, TimeSpan.FromHours(2));
        Run(connection, "INSERT INTO zoned VALUES (?, ?)", 1, moment);
        Run(connection, "ALTER SESSION SET TIMEZONE = 'Asia/Kolkata'");
        try
        {
            using var command = connection.CreateCommand();
            // The LTZ is cast from the stored TZ rather than stored itself: a stored LTZ column
            // reads back at the offset it was written with, which is the engine's to fix.
            command.CommandText = "SELECT tz, tz::TIMESTAMP_LTZ FROM zoned WHERE id = 1";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(typeof(DateTimeOffset), reader.GetFieldType(0));
            Assert.Equal(typeof(DateTimeOffset), reader.GetFieldType(1));
            Assert.Throws<FrostlakeException>(() => reader.GetDateTime(0));
            if (!keepsOffsets)
            {
                // engines before 0.1.0 relabel every zoned value in the server's own zone
                return;
            }
            // the TZ keeps the offset it was written with ...
            var tz = reader.GetFieldValue<DateTimeOffset>(0);
            Assert.Equal(moment, tz);
            Assert.Equal(TimeSpan.FromHours(2), tz.Offset);
            // ... and the LTZ is the same instant in the session's zone, whatever this machine's is
            var ltz = reader.GetFieldValue<DateTimeOffset>(1);
            Assert.Equal(moment, ltz);
            Assert.Equal(new TimeSpan(5, 30, 0), ltz.Offset);
            Assert.Equal(new DateTime(2026, 8, 13, 16, 4, 56, 789), reader.GetDateTime(1));
        }
        finally
        {
            // the engine currently shares session parameters across sessions, so put it back
            Run(connection, "ALTER SESSION UNSET TIMEZONE");
        }
    }

    [RequiresServerFact]
    public void DateTimeBinaryAndDecimalRoundTrip()
    {
        using var connection = Open("net_types_db");
        Run(connection, "CREATE TABLE t (d DATE, tm TIME, b BINARY, n NUMBER(12,3), v VARIANT)");
        Run(connection,
            "INSERT INTO t SELECT ?, ?, ?, ?, PARSE_JSON('{\"k\":1}')",
            new DateOnly(2026, 1, 2),
            new TimeSpan(3, 4, 5),
            new byte[] { 0xCA, 0xFE },
            12.345m);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT d, tm, b, n, v FROM t";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new DateTime(2026, 1, 2), reader.GetDateTime(0));
        Assert.Equal(new TimeSpan(3, 4, 5), reader.GetValue(1));
        Assert.Equal(new byte[] { 0xCA, 0xFE }, reader.GetValue(2));
        Assert.Equal(12.345m, reader.GetDecimal(3));
        Assert.Contains("\"k\"", reader.GetString(4));
        for (var i = 0; i < reader.FieldCount; i++)
        {
            Assert.Equal(reader.GetFieldType(i), reader.GetValue(i).GetType());
        }
    }

    [RequiresServerFact]
    public void DapperMapsRowsToObjects()
    {
        using var connection = Open("net_dapper_db");
        Run(connection, "CREATE TABLE crew (id INTEGER, name VARCHAR)");
        Run(connection, "INSERT INTO crew VALUES (1, 'Ada'), (2, 'Grace')");
        var crew = connection.Query<CrewMember>("SELECT id, name FROM crew ORDER BY id").ToList();
        Assert.Equal(2, crew.Count);
        Assert.Equal(1, crew[0].Id);
        Assert.Equal("Ada", crew[0].Name);
        Assert.Equal("Grace", crew[1].Name);
    }

    [RequiresServerFact]
    public void DapperBindsNamedParameters()
    {
        using var connection = Open("net_dappernamed_db");
        Run(connection, "CREATE TABLE crew (id INTEGER, name VARCHAR)");
        Run(connection, "INSERT INTO crew VALUES (1, 'Ada'), (2, 'Grace')");
        var one = connection.Query<CrewMember>(
            "SELECT id, name FROM crew WHERE id = @id", new { id = 2 }).ToList();
        Assert.Single(one);
        Assert.Equal("Grace", one[0].Name);

        var inserted = connection.Execute(
            "INSERT INTO crew VALUES (@id, @name)", new { id = 3, name = "Hopper" });
        Assert.Equal(1, inserted);
        Assert.Equal(3L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM crew"));
    }

    [RequiresServerFact]
    public void SurplusParametersAreRejectedRatherThanReused()
    {
        using var connection = Open("net_stale_db");
        Run(connection, "CREATE TABLE t (n INTEGER)");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (?)";
        command.Parameters.AddWithValue("", 1);
        command.ExecuteNonQuery();

        command.Parameters.AddWithValue("", 2); // caller forgot Clear(): must not silently reuse 1
        Assert.Throws<FrostlakeException>(() => command.ExecuteNonQuery());

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    [RequiresServerFact]
    public void DdlThroughExecuteReaderYieldsTheStatusResult()
    {
        using var connection = Open("net_ddlreader_db");
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE z (a INTEGER)";
        using var reader = command.ExecuteReader();
        if (reader.FieldCount == 0)
        {
            // engines before 0.1.0 answer DDL with no result set at all
            Assert.False(reader.HasRows);
            Assert.False(reader.Read());
        }
        else
        {
            // 0.1.0+ answers the Snowflake-style one-row status message
            Assert.Equal(1, reader.FieldCount);
            Assert.True(reader.Read());
            Assert.Contains("successfully", reader.GetString(0), StringComparison.OrdinalIgnoreCase);
            Assert.False(reader.Read());
        }
    }

    [RequiresServerFact]
    public void UpdateDeleteAndMergeReportTheirCounts()
    {
        using var connection = Open("net_counts_db");
        Run(connection, "CREATE TABLE t (id INTEGER, v VARCHAR)");
        Assert.Equal(3, Run(connection, "INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')"));
        // UPDATE answers two columns (the second is "number of multi-joined rows updated");
        // the count must still come through rather than the -1 of an unrecognised result.
        Assert.Equal(2, Run(connection, "UPDATE t SET v = 'x' WHERE id <= 2"));
        Assert.Equal(1, Run(connection, "DELETE FROM t WHERE id = 3"));

        Run(connection, "CREATE TABLE src (id INTEGER, v VARCHAR)");
        Assert.Equal(2, Run(connection, "INSERT INTO src VALUES (2, 'm'), (9, 'n')"));
        var merged = Run(connection, """
            MERGE INTO t USING src ON t.id = src.id
            WHEN MATCHED THEN UPDATE SET t.v = src.v
            WHEN NOT MATCHED THEN INSERT (id, v) VALUES (src.id, src.v)
            """);
        Assert.Equal(2, merged); // one updated + one inserted, summed across the MERGE columns
    }

    [RequiresServerFact]
    public void MultiStatementResponsesWalkWithNextResult()
    {
        using var connection = Open("net_multi_db");
        // A request carrying more than one statement has to be asked for; 0 means any number.
        using (var declare = connection.CreateCommand())
        {
            declare.CommandText = "ALTER SESSION SET MULTI_STATEMENT_COUNT = 0";
            declare.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 AS a; SELECT 2 AS b;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.False(reader.NextResult());
    }

    [RequiresServerFact]
    public void CommandTimeoutStopsALongStatement()
    {
        using var connection = Open("net_timeout_db");
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT SYSTEM$WAIT(10)";
        var error = Assert.Throws<FrostlakeException>(() => command.ExecuteScalar());
        Assert.Contains("timed out", error.Message);
    }

    [RequiresServerFact]
    public void ClosingReleasesTheSessionAndRollsBackItsTransaction()
    {
        using var probe = Open("net_release_db");
        Skip.IfNot(EngineAtLeast(probe, 0, 1), "engines before 0.1.0 have no endpoint to release a session with");
        Run(probe, "CREATE TABLE acc (n INTEGER)");
        var sessions = ActiveSessions();
        var transactions = RowsOf(probe, "SHOW TRANSACTIONS");

        using var connection = new FrostlakeConnection(ConnectionString);
        connection.Open();
        connection.ChangeDatabase("net_release_db");
        // Begun in SQL, so the provider never saw it begin: closing must end it all the same.
        Run(connection, "BEGIN");
        Run(connection, "INSERT INTO acc VALUES (1)");
        Assert.Equal(sessions + 1, ActiveSessions());
        Assert.Equal(transactions + 1, RowsOf(probe, "SHOW TRANSACTIONS"));

        connection.Close();
        Assert.Equal(sessions, ActiveSessions());
        Assert.Equal(transactions, RowsOf(probe, "SHOW TRANSACTIONS"));
        Assert.Equal(0, RowsOf(probe, "SELECT n FROM acc"));
    }

    /// <summary>How many sessions the engine holds, as its <c>GET /api/sessions</c> counts them.</summary>
    private int ActiveSessions()
    {
        var server = new Uri(ConnectionString);
        using var http = new HttpClient();
        var body = http.GetStringAsync($"http://{server.Host}:{server.Port}/api/sessions").GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("activeSessions").GetInt32();
    }

    private static int RowsOf(FrostlakeConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            rows++;
        }
        return rows;
    }

    /// <summary>Releases a session behind its connection's back, as an idle expiry or a restart loses one.</summary>
    private void Release(string sessionId)
    {
        var server = new Uri(ConnectionString);
        using var http = new HttpClient();
        using var response = http.Send(new HttpRequestMessage(
            HttpMethod.Delete,
            $"http://{server.Host}:{server.Port}/api/sessions/{Uri.EscapeDataString(sessionId)}"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static object? Scalar(FrostlakeConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [RequiresServerFact]
    public void ALostSessionIsReplacedOnTheConnectionStringsScope()
    {
        using var probe = Open("net_recover_db");
        Skip.IfNot(EngineAtLeast(probe, 0, 1), "engines before 0.1.0 cannot refuse a session they no longer hold");
        using var connection = new FrostlakeConnection(ConnectionString + "/NET_RECOVER_DB");
        connection.Open();
        var lost = connection.SessionId!;
        Release(lost);

        Assert.Equal("NET_RECOVER_DB", Scalar(connection, "SELECT CURRENT_DATABASE()"));
        Assert.NotEqual(lost, connection.SessionId);
    }

    [RequiresServerFact]
    public void ALostSessionWithATransactionOpenIsReportedAndNothingRuns()
    {
        using var probe = Open("net_lost_tx_db");
        Skip.IfNot(EngineAtLeast(probe, 0, 1), "engines before 0.1.0 cannot refuse a session they no longer hold");
        Run(probe, "CREATE TABLE acc (n INTEGER)");
        using var connection = new FrostlakeConnection(ConnectionString + "/NET_LOST_TX_DB");
        connection.Open();
        Run(connection, "BEGIN");
        Run(connection, "INSERT INTO acc VALUES (1)");
        Release(connection.SessionId!);

        var error = Assert.Throws<FrostlakeSessionLostException>(() => Run(connection, "INSERT INTO acc VALUES (2)"));
        Assert.Contains("transaction", error.Message);
        // The release rolled the first row back, and the second statement never ran.
        Assert.Equal(0, RowsOf(probe, "SELECT n FROM acc"));
        // The connection carries on, in a fresh session on its connection string's scope.
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal("NET_LOST_TX_DB", Scalar(connection, "SELECT CURRENT_DATABASE()"));
    }

    [RequiresServerFact]
    public void ALostSessionWhoseSchemaMovedIsReported()
    {
        using var probe = Open("net_lost_use_db");
        Skip.IfNot(EngineAtLeast(probe, 0, 1), "engines before 0.1.0 cannot refuse a session they no longer hold");
        Run(probe, "CREATE SCHEMA other");
        using var connection = new FrostlakeConnection(ConnectionString + "/NET_LOST_USE_DB");
        connection.Open();
        Run(connection, "USE SCHEMA other");
        Release(connection.SessionId!);

        var error = Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT CURRENT_SCHEMA()"));
        Assert.Contains("context", error.Message);
        Assert.Equal("PUBLIC", Scalar(connection, "SELECT CURRENT_SCHEMA()"));
    }

    [RequiresServerFact]
    public void CloseConnectionBehaviourClosesTheConnection()
    {
        using var connection = Open("net_closebehaviour_db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var reader = command.ExecuteReader(CommandBehavior.CloseConnection);
        Assert.Equal(ConnectionState.Open, connection.State);
        reader.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [RequiresServerFact]
    public void DatabasePropertyTracksTheCurrentDatabase()
    {
        using var connection = Open("net_dbprop_db");
        Assert.Equal("net_dbprop_db", connection.Database);
        Assert.NotEqual("", connection.DataSource);
        Assert.NotEqual("", connection.ServerVersion);
    }

    [RequiresServerFact]
    public void OpeningAnUnknownDatabaseFailsAtOpen()
    {
        using var connection = new FrostlakeConnection(ConnectionString + "/NO_SUCH_DB_HERE");
        Assert.Throws<FrostlakeException>(() => connection.Open());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [RequiresServerFact]
    public async Task AsyncPathsWork()
    {
        await using var connection = new FrostlakeConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE OR REPLACE DATABASE net_async_db";
            await create.ExecuteNonQueryAsync();
        }
        connection.ChangeDatabase("net_async_db");
        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE t (n INTEGER)";
            await ddl.ExecuteNonQueryAsync();
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t VALUES (@n)";
            insert.Parameters.AddWithValue("n", 5);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        await using (var scalar = connection.CreateCommand())
        {
            scalar.CommandText = "SELECT n FROM t";
            Assert.Equal(5L, await scalar.ExecuteScalarAsync());
        }
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT n FROM t";
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5L, reader.GetInt64(0));
    }

    [RequiresServerFact]
    public void DollarQuotedBodiesSurviveBinding()
    {
        using var connection = Open("net_dollar_db");
        Run(connection, "CREATE TABLE t (n INTEGER)");
        using var command = connection.CreateCommand();
        // The ? inside the dollar-quoted body must reach the engine untouched.
        command.CommandText = "SELECT ? AS bound, $$ a ? b $$ AS body";
        command.Parameters.AddWithValue("", 7);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.Equal(" a ? b ", reader.GetString(1));
    }

    [RequiresServerFact]
    public void SchemaTableAndDataTableLoadWorkAgainstTheEngine()
    {
        using var connection = Open("net_schema_db");
        Run(connection, "CREATE TABLE t (id INTEGER, name VARCHAR)");
        Run(connection, "INSERT INTO t VALUES (1, 'Ada')");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM t";
        using var reader = command.ExecuteReader();
        var schema = reader.GetSchemaTable();
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Rows.Count);

        using var again = connection.CreateCommand();
        again.CommandText = "SELECT id, name FROM t";
        using var second = again.ExecuteReader();
        var table = new DataTable();
        table.Load(second);
        Assert.Equal(1, table.Rows.Count);
        Assert.Equal("Ada", table.Rows[0]["NAME"]);
    }

    /// <summary>
    /// A command may ask for a pack itself, with no ALTER SESSION. The count rides on that one
    /// command, so the session is left alone and the next unasked pack is still refused.
    /// </summary>
    [RequiresServerFact]
    public void APackRunsOnTheCommandsOwnCount()
    {
        using var connection = Open("net_pack_db");
        using (var packed = connection.CreateCommand())
        {
            packed.CommandText = "SELECT 1 AS a; SELECT 2 AS b";
            packed.Parameters.AddWithValue("MULTI_STATEMENT_COUNT", 2);
            using var reader = packed.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.True(reader.NextResult());
            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
        }

        using var unasked = connection.CreateCommand();
        unasked.CommandText = "SELECT 1 AS a; SELECT 2 AS b";
        FrostlakeException? refusal = null;
        try
        {
            using var reader = unasked.ExecuteReader();
        }
        catch (FrostlakeException e)
        {
            refusal = e;
        }
        // Only an engine that counts a request's statements refuses one, and this driver supports
        // older engines that run any pack they are sent. Against one of those there is no refusal
        // to observe, so the check is skipped rather than passed.
        Skip.If(refusal is null, "the engine does not enforce a statement count");
        Assert.Contains("did not match the desired statement count", refusal!.Message);
    }
}
