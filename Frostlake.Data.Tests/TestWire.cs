using System.Text.Json;

namespace Frostlake.Data.Tests;

/// <summary>
/// Builds readers straight from wire-shaped JSON so the reader can be tested without a server.
/// </summary>
internal static class TestWire
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static ResultSetDto Set(string json)
    {
        return JsonSerializer.Deserialize<ResultSetDto>(json, Options)
               ?? throw new InvalidOperationException("bad test JSON");
    }

    public static FrostlakeDataReader Reader(params string[] sets)
    {
        var parsed = new List<ResultSetDto>();
        foreach (var set in sets)
        {
            parsed.Add(Set(set));
        }
        return new FrostlakeDataReader(parsed);
    }

    /// <summary>A reader positioned on the first row of a single-column result.</summary>
    public static FrostlakeDataReader Column(string dataType, string rowsJson, int scale = 0, int precision = 38)
    {
        var reader = Reader($$"""
            {"columns":[{"name":"V","dataType":"{{dataType}}","precision":{{precision}},"scale":{{scale}},"nullable":true}],
             "rows":{{rowsJson}}}
            """);
        reader.Read();
        return reader;
    }
}
