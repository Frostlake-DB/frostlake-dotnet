using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>
/// A fact for tests that need a real engine. Without <c>FROSTLAKE_CLASSPATH</c> the test reports
/// as <em>skipped</em> rather than passing, so a run with no server cannot be mistaken for a green
/// suite. It is a <see cref="SkippableFactAttribute"/> so that a test which finds, once it is
/// running, that the engine cannot answer a check can skip that check the same way.
/// </summary>
public sealed class RequiresServerFactAttribute : SkippableFactAttribute
{
    public RequiresServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FROSTLAKE_CLASSPATH")))
        {
            Skip = "needs an engine: set FROSTLAKE_CLASSPATH and JAVA_HOME (JDK 17)";
        }
    }
}
