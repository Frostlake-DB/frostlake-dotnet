using System.Data;
using System.Diagnostics;
using System.Net;
using Xunit;
using static Frostlake.Data.Tests.ScriptedEngine;

namespace Frostlake.Data.Tests;

/// <summary>
/// How a connection keeps its idea of the engine session in step with the engine's, over a scripted
/// transport: every request a scenario makes is one it scripted. No engine required.
/// </summary>
public class SessionTests
{
    private const string Dsn = "frostlake://scripted:18082/APP?schema=PUBLIC";
    private const string UseDatabase = "USE DATABASE APP";
    private const string UseSchema = "USE SCHEMA PUBLIC";

    /// <summary>A connection opened on a scripted engine that reports <c>newSession</c>.</summary>
    private static (ScriptedEngine Engine, FrostlakeConnection Connection) Opened()
    {
        var engine = new ScriptedEngine();
        engine.Reply(Health);
        engine.Reply(Answer("s1", true, Ok));
        engine.Reply(Answer("s1", false, Ok));
        var connection = new FrostlakeConnection(Dsn, engine);
        connection.Open();
        return (engine, connection);
    }

    /// <summary>A connection opened on a scripted engine from before <c>newSession</c> (0.0.7).</summary>
    private static (ScriptedEngine Engine, FrostlakeConnection Connection) OpenedOnAnOlderEngine()
    {
        var engine = new ScriptedEngine();
        engine.Reply(Health);
        engine.Reply(LegacyAnswer("old1", Ok));
        engine.Reply(LegacyAnswer("old1", Ok));
        var connection = new FrostlakeConnection(Dsn, engine);
        connection.Open();
        return (engine, connection);
    }

    private static object? Scalar(FrostlakeConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Run(FrostlakeConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void RequireSessionIsSentOnlyOnceTheEngineHasShownItReportsNewSession()
    {
        var (engine, connection) = Opened();
        var executes = engine.Executes();
        // The first request names no session, so there is nothing to require yet; its answer is what
        // settles the capability.
        Assert.Equal(UseDatabase, executes[0].Sql);
        Assert.Null(executes[0].SessionId);
        Assert.Null(executes[0].RequireSession);
        Assert.Equal("s1", executes[1].SessionId);
        Assert.True(executes[1].RequireSession);

        engine.Reply(Answer("s1", false, NumberSet("N", 1)));
        Assert.Equal(1L, Scalar(connection, "SELECT 1 AS N"));
        var last = engine.Sent[^1];
        Assert.Equal("s1", last.SessionId);
        Assert.True(last.RequireSession);
        Assert.True(last.AutoCommit);
    }

    [Fact]
    public void AnOlderEngineIsNeverSentRequireSessionNorADelete()
    {
        var (engine, connection) = OpenedOnAnOlderEngine();
        engine.Reply(LegacyAnswer("old1", NumberSet("N", 1)));
        Assert.Equal(1L, Scalar(connection, "SELECT 1 AS N"));
        foreach (var request in engine.Executes())
        {
            // Its parser may refuse a field it does not know.
            Assert.Null(request.RequireSession);
        }
        Assert.Equal("old1", engine.Sent[^1].SessionId);
        connection.Close();
        Assert.DoesNotContain(engine.Sent, request => request.Method == "DELETE");
        Assert.Equal(0, engine.Pending);
    }

    [Fact]
    public void WithNoScopeTheSessionStartsWithTheFirstStatement()
    {
        var engine = new ScriptedEngine();
        engine.Reply(Health);
        var connection = new FrostlakeConnection("frostlake://scripted:18082", engine);
        connection.Open();
        Assert.Single(engine.Sent);
        engine.Reply(Answer("n1", true, NumberSet("N", 1)));
        engine.Reply(Answer("n1", false, NumberSet("N", 2)));
        Scalar(connection, "SELECT 1 AS N");
        Scalar(connection, "SELECT 2 AS N");
        var executes = engine.Executes();
        Assert.Null(executes[0].SessionId);
        Assert.Null(executes[0].RequireSession);
        Assert.Equal("n1", executes[1].SessionId);
        Assert.True(executes[1].RequireSession);
    }

    [Fact]
    public void ALostSessionIsReplacedOnTheScopeAndTheStatementSentOnceMore()
    {
        var (engine, connection) = Opened();
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, NumberSet("N", 1)));
        Assert.Equal(1L, Scalar(connection, "SELECT 1 AS N"));
        Assert.Equal(
            new[] { UseDatabase, UseSchema, "SELECT 1 AS N", UseDatabase, UseSchema, "SELECT 1 AS N" },
            engine.Statements());
        var executes = engine.Executes();
        // The lost id is dropped: the scope starts a fresh session, and the statement runs in it.
        Assert.Null(executes[3].SessionId);
        Assert.Null(executes[3].RequireSession);
        Assert.Equal("s2", executes[4].SessionId);
        Assert.Equal("s2", executes[5].SessionId);
        Assert.True(executes[5].RequireSession);
        Assert.Equal("s2", connection.SessionId);
        Assert.Equal(0, engine.Pending);
    }

