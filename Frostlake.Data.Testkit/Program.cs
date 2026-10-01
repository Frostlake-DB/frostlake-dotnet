using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Frostlake.Data.Testkit;

/// <summary>
/// Replays the engine's testkit corpus — its language-neutral JSON suites, format in the
/// <c>SCHEMA.md</c> beside them — through this provider, and reports every case the way the engine's
/// reference runner does. The corpus belongs to the engine, so suites added there are picked up here
/// with no change.
/// <para>
/// Environment: <c>FL_CORPUS</c> names the corpus, frostlake's <c>engine/src/test/resources/testkit</c>,
/// whose <c>suites/*.json</c> are replayed (unset, the run is skipped); <c>FROSTLAKE_URL</c>
/// (<c>frostlake://host:port</c>, required) names a running <c>DatabaseHttpServer</c>;
/// <c>FROSTLAKE_TESTKIT_REPORT</c> the TSV report (default <c>results/testkit-dotnet.tsv</c>), beside which
/// <c>missing-apis-dotnet.md</c> lands; and <c>FROSTLAKE_TESTKIT_FILTER</c> a substring that picks only
/// the suites whose name contains it. The test project replays the corpus through <see cref="Run"/> too.
/// </para>
/// <para>
/// Semantics follow the reference runner, except that every test gets a connection, and so an engine
/// session, of its own: reset to an empty <c>test_db.test_schema</c>, it runs the test's steps in
/// order and is closed after them. The first failed check stops the test; values compare after the
/// corpus's normalization; a test whose skip clause names
/// <c>dotnet</c> or <c>http</c> (the transport every statement rides) is skipped; and a check that
/// needs an error code is recorded as a missing API rather than failed. Exits 1 when any case failed
/// or errored, 2 when it could not start, and 0 otherwise.
/// </para>
/// </summary>
internal static class Program
{
    private const string Backend = "dotnet";

    /// <summary>The skip-clause names that apply here: this provider's, and the transport's it speaks.</summary>
    private static readonly string[] SkipKeys = { Backend, "http" };

    private const int MaxDetailLength = 4000;

    private const int MaxListedFailures = 25;

    /// <summary>Why a run without <c>FL_CORPUS</c> is skipped.</summary>
    internal const string CorpusUnset =
        "set FL_CORPUS to frostlake's engine/src/test/resources/testkit to replay the testkit corpus";

