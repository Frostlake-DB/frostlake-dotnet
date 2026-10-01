using Frostlake.Data.Testkit;
using Xunit;
using Xunit.Abstractions;

namespace Frostlake.Data.Tests;

/// <summary>
/// The engine's testkit corpus, replayed through the provider by the <c>Frostlake.Data.Testkit</c>
/// runner in-process. It is a <see cref="CorpusFactAttribute"/>: it runs when <c>FL_CORPUS</c> names
/// frostlake's <c>engine/src/test/resources/testkit</c>, against <c>FROSTLAKE_URL</c> when that is set
/// and otherwise against a server of its own from <c>FROSTLAKE_CLASSPATH</c> (a fresh one: the corpus
/// creates objects outside its test database), and fails when any case failed or errored.
/// </summary>
public class TestkitCorpusTests
{
    private readonly ITestOutputHelper _output;

    public TestkitCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [CorpusFact]
    public void CorpusRunsOverTheProvider()
    {
        var corpus = Environment.GetEnvironmentVariable("FL_CORPUS")!.Trim();
        if (Program.SuiteFiles(corpus) is null)
        {
            Assert.Fail(Program.NoSuites(corpus));
        }
        var url = Environment.GetEnvironmentVariable("FROSTLAKE_URL")?.Trim();
        using var server = string.IsNullOrEmpty(url) ? new ServerFixture() : null;
        var log = new StringWriter();
        var exit = Program.Run(corpus, server?.ConnectionString ?? url, log, log);
        _output.WriteLine(log.ToString());
        Assert.True(exit == 0,
            $"the corpus runner exited {exit} (1: a case failed or errored, 2: it could not start); its output is the test's output");
    }
}
