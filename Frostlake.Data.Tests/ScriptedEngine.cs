using System.Net;
using System.Text;
using System.Text.Json;

namespace Frostlake.Data.Tests;

/// <summary>A request the connection sent, with its JSON body read back.</summary>
internal sealed record SentRequest(
    string Method,
    string Path,
    string? Sql,
    string? SessionId,
    bool? RequireSession,
    bool? AutoCommit);

/// <summary>
/// A transport that answers from a script and records what it was sent, so a test sees every round
/// trip a connection makes. An unscripted request fails the test.
/// </summary>
internal sealed class ScriptedEngine : HttpMessageHandler
{
    public const string Health = """{"status":"healthy","activeSessions":0}""";

    public const string Released =
        """{"errorMessage":null,"executionTimeMs":0,"newSession":false,"resultSets":[],"sessionId":null,"success":true}""";

    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly List<SentRequest> _sent = new();

    public IReadOnlyList<SentRequest> Sent => _sent;

    public int Pending => _script.Count;

    /// <summary>The SQL of every <c>POST /api/execute</c>, in order.</summary>
    public List<string> Statements()
    {
        var statements = new List<string>();
        foreach (var request in _sent)
        {
            if (request.Path == "/api/execute" && request.Sql is not null)
            {
                statements.Add(request.Sql);
            }
        }
        return statements;
    }

    public List<SentRequest> Executes()
    {
        return _sent.FindAll(request => request.Path == "/api/execute");
    }

    public void Reply(string body)
    {
        Reply(HttpStatusCode.OK, body);
    }

    public void Reply(HttpStatusCode status, string body)
    {
        _script.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
    }

    /// <summary>The request fails the way a refused or reset socket does.</summary>
    public void Fail(Exception error)
    {
        _script.Enqueue(_ => Task.FromException<HttpResponseMessage>(error));
    }

    /// <summary>The request is never answered: it lasts until the caller gives up on it.</summary>
    public void Hang()
    {
        _script.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? sql = null;
        string? sessionId = null;
        bool? requireSession = null;
        bool? autoCommit = null;
        if (request.Content is not null)
        {
            using var document = JsonDocument.Parse(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            var root = document.RootElement;
            sql = Text(root, "sql");
            sessionId = Text(root, "sessionId");
            requireSession = Flag(root, "requireSession");
            autoCommit = Flag(root, "autoCommit");
        }
        _sent.Add(new SentRequest(request.Method.Method, request.RequestUri!.AbsolutePath, sql, sessionId,
            requireSession, autoCommit));
        if (_script.Count == 0)
        {
            throw new InvalidOperationException($"unscripted request: {request.Method} {request.RequestUri} {sql}");
        }
        return _script.Dequeue()(cancellationToken);
    }

    private static string? Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static string StatusSet(string text)
    {
        return $$"""{"columns":[{"dataType":"VARCHAR","name":"status","nullable":false,"precision":0,"scale":0}],"rowCount":1,"rows":[["{{text}}"]]}""";
    }

    public static string NumberSet(string name, long value)
    {
        return $$"""{"columns":[{"dataType":"NUMBER","name":"{{name}}","nullable":false,"precision":38,"scale":0}],"rowCount":1,"rows":[[{{value}}]]}""";
    }

    public static readonly string Ok = StatusSet("Statement executed successfully.");

    /// <summary>An answer from an engine that reports <c>newSession</c> (0.1.0 and later).</summary>
    public static string Answer(string sessionId, bool started, params string[] sets)
    {
        return $$"""{"errorMessage":null,"executionTimeMs":1,"newSession":{{(started ? "true" : "false")}},"resultSets":[{{string.Join(",", sets)}}],"sessionId":"{{sessionId}}","success":true}""";
    }

    /// <summary>An answer from an engine that predates <c>newSession</c> (0.0.7).</summary>
    public static string LegacyAnswer(string sessionId, params string[] sets)
    {
        return $$"""{"errorMessage":null,"executionTimeMs":1,"resultSets":[{{string.Join(",", sets)}}],"sessionId":"{{sessionId}}","success":true}""";
    }

    public static string Refused(string sessionId, string message)
    {
        return $$"""{"errorMessage":{{JsonSerializer.Serialize(message)}},"executionTimeMs":0,"newSession":false,"resultSets":[],"sessionId":"{{sessionId}}","success":false}""";
    }

    /// <summary>The 404 a <c>requireSession</c> request gets when its session is gone.</summary>
    public static string SessionGone(string sessionId)
    {
        return $$"""{"errorMessage":"Session '{{sessionId}}' does not exist or has expired.","executionTimeMs":0,"newSession":false,"resultSets":[],"sessionId":null,"success":false}""";
    }
}
