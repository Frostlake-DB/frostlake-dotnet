using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Frostlake.Data;

/// <summary>
/// A connection to a running Frostlake <c>DatabaseHttpServer</c>. One <c>POST /api/execute</c>
/// per statement; the server issues a session id on first contact and the connection echoes it
/// back, so session state (current database/schema, transactions) persists across statements.
/// <para>
/// Note: the server exposes no endpoint for dropping a session, so <see cref="Close"/> can only
/// release the client's handle — the server reclaims the session when its own idle timeout
/// expires. Prefer holding a connection open over rapid open/close cycles.
/// </para>
/// </summary>
public sealed class FrostlakeConnection : DbConnection
{
    /// <summary>Per-request deadlines are applied with a token, so the shared client must not impose its own.</summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private string _connectionString = "";
    private FrostlakeConnectionOptions? _options;
    private string? _sessionId;
    private string? _database;
    private string? _serverVersion;
    private ConnectionState _state = ConnectionState.Closed;

    internal bool AutoCommit { get; set; } = true;

    internal FrostlakeTransaction? ActiveTransaction { get; set; }

    public FrostlakeConnection() { }

    public FrostlakeConnection(string connectionString)
    {
        _connectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state == ConnectionState.Open)
            {
                throw new FrostlakeException("cannot change the connection string of an open connection");
            }
            _connectionString = value ?? "";
        }
    }

    public override string Database => _database ?? _options?.Database ?? "";

    public override string DataSource => _options?.BaseUrl ?? "";

    /// <summary>The engine's <c>CURRENT_VERSION()</c>, read once per connection.</summary>
    public override string ServerVersion
    {
        get
        {
            if (_serverVersion is not null)
            {
                return _serverVersion;
            }
            if (_state != ConnectionState.Open)
            {
                return "";
            }
            try
            {
                var response = Execute("SELECT CURRENT_VERSION()", 0);
                var sets = response.ResultSets;
                if (sets is { Count: > 0 } && sets[0].Rows.Count > 0 && sets[0].Rows[0].Count > 0)
                {
                    var cell = sets[0].Rows[0][0];
                    _serverVersion = cell.ValueKind == JsonValueKind.String ? cell.GetString()! : cell.GetRawText();
                    return _serverVersion;
                }
            }
            catch (FrostlakeException)
            {
                // an engine too old to answer CURRENT_VERSION() still deserves a usable property
            }
            _serverVersion = "";
            return _serverVersion;
        }
    }

    public override ConnectionState State => _state;

    public override void Open()
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }
        _options = FrostlakeConnectionOptions.Parse(_connectionString);
        HttpResponseMessage response;
        try
        {
            response = Http.Send(new HttpRequestMessage(HttpMethod.Get, _options.BaseUrl + "/api/health"));
        }
        catch (HttpRequestException e)
        {
            throw new FrostlakeException($"cannot reach {_options.BaseUrl}: {e.Message}", e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new FrostlakeException($"server unhealthy: HTTP {(int)response.StatusCode}");
            }
        }
        _state = ConnectionState.Open;
        _serverVersion = null;
        _database = null;
        try
        {
            // Applied eagerly so an unknown database fails Open() rather than the first statement.
            if (_options.Database is not null)
            {
                RoundTrip("USE DATABASE " + QuoteIdentifier(_options.Database), 0);
                _database = _options.Database;
            }
            if (_options.Schema is not null)
            {
                RoundTrip("USE SCHEMA " + QuoteIdentifier(_options.Schema), 0);
            }
        }
        catch (FrostlakeException)
        {
            _state = ConnectionState.Closed;
            _sessionId = null;
            throw;
        }
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }
        _options = FrostlakeConnectionOptions.Parse(_connectionString);
        try
        {
            using var response = await Http
                .GetAsync(_options.BaseUrl + "/api/health", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new FrostlakeException($"server unhealthy: HTTP {(int)response.StatusCode}");
            }
        }
        catch (HttpRequestException e)
        {
            throw new FrostlakeException($"cannot reach {_options.BaseUrl}: {e.Message}", e);
        }
        _state = ConnectionState.Open;
        _serverVersion = null;
        _database = null;
        try
        {
            if (_options.Database is not null)
            {
                await RoundTripAsync("USE DATABASE " + QuoteIdentifier(_options.Database), 0, cancellationToken)
                    .ConfigureAwait(false);
                _database = _options.Database;
            }
            if (_options.Schema is not null)
            {
                await RoundTripAsync("USE SCHEMA " + QuoteIdentifier(_options.Schema), 0, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (FrostlakeException)
        {
            _state = ConnectionState.Closed;
            _sessionId = null;
            throw;
        }
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }
        if (!AutoCommit)
        {
            try
            {
                // Leave no half-open transaction behind on a session the server still holds.
                RoundTrip("ROLLBACK", 0);
            }
            catch (FrostlakeException)
            {
                // the session may already be gone; closing must not throw
            }
        }
        ActiveTransaction = null;
        _state = ConnectionState.Closed;
        _sessionId = null;
        _database = null;
        _serverVersion = null;
        AutoCommit = true;
    }

    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        Execute("USE DATABASE " + QuoteIdentifier(databaseName), 0);
        _database = databaseName;
    }

    public void ChangeSchema(string schemaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        Execute("USE SCHEMA " + QuoteIdentifier(schemaName), 0);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
        {
            throw new FrostlakeException("connection is not open");
        }
        if (ActiveTransaction is not null)
        {
            throw new FrostlakeException("a transaction is already open on this connection");
        }
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.ReadCommitted))
        {
            throw new FrostlakeException($"{isolationLevel} is not supported; Frostlake reads committed data");
        }
        Execute("BEGIN", 0);
        AutoCommit = false;
        var transaction = new FrostlakeTransaction(this);
        ActiveTransaction = transaction;
        return transaction;
    }

    protected override DbCommand CreateDbCommand()
    {
        return new FrostlakeCommand { Connection = this };
    }

    public new FrostlakeCommand CreateCommand()
    {
        return new FrostlakeCommand { Connection = this };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }

    internal SqlResponse Execute(string sql, int timeoutSeconds)
    {
        EnsureOpen();
        return RoundTrip(sql, timeoutSeconds);
    }

    internal Task<SqlResponse> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken cancellationToken)
    {
        EnsureOpen();
        return RoundTripAsync(sql, timeoutSeconds, cancellationToken);
    }

    private void EnsureOpen()
    {
        if (_state != ConnectionState.Open)
        {
            throw new FrostlakeException("connection is not open");
        }
    }

    private HttpRequestMessage BuildRequest(string sql)
    {
        var payload = new Dictionary<string, object?> { ["sql"] = sql, ["autoCommit"] = AutoCommit };
        if (_sessionId is not null)
        {
            payload["sessionId"] = _sessionId;
        }
        return new HttpRequestMessage(HttpMethod.Post, _options!.BaseUrl + "/api/execute")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private SqlResponse RoundTrip(string sql, int timeoutSeconds)
    {
        using var deadline = Deadline(timeoutSeconds);
        HttpResponseMessage response;
        try
        {
            response = Http.Send(BuildRequest(sql), deadline?.Token ?? CancellationToken.None);
        }
        catch (HttpRequestException e)
        {
            throw new FrostlakeException($"request failed: {e.Message}", e);
        }
        catch (OperationCanceledException e) when (deadline is not null && deadline.IsCancellationRequested)
        {
            throw new FrostlakeException($"command timed out after {timeoutSeconds}s", e);
        }
        using (response)
        {
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return Interpret(reader.ReadToEnd(), response);
        }
    }

    private async Task<SqlResponse> RoundTripAsync(string sql, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(timeoutSeconds);
        using var linked = deadline is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var token = linked?.Token ?? cancellationToken;
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(BuildRequest(sql), token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new FrostlakeException($"request failed: {e.Message}", e);
        }
        catch (OperationCanceledException e) when (deadline is not null && deadline.IsCancellationRequested)
        {
            throw new FrostlakeException($"command timed out after {timeoutSeconds}s", e);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(body, response);
        }
    }

    private static CancellationTokenSource? Deadline(int timeoutSeconds)
    {
        return timeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)) : null;
    }

    /// <summary>Failed statements answer with a non-2xx status AND the error payload in the body.</summary>
    private SqlResponse Interpret(string body, HttpResponseMessage response)
    {
        SqlResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SqlResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            parsed = null;
        }
        if (parsed is null)
        {
            throw new FrostlakeException($"HTTP {(int)response.StatusCode} with unreadable body");
        }
        if (parsed.SessionId is not null)
        {
            _sessionId = parsed.SessionId;
        }
        if (!parsed.Success)
        {
            throw new FrostlakeException(parsed.ErrorMessage ?? "statement failed");
        }
        return parsed;
    }

    private static string QuoteIdentifier(string name)
    {
        return Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_$]*$") && name.ToUpperInvariant() == name
            ? name
            : "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
