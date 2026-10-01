using Frostlake.Data.Testkit;
using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>
/// A fact for the testkit corpus replay. Without <c>FL_CORPUS</c> the test reports as <em>skipped</em>,
/// and so it does without an engine (neither <c>FROSTLAKE_URL</c> nor <c>FROSTLAKE_CLASSPATH</c>) — except
/// that an <c>FL_CORPUS</c> naming no corpus is never skipped: the test fails on it, naming the value.
/// </summary>
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        var corpus = Environment.GetEnvironmentVariable("FL_CORPUS");
        if (string.IsNullOrWhiteSpace(corpus))
        {
            Skip = Program.CorpusUnset;
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTLAKE_URL"))
                 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FROSTLAKE_CLASSPATH"))
                 && Program.SuiteFiles(corpus.Trim()) is not null)
        {
            Skip = "needs an engine: set FROSTLAKE_URL, or FROSTLAKE_CLASSPATH and JAVA_HOME (JDK 17)";
        }
    }
}
