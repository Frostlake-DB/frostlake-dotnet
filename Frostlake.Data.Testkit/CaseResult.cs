namespace Frostlake.Data.Testkit;

/// <summary>One report row: PASS, FAIL, ERROR or SKIP, the step that failed, why, and how long the case took.</summary>
internal readonly record struct CaseResult(string Status, int? FailedStep, string? Detail, long Milliseconds);
