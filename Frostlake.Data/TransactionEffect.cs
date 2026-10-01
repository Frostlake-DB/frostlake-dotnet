namespace Frostlake.Data;

/// <summary>What a statement does to the session's transaction.</summary>
internal enum TransactionEffect
{
    None,
    Begins,
    Ends,
}