    [Fact]
    public void ASecondLossRaisesAndTheConnectionStaysUsable()
    {
        var (engine, connection) = Opened();
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s2"));
        var error = Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT 1 AS N"));
        Assert.Contains("just started", error.Message);
        Assert.Equal(
            new[] { UseDatabase, UseSchema, "SELECT 1 AS N", UseDatabase, UseSchema, "SELECT 1 AS N" },
            engine.Statements());
        Assert.Equal(ConnectionState.Open, connection.State);

        engine.Reply(Answer("s3", true, Ok));
        engine.Reply(Answer("s3", false, Ok));
        engine.Reply(Answer("s3", false, NumberSet("N", 2)));
        Assert.Equal(2L, Scalar(connection, "SELECT 2 AS N"));
        Assert.Equal(0, engine.Pending);
    }

    [Fact]
    public void ALostSessionWithAnOpenTransactionIsReportedNotReplaced()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        var transaction = connection.BeginTransaction();
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        var error = Assert.Throws<FrostlakeSessionLostException>(() => Run(connection, "INSERT INTO T VALUES (1)"));
        Assert.Contains("transaction", error.Message);
        Assert.Contains("did not run", error.Message);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Null(connection.SessionId);

        // The transaction went with its session: it cannot be committed, and disposing it sends nothing.
        var count = engine.Sent.Count;
        Assert.Throws<FrostlakeSessionLostException>(() => transaction.Commit());
        transaction.Dispose();
        Assert.Equal(count, engine.Sent.Count);

