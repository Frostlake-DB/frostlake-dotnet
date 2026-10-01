namespace Frostlake.Data.Testkit;

/// <summary>
/// The verdict on one step's expectations: whether they held, why not when they did not, and a check
/// the transport could not express, when there was one.
/// </summary>
internal readonly record struct Verdict(bool Passed, string? Detail, string? MissingApi)
{
    public static Verdict Success => new(true, null, null);

    public static Verdict SuccessMissing(string missingApi)
    {
        return new Verdict(true, null, missingApi);
    }

    public static Verdict Failure(string detail)
    {
        return new Verdict(false, detail, null);
    }
}
