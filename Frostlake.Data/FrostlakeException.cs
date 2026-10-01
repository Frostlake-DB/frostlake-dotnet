using System.Data.Common;

namespace Frostlake.Data;

/// <summary>
/// Raised for every driver and engine error; the message carries the engine's wording. A session
/// the engine lost with something in it raises the <see cref="FrostlakeSessionLostException"/> kind.
/// </summary>
public class FrostlakeException : DbException
{
    public FrostlakeException(string message) : base(message) { }

    public FrostlakeException(string message, Exception inner) : base(message, inner) { }
}
