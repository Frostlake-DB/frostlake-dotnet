using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frostlake.Data;

internal sealed class SqlResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("resultSets")] public List<ResultSetDto>? ResultSets { get; set; }
}

internal sealed class ResultSetDto
{
    [JsonPropertyName("columns")] public List<ColumnDto> Columns { get; set; } = new();
    [JsonPropertyName("rows")] public List<List<JsonElement>> Rows { get; set; } = new();
    [JsonPropertyName("rowCount")] public int RowCount { get; set; }
}

internal sealed class ColumnDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dataType")] public string? DataType { get; set; }
    [JsonPropertyName("precision")] public int? Precision { get; set; }
    [JsonPropertyName("scale")] public int? Scale { get; set; }
    [JsonPropertyName("nullable")] public bool Nullable { get; set; } = true;
}