        // The next statement starts over on the scope, outside any transaction.
        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, NumberSet("N", 1)));
        Scalar(connection, "SELECT 1 AS N");
        Assert.Equal(
            new[] { UseDatabase, UseSchema, "BEGIN", "INSERT INTO T VALUES (1)", UseDatabase, UseSchema, "SELECT 1 AS N" },
            engine.Statements());
        Assert.True(engine.Sent[^1].AutoCommit);

        // A new transaction can begin: the lost one no longer holds the connection.
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, Ok));
        using (connection.BeginTransaction())
        {
        }
        Assert.Equal(new[] { "BEGIN", "ROLLBACK" }, engine.Statements()[^2..]);
        Assert.Equal(0, engine.Pending);
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("begin transaction")]
    [InlineData("START TRANSACTION")]
    public void ATransactionBegunInSqlIsTrackedToo(string begin)
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        Run(connection, begin);
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        var error = Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT 1"));
        Assert.Contains("transaction", error.Message);
        Assert.Equal(new[] { UseDatabase, UseSchema, begin, "SELECT 1" }, engine.Statements());
    }

    [Theory]
    [InlineData("USE SCHEMA OTHER")]
    [InlineData("SET X = 1")]
    [InlineData("UNSET X")]
    [InlineData("ALTER SESSION SET TIMEZONE = 'UTC'")]
    [InlineData("CREATE TEMPORARY TABLE T (A INT)")]
    [InlineData("CREATE SCHEMA OTHER")]
    [InlineData("DROP DATABASE OTHER")]
    public void ALostSessionWhoseContextMovedIsReportedNotReplaced(string move)
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        Run(connection, move);
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        var error = Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT * FROM T"));
        Assert.Contains("context", error.Message);
        Assert.Contains("not re-run", error.Message);
        Assert.Equal(new[] { UseDatabase, UseSchema, move, "SELECT * FROM T" }, engine.Statements());
        Assert.Equal(ConnectionState.Open, connection.State);

        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, NumberSet("N", 1)));
        Assert.Equal(1L, Scalar(connection, "SELECT 1 AS N"));
        Assert.Equal(new[] { UseDatabase, UseSchema, "SELECT 1 AS N" }, engine.Statements()[4..]);
    }

    [Fact]
    public void ChangeDatabaseMovesTheContextAndTheScopeComesBackAfterALoss()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        connection.ChangeDatabase("OTHER");
        Assert.Equal("OTHER", connection.Database);
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT 1"));
        Assert.Equal("APP", connection.Database);
    }

    [Fact]
    public void StatementsThatMoveTheSessionAreNoticedAnywhereInARequest()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, NumberSet("N", 1), Ok));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 AS N; USE SCHEMA OTHER";
            command.Parameters.AddWithValue("MULTI_STATEMENT_COUNT", 2);
            command.ExecuteNonQuery();
        }
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        Assert.Throws<FrostlakeSessionLostException>(() => Scalar(connection, "SELECT 2"));
    }

    [Fact]
    public void ACommittedTransactionLeavesNothingBehindToLose()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        engine.Reply(Answer("s1", false, Ok));
        using (var transaction = connection.BeginTransaction())
        {
            transaction.Commit();
        }
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, NumberSet("N", 1)));
        Assert.Equal(1L, Scalar(connection, "SELECT 1 AS N"));
        Assert.Equal(0, engine.Pending);
    }

    [Fact]
    public void ARefusedStatementLeavesTheSessionUsable()
    {
        var (engine, connection) = Opened();
        engine.Reply(Refused("s1", "SQL compilation error:\nObject 'NOPE' does not exist or not authorized."));
        var error = Assert.Throws<FrostlakeException>(() => Scalar(connection, "SELECT * FROM NOPE"));
        Assert.IsNotType<FrostlakeSessionLostException>(error);
        Assert.StartsWith("SQL compilation error", error.Message);
        Assert.Equal("s1", connection.SessionId);
    }

    [Fact]
    public void AServerThatReplacedTheSessionGetsTheScopeBackBeforeTheNextStatement()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", true, NumberSet("N", 1)));
        Scalar(connection, "SELECT 1 AS N");
        engine.Reply(Answer("s1", false, Ok));
        engine.Reply(Answer("s1", false, Ok));
        engine.Reply(Answer("s1", false, NumberSet("N", 2)));
        Scalar(connection, "SELECT 2 AS N");
        Assert.Equal(
            new[] { UseDatabase, UseSchema, "SELECT 1 AS N", UseDatabase, UseSchema, "SELECT 2 AS N" },
            engine.Statements());
    }

    [Fact]
    public async Task TheAsynchronousPathFollowsTheSameRules()
    {
        var (engine, connection) = Opened();
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        engine.Reply(Answer("s2", true, Ok));
        engine.Reply(Answer("s2", false, Ok));
        engine.Reply(Answer("s2", false, NumberSet("N", 3)));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 3 AS N";
            Assert.Equal(3L, await command.ExecuteScalarAsync());
        }
        Assert.Equal(
            new[] { UseDatabase, UseSchema, "SELECT 3 AS N", UseDatabase, UseSchema, "SELECT 3 AS N" },
            engine.Statements());

        engine.Reply(Answer("s2", false, Ok));
        await using (var use = connection.CreateCommand())
        {
            use.CommandText = "USE SCHEMA OTHER";
            await use.ExecuteNonQueryAsync();
        }
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s2"));
        await using var lost = connection.CreateCommand();
        lost.CommandText = "SELECT 4";
        await Assert.ThrowsAsync<FrostlakeSessionLostException>(() => lost.ExecuteScalarAsync());
    }

    [Fact]
    public void ServerVersionDoesNotHideALostTransaction()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        Run(connection, "BEGIN");
        engine.Reply(HttpStatusCode.NotFound, SessionGone("s1"));
        Assert.Throws<FrostlakeSessionLostException>(() => connection.ServerVersion);
    }

    [Fact]
    public void ClosingReleasesTheSessionOnce()
    {
        var (engine, connection) = Opened();
        engine.Reply(Released);
        connection.Close();
        var last = engine.Sent[^1];
        Assert.Equal("DELETE", last.Method);
        Assert.Equal("/api/sessions/s1", last.Path);
        Assert.Equal(0, engine.Pending);
        var count = engine.Sent.Count;
        connection.Close();
        connection.Dispose();
        Assert.Equal(count, engine.Sent.Count);
        Assert.Throws<FrostlakeException>(() => Scalar(connection, "SELECT 1"));
    }

    [Fact]
    public void ClosingWithATransactionOpenReleasesTheSessionWhichRollsItBack()
    {
        var (engine, connection) = Opened();
        engine.Reply(Answer("s1", false, Ok));
        connection.BeginTransaction();
        engine.Reply(Released);
        connection.Close();
        // The release rolls the transaction back; a ROLLBACK beforehand would be one more round trip.
        Assert.Equal(new[] { UseDatabase, UseSchema, "BEGIN" }, engine.Statements());
        Assert.Equal("DELETE", engine.Sent[^1].Method);
        Assert.Equal(0, engine.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingOnAnOlderEngineRollsBackAnOpenTransactionInstead(bool inSql)
    {
        var (engine, connection) = OpenedOnAnOlderEngine();
        engine.Reply(LegacyAnswer("old1", Ok));
        if (inSql)
        {
            Run(connection, "BEGIN");
        }
        else
        {
            connection.BeginTransaction();
        }
        engine.Reply(LegacyAnswer("old1", Ok));
        connection.Close();
        Assert.Equal("ROLLBACK", engine.Sent[^1].Sql);
        Assert.DoesNotContain(engine.Sent, request => request.Method == "DELETE");
        Assert.Equal(0, engine.Pending);
    }

    [Theory]
    [InlineData("404")]
    [InlineData("405")]
    [InlineData("closed socket")]
    [InlineData("hang")]
    public void ClosingNeverRaises(string answer)
    {
        var (engine, connection) = Opened();
        connection.ReleaseDeadline = TimeSpan.FromMilliseconds(300);
        switch (answer)
        {
            case "404":
                engine.Reply(HttpStatusCode.NotFound,
                    """{"success":false,"errorMessage":"Session 's1' does not exist or has expired.","sessionId":null}""");
                break;
            case "405":
                engine.Reply(HttpStatusCode.MethodNotAllowed, "");
                break;
            case "closed socket":
                engine.Fail(new HttpRequestException("An error occurred while sending the request.",
                    new IOException("Unable to read data from the transport connection: Connection reset by peer.")));
                break;
            default:
                engine.Hang();
                break;
        }
        var clock = Stopwatch.StartNew();
        connection.Close();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Close took {clock.Elapsed}");
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Single(engine.Sent, request => request.Method == "DELETE");
    }

    [Fact]
    public void AScopeTheEngineRefusesFailsTheOpenAndReleasesTheSession()
    {
        var engine = new ScriptedEngine();
        engine.Reply(Health);
        engine.Reply(Refused("s9", "Database 'APP' does not exist or not authorized."));
        engine.Reply(Released);
        var connection = new FrostlakeConnection(Dsn, engine);
        var error = Assert.Throws<FrostlakeException>(() => connection.Open());
        Assert.Contains("APP", error.Message);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal("DELETE", engine.Sent[^1].Method);
        Assert.Equal("/api/sessions/s9", engine.Sent[^1].Path);
        Assert.Equal(0, engine.Pending);
    }

    [Fact]
    public void ReopeningForgetsWhatTheLastEngineSaid()
    {
        var (engine, connection) = OpenedOnAnOlderEngine();
        connection.Close();
        engine.Reply(Health);
        engine.Reply(Answer("s1", true, Ok));
        engine.Reply(Answer("s1", false, Ok));
        connection.Open();
        Assert.True(engine.Sent[^1].RequireSession);
    }
}