    private static int Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var corpus = Setting("FL_CORPUS");
        if (corpus is null)
        {
            Console.WriteLine($"testkit [dotnet]: {CorpusUnset}");
            return 0;
        }
        return Run(corpus, Setting("FROSTLAKE_URL"), Console.Out, Console.Error);
    }

    /// <summary>
    /// Replays the corpus in the testkit directory <paramref name="corpus"/> against the engine at
    /// <paramref name="url"/>, writing the progress and the summary to <paramref name="output"/> and what
    /// kept the run from starting to <paramref name="errors"/>, and answers the exit code.
    /// </summary>
    internal static int Run(string corpus, string? url, TextWriter output, TextWriter errors)
    {
        var files = SuiteFiles(corpus);
        if (files is null)
        {
            errors.WriteLine($"testkit [dotnet]: {NoSuites(corpus)}");
            return 2;
        }
        if (url is null)
        {
            errors.WriteLine(
                "testkit [dotnet]: set FROSTLAKE_URL to a running DatabaseHttpServer (frostlake://127.0.0.1:<port>)");
            return 2;
        }
        var checkout = CheckoutRoot();
        var reportPath = Path.GetFullPath(Setting("FROSTLAKE_TESTKIT_REPORT")
                                          ?? Path.Combine(checkout, "results", $"testkit-{Backend}.tsv"));
        var filter = Setting("FROSTLAKE_TESTKIT_FILTER");

        var report = new StringBuilder("suite\ttest\tstatus\tfailedStep\tdetail\tms\n");
        var counts = new Dictionary<string, int> { ["PASS"] = 0, ["FAIL"] = 0, ["ERROR"] = 0, ["SKIP"] = 0 };
        var missing = new List<(string Note, string Where)>();
        var failures = new List<string>();
        var suiteCount = 0;
        var clock = Stopwatch.StartNew();
        output.WriteLine(
            $"testkit [dotnet]: {files.Length} suite file(s) from {Path.GetFullPath(Path.Combine(corpus, "suites"))} against {url}");

        foreach (var file in files)
        {
            using var document = ParseSuite(file, errors);
            if (document is null)
            {
                // A suite that does not parse has no cases to count, so no report could stand for it.
                return 2;
            }
            var root = document.RootElement;
            var suite = StringProperty(root, "suite") ?? Path.GetFileNameWithoutExtension(file);
            if (filter is not null
                && !suite.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileNameWithoutExtension(file).Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            suiteCount++;
            if (!root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var test in tests.EnumerateArray())
            {
                var name = StringProperty(test, "name") ?? "";
                var result = SkipReason(test) is { } reason
                    ? new CaseResult("SKIP", null, reason, 0)
                    : RunCase(url, suite, name, test, missing);
                counts[result.Status]++;
                report.Append(Field(suite)).Append('\t')
                    .Append(Field(name)).Append('\t')
                    .Append(result.Status).Append('\t')
                    .Append(result.FailedStep?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
                    .Append(Field(Truncate(result.Detail ?? ""))).Append('\t')
                    .Append(result.Milliseconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (result.Status is "FAIL" or "ERROR" && failures.Count < MaxListedFailures)
                {
                    failures.Add($"  {suite} / {name}: {result.Status} {Truncate(Field(result.Detail ?? ""), 300)}");
                }
            }
            if (suiteCount % 100 == 0)
            {
                output.WriteLine(
                    $"  ... {suiteCount} suites: {counts["PASS"]} passed, {counts["FAIL"] + counts["ERROR"]} failed, {counts["SKIP"]} skipped");
            }
        }

        var reportDirectory = Path.GetDirectoryName(reportPath)!;
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(reportPath, report.ToString());
        var missingPath = Path.Combine(reportDirectory, $"missing-apis-{Backend}.md");
        File.WriteAllText(missingPath, MissingApis(missing));

        var failed = counts["FAIL"] + counts["ERROR"];
        output.WriteLine();
        output.WriteLine($"testkit [dotnet]: {counts["PASS"]} passed, {failed} failed, {counts["SKIP"]} skipped");
        output.WriteLine(
            $"  FAIL {counts["FAIL"]}, ERROR {counts["ERROR"]}; {suiteCount} suite(s) in {clock.Elapsed.TotalSeconds:0.0} s; "
            + $"{missing.Count} check(s) need an API the HTTP transport lacks");
        output.WriteLine($"  report {reportPath}");
        output.WriteLine($"  missing APIs {missingPath}");
        foreach (var line in failures)
        {
            output.WriteLine(line);
        }
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// The suites of the testkit directory <paramref name="corpus"/>, its <c>suites/*.json</c> in name
    /// order, or null when there are none: the directory is not the corpus.
    /// </summary>
    internal static string[]? SuiteFiles(string corpus)
    {
        var suites = Path.Combine(corpus, "suites");
        if (!Directory.Exists(suites))
        {
            return null;
        }
        var files = Directory.GetFiles(suites, "*.json");
        if (files.Length == 0)
        {
            return null;
        }
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>Why an <c>FL_CORPUS</c> that names no corpus cannot be replayed, naming the value.</summary>
    internal static string NoSuites(string corpus)
    {
        return $"FL_CORPUS is {corpus}, but there are no *.json suites in {Path.GetFullPath(Path.Combine(corpus, "suites"))}"
               + " (set it to frostlake's engine/src/test/resources/testkit)";
    }

    /// <summary>
    /// Runs one test on a connection of its own: the reset, then every step in order until the first
    /// check that fails. A statement the engine refused is an outcome like any other; a request that
    /// never completed makes the case an ERROR.
    /// </summary>
    private static CaseResult RunCase(string url, string suite, string name, JsonElement test,
        List<(string Note, string Where)> missing)
    {
        var clock = Stopwatch.StartNew();
        int? step = null;
        try
        {
            using var session = CaseSession.Open(url);
            if (test.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            {
                var number = 0;
                foreach (var each in steps.EnumerateArray())
                {
                    step = ++number;
                    var sql = StringProperty(each, "sql") ?? "";
                    var outcome = session.Execute(sql);
                    var verdict = Expectations.Check(
                        each.TryGetProperty("expect", out var expect) ? expect : null,
                        outcome);
                    if (verdict.MissingApi is not null)
                    {
                        missing.Add((verdict.MissingApi, $"{suite}/{name} step {number}"));
                    }
                    if (!verdict.Passed)
                    {
                        return new CaseResult("FAIL", number, $"{verdict.Detail}  [sql: {sql}]",
                            clock.ElapsedMilliseconds);
                    }
                }
            }
            return new CaseResult("PASS", null, null, clock.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            var kind = e is TransportException ? "" : e.GetType().Name + ": ";
            return new CaseResult("ERROR", step, kind + e.Message, clock.ElapsedMilliseconds);
        }
    }

    private static JsonDocument? ParseSuite(string file, TextWriter errors)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (JsonException e)
        {
            errors.WriteLine($"testkit [dotnet]: {file} does not parse: {e.Message}");
            return null;
        }
    }

    /// <summary>The skip clause's reason when it names this provider or its transport, else null.</summary>
    private static string? SkipReason(JsonElement test)
    {
        if (!test.TryGetProperty("skip", out var skip) || skip.ValueKind != JsonValueKind.Object
            || !skip.TryGetProperty("backends", out var backends) || backends.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var mine in SkipKeys)
        {
            foreach (var backend in backends.EnumerateArray())
            {
                if (backend.ValueKind == JsonValueKind.String
                    && string.Equals(backend.GetString(), mine, StringComparison.OrdinalIgnoreCase))
                {
                    return $"skip[{mine}]: {StringProperty(skip, "reason") ?? "no reason given"}";
                }
            }
        }
        return null;
    }

    private static string MissingApis(List<(string Note, string Where)> missing)
    {
        var text = new StringBuilder();
        text.Append("# Missing APIs for backend `").Append(Backend).Append("`\n\n");
        text.Append("Checks the corpus asks for that the HTTP protocol cannot express, so this provider cannot either.\n");
        text.Append("They are recorded, not failed: the expectations are already in the suite files, so the day the\n");
        text.Append("API exists the checks light up without touching a single test.\n\n");
        if (missing.Count == 0)
        {
            text.Append("None were asked for in this run.\n");
            return text.ToString();
        }
        var notes = new List<string>();
        foreach (var (note, _) in missing)
        {
            if (!notes.Contains(note))
            {
                notes.Add(note);
            }
        }
        foreach (var note in notes)
        {
            var places = new List<string>();
            foreach (var (each, where) in missing)
            {
                if (each == note)
                {
                    places.Add(where);
                }
            }
            text.Append("- ").Append(note).Append(" (").Append(places.Count).Append(" check(s): ")
                .Append(string.Join(", ", places)).Append(")\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// The checkout this runner belongs to: the directory holding <c>Frostlake.sln</c> above the build
    /// output, or failing that the working directory, which is where the documented command runs.
    /// </summary>
    private static string CheckoutRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Frostlake.sln")))
            {
                return directory.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? Setting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>A TSV field: tabs and line breaks would split the row, so they become spaces.</summary>
    private static string Field(string text)
    {
        return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string Truncate(string text, int limit = MaxDetailLength)
    {
        return text.Length <= limit ? text : text[..limit] + " …";
    }
}
