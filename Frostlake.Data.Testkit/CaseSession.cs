using System.Data.Common;

namespace Frostlake.Data.Testkit;

/// <summary>
/// One test's session: a <see cref="FrostlakeConnection"/> of its own, opened for the test and closed
/// after it. Every statement goes through the provider's public surface — a command, its reader, and
/// the reader's typed values — so what the corpus checks is what an application would be handed.
/// </summary>
internal sealed class CaseSession : IDisposable
{
    /// <summary>
    /// The context every test starts from: a session that lets one request hold any number of
    /// statements (several cases send a script), then a fresh <c>test_db.test_schema</c>, made current.
    /// </summary>
    private static readonly string[] Reset =
    {
        "ALTER SESSION SET MULTI_STATEMENT_COUNT = 0",
        "CREATE OR REPLACE DATABASE test_db",
        "USE DATABASE test_db",
        "CREATE OR REPLACE SCHEMA test_schema",
        "USE SCHEMA test_schema",
    };

    /// <summary>How long one statement may take, as the reference runner's HTTP backend allows.</summary>
    private const int StatementTimeoutSeconds = 60;

    private readonly FrostlakeConnection _connection;

    private CaseSession(FrostlakeConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Opens a connection to the engine and resets its context for a test.</summary>
    /// <exception cref="TransportException">when the engine cannot be reached, or the reset is refused</exception>
    public static CaseSession Open(string url)
    {
        var connection = new FrostlakeConnection(url);
        try
        {
            connection.Open();
        }
        catch (FrostlakeException e)
        {
            connection.Dispose();
            throw new TransportException("cannot open a connection: " + e.Message, e);
        }
        var session = new CaseSession(connection);
        try
        {
            foreach (var sql in Reset)
            {
                var outcome = session.Execute(sql);
                if (outcome.Failed)
                {
                    throw new TransportException($"resetContext failed on '{sql}': {outcome.Error}");
                }
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>
    /// Runs one statement. The engine's refusal comes back in the outcome; a transport failure, which
    /// no expectation can stand for, throws.
    /// </summary>
    /// <exception cref="TransportException">when the request itself failed</exception>
    public StepOutcome Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = StatementTimeoutSeconds;
        DbDataReader reader;
        try
        {
            reader = command.ExecuteReader();
        }
        catch (FrostlakeException e) when (IsTransportFailure(e))
        {
            throw new TransportException(e.Message, e);
        }
        catch (FrostlakeException e)
        {
            return StepOutcome.Refused(e.Message);
        }
        // Outside the refusal handler on purpose: a value the provider cannot decode is its own
        // failure, never a refusal an expectation could stand for.
        using (reader)
        {
            return Read(reader);
        }
    }

    /// <summary>The first result: its column names and every row, each cell as the engine's text.</summary>
    private static StepOutcome Read(DbDataReader reader)
    {
        var width = reader.FieldCount;
        var columns = new List<string>(width);
        for (var i = 0; i < width; i++)
        {
            columns.Add(reader.GetName(i));
        }
        var rows = new List<IReadOnlyList<string?>>();
        while (reader.Read())
        {
            var cells = new string?[width];
            for (var i = 0; i < width; i++)
            {
                cells[i] = WireText.Cell(reader, i);
            }
            rows.Add(cells);
        }
        return StepOutcome.Grid(columns, rows);
    }

    /// <summary>
    /// Whether the provider failed to carry the statement at all, rather than relaying the engine's
    /// refusal: the request did not complete (the provider wraps the HTTP failure or the lapsed
    /// deadline), or the answer was not a response the protocol defines.
    /// </summary>
    private static bool IsTransportFailure(FrostlakeException e)
    {
        return e.InnerException is HttpRequestException or OperationCanceledException or IOException
               || e.Message.EndsWith("with unreadable body", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
