namespace Frostlake.Data.Testkit;

/// <summary>
/// The statement never reached an answer: the engine could not be reached, the request failed or timed
/// out, or the test's reset was refused. The case is an ERROR, not a FAIL: no expectation stands for it.
/// </summary>
internal sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }

    public TransportException(string message, Exception inner) : base(message, inner) { }
}
