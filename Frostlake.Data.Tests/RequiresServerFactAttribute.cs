using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need a real engine. Without
/// <c>FROSTLAKE_CLASSPATH</c> the test reports as <em>skipped</em> rather than passing, so a run
/// with no server cannot be mistaken for a green suite.
/// </summary>
public sealed class RequiresServerFactAttribute : FactAttribute
{
    public RequiresServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FROSTLAKE_CLASSPATH")))
        {
            Skip = "needs an engine: set FROSTLAKE_CLASSPATH and JAVA_HOME (JDK 17)";
        }
    }
}
