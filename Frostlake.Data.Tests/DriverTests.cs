using System.Data;
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
    public void DdlThroughExecuteReaderReportsAnEmptyResult()
    {
        using var connection = Open("net_ddlreader_db");
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE z (a INTEGER)";
        using var reader = command.ExecuteReader();
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
    }

    [RequiresServerFact]
    public void MultiStatementResponsesWalkWithNextResult()
    {
        using var connection = Open("net_multi_db");
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
}
