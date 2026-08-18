using System.Data;
using System.Data.Common;

namespace Frostlake.Data;

/// <summary>Wraps an open <c>BEGIN … COMMIT/ROLLBACK</c> span; disposing an unfinished transaction rolls it back.</summary>
public sealed class FrostlakeTransaction : DbTransaction
{
    private readonly FrostlakeConnection _connection;
    private bool _completed;

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

    private void Finish(string statement)
    {
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
