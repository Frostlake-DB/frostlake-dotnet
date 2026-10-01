namespace Frostlake.Data;

/// <summary>
/// The engine no longer holds the connection's session: it expired, was released, or the server
/// restarted. The session held something a fresh one cannot reproduce (an open transaction, or
/// context set up with <c>USE</c>, <c>SET</c>, <c>ALTER SESSION</c> or a temporary object), so the
/// statement did not run and was not re-run. The connection stays open, and its next statement
/// starts a fresh session on the connection string's database and schema.
/// </summary>
public sealed class FrostlakeSessionLostException : FrostlakeException
{
    public FrostlakeSessionLostException(string message) : base(message) { }
}
