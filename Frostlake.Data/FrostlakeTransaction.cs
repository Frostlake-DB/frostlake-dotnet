using System.Data;
using System.Data.Common;

namespace Frostlake.Data;

/// <summary>Wraps an open <c>BEGIN … COMMIT/ROLLBACK</c> span; disposing an unfinished transaction rolls it back.</summary>
public sealed class FrostlakeTransaction : DbTransaction
{
    private readonly FrostlakeConnection _connection;
    private bool _completed;
    private bool _lost;

    internal FrostlakeTransaction(FrostlakeConnection connection)
    {
        _connection = connection;
    }

    protected override DbConnection DbConnection => _connection;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    public override void Commit()
    {
        Finish("COMMIT");
    }

    public override void Rollback()
    {
        Finish("ROLLBACK");
    }

    /// <summary>
    /// The engine session that held the transaction is gone, and the transaction with it: nothing it
    /// did was committed. Disposing it sends nothing, and Commit or Rollback says so.
    /// </summary>
    internal void Lose()
    {
        _completed = true;
        _lost = true;
    }

    private void Finish(string statement)
    {
        if (_lost)
        {
            throw new FrostlakeSessionLostException(
                "this transaction went with the engine session that held it, and nothing it did was committed");
        }
        if (_completed)
        {
            throw new FrostlakeException("this transaction has already completed");
        }
        _connection.Execute(statement, 0);
        _connection.AutoCommit = true;
        _connection.ActiveTransaction = null;
        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed && _connection.State == ConnectionState.Open)
        {
            Rollback();
        }
        base.Dispose(disposing);
    }
}
