using System.Data.Common;

namespace Frostlake.Data;

/// <summary>Raised for every driver and engine error; the message carries the engine's wording.</summary>
public sealed class FrostlakeException : DbException
{
    public FrostlakeException(string message) : base(message) { }

    public FrostlakeException(string message, Exception inner) : base(message, inner) { }
}
