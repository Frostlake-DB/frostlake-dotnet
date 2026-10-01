using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frostlake.Data;

internal sealed class SqlResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }

    /// <summary>
    /// Whether the statement ran in a session the engine started for it. Absent (null) from an engine
    /// that predates the field, which is also one that ignores <c>requireSession</c> and has no
    /// <c>DELETE /api/sessions/{id}</c>: its presence, true or false, is the capability signal.
    /// </summary>
    [JsonPropertyName("newSession")] public bool? NewSession { get; set; }

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

    /// <summary>
    /// A text column's length in characters, or a binary column's in bytes. Null for every other
    /// type, and from any engine predating the field. The account reports this same number as the
    /// column's size.
    /// </summary>
    [JsonPropertyName("length")] public int? Length { get; set; }

    [JsonPropertyName("nullable")] public bool Nullable { get; set; } = true;
}
