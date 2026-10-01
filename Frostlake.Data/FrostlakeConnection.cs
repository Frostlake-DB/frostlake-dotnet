using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Frostlake.Data;

/// <summary>
/// A connection to a running Frostlake <c>DatabaseHttpServer</c>. One <c>POST /api/execute</c>
/// per statement; the server issues a session id on first contact and the connection echoes it
/// back, so session state (current database/schema, transactions) persists across statements.
/// <para>
/// <see cref="Close"/> releases the engine session (<c>DELETE /api/sessions/{id}</c>), which rolls
/// back a transaction still open on it: one begun with a SQL <c>BEGIN</c> as much as one from
/// <see cref="DbConnection.BeginTransaction()"/>. An engine before 0.1.0 has no such endpoint, and
/// there the session lives on until the server's own idle timeout reclaims it.
/// </para>
/// <para>
/// Once the engine has shown that it reports <c>newSession</c> (0.1.0 and later), every request
/// naming the session also sends <c>requireSession: true</c>, so a session the engine no longer
/// holds is refused (404) rather than silently replaced by a fresh one at the server's default
/// scope. The connection then starts a fresh session on its connection string's database and
/// schema and sends the statement once more, unless the lost session held something the fresh one
/// cannot reproduce (an open transaction, or context from <c>USE</c>, <c>SET</c>/<c>UNSET</c>,
/// <c>ALTER SESSION</c>, a temporary object or a <c>CREATE</c>/<c>DROP</c> of a database or schema):
/// then it raises <see cref="FrostlakeSessionLostException"/> and the statement does not run.
/// </para>
/// </summary>
public sealed class FrostlakeConnection : DbConnection
{
    /// <summary>Per-request deadlines are applied with a token, so the shared client must not impose its own.</summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The client this connection's requests go through: the shared one, or a test's transport.</summary>
    private readonly HttpClient _http = Http;

    private string _connectionString = "";
    private FrostlakeConnectionOptions? _options;
    private string? _sessionId;
    private string? _database;
    private string? _serverVersion;
    private ConnectionState _state = ConnectionState.Closed;

    /// <summary>
    /// Whether the engine reports <c>newSession</c>, which arrived together with <c>requireSession</c>
    /// and <c>DELETE /api/sessions/{id}</c>. Null until the first answer that names a session.
    /// </summary>
    private bool? _tracksSessions;

    /// <summary>Whether the connection string's database and schema are on the session.</summary>
    private bool _scopeApplied;

    /// <summary>Set once a statement left state behind that a fresh session would not have.</summary>
    private bool _dirty;

    /// <summary>Whether a transaction is open on the session, begun in SQL or by <see cref="DbConnection.BeginTransaction()"/>.</summary>
    private bool _inTransaction;

    /// <summary>How long <see cref="Close"/> waits for the engine to release the session.</summary>
    internal TimeSpan ReleaseDeadline { get; set; } = TimeSpan.FromSeconds(10);

    internal bool AutoCommit { get; set; } = true;

    internal FrostlakeTransaction? ActiveTransaction { get; set; }

    /// <summary>The engine's id for the session, once it has named one.</summary>
    internal string? SessionId => _sessionId;

    public FrostlakeConnection() { }

    public FrostlakeConnection(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>A connection whose requests go through <paramref name="transport"/> rather than the network.</summary>
    internal FrostlakeConnection(string connectionString, HttpMessageHandler transport) : this(connectionString)
    {
        _http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
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
            catch (FrostlakeException e) when (e is not FrostlakeSessionLostException)
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
            response = _http.Send(new HttpRequestMessage(HttpMethod.Get, _options.BaseUrl + "/api/health"));
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
        Opened();
        try
        {
            // Applied eagerly so an unknown database fails Open() rather than the first statement.
            ApplyScope(0);
        }
        catch (FrostlakeException)
        {
            FailedToOpen();
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
            using var response = await _http
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
        Opened();
        try
        {
            await ApplyScopeAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (FrostlakeException)
        {
            FailedToOpen();
            throw;
        }
    }

    /// <summary>A fresh start: nothing is known yet about the engine or a session on it.</summary>
    private void Opened()
    {
        _state = ConnectionState.Open;
        _serverVersion = null;
        _database = null;
        _sessionId = null;
        _tracksSessions = null;
        _scopeApplied = false;
        _dirty = false;
        _inTransaction = false;
    }

    /// <summary>The scope was refused: a session it started is released rather than left to expire.</summary>
    private void FailedToOpen()
    {
        ReleaseSession();
        _state = ConnectionState.Closed;
        _sessionId = null;
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }
        if (_tracksSessions == true)
        {
            ReleaseSession();
        }
        else if (_sessionId is not null && (_inTransaction || !AutoCommit))
        {
            try
            {
                // An engine without the release endpoint keeps the session: leave no half-open
                // transaction behind on it.
                Post("ROLLBACK", (int)Math.Ceiling(ReleaseDeadline.TotalSeconds));
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
        _inTransaction = false;
        AutoCommit = true;
    }

    /// <summary>
    /// Ends the engine session, so nothing the connection began outlives it: the engine rolls back a
    /// transaction still open on the session, whether a SQL <c>BEGIN</c> or
    /// <see cref="DbConnection.BeginTransaction()"/> opened it, as ADO.NET expects of
    /// <see cref="Close"/>. Only an engine that reports <c>newSession</c> is asked: one before 0.1.0
    /// has no such endpoint and keeps the session until its idle timeout. Never throws.
    /// </summary>
    private void ReleaseSession()
    {
        if (_sessionId is null || _options is null || _tracksSessions != true)
        {
            return;
        }
        try
        {
            using var deadline = new CancellationTokenSource(ReleaseDeadline);
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                _options.BaseUrl + "/api/sessions/" + Uri.EscapeDataString(_sessionId));
            using var response = _http.Send(request, deadline.Token);
        }
        catch (HttpRequestException)
        {
            // the server is gone, and the session with it
        }
        catch (OperationCanceledException)
        {
            // the server did not answer in time; its idle timeout reclaims the session
        }
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

    internal SqlResponse Execute(string sql, int timeoutSeconds, int? multiStatementCount = null)
    {
        EnsureOpen();
        if (!_scopeApplied)
        {
            ApplyScope(timeoutSeconds);
        }
        var response = Post(sql, timeoutSeconds, multiStatementCount)
            ?? Recover(sql, timeoutSeconds, multiStatementCount);
        return Finish(sql, response);
    }

    internal Task<SqlResponse> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken cancellationToken,
        int? multiStatementCount = null)
    {
        EnsureOpen();
        return ExecuteCoreAsync(sql, timeoutSeconds, cancellationToken, multiStatementCount);
    }

    private async Task<SqlResponse> ExecuteCoreAsync(string sql, int timeoutSeconds,
        CancellationToken cancellationToken, int? multiStatementCount)
    {
        if (!_scopeApplied)
        {
            await ApplyScopeAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        var response = await PostAsync(sql, timeoutSeconds, cancellationToken, multiStatementCount)
                           .ConfigureAwait(false)
                       ?? await RecoverAsync(sql, timeoutSeconds, cancellationToken, multiStatementCount)
                           .ConfigureAwait(false);
        return Finish(sql, response);
    }

    private void EnsureOpen()
    {
        if (_state != ConnectionState.Open)
        {
            throw new FrostlakeException("connection is not open");
        }
    }

    /// <summary>The connection string's database and schema, as the statements that put them on a session.</summary>
    private List<string> ScopeStatements()
    {
        var statements = new List<string>(2);
        if (_options!.Database is not null)
        {
            statements.Add("USE DATABASE " + QuoteIdentifier(_options.Database));
        }
        if (_options.Schema is not null)
        {
            statements.Add("USE SCHEMA " + QuoteIdentifier(_options.Schema));
        }
        return statements;
    }

    /// <summary>
    /// Puts the connection string's database and schema on the session, starting one when there is
    /// none. A session lost part-way through is started over once.
    /// </summary>
    private void ApplyScope(int timeoutSeconds)
    {
        var statements = ScopeStatements();
        for (var attempt = 0; ; attempt++)
        {
            var lost = false;
            foreach (var statement in statements)
            {
                var response = Post(statement, timeoutSeconds);
                if (response is null)
                {
                    lost = true;
                    break;
                }
                if (!response.Success)
                {
                    throw Refusal(response);
                }
            }
            if (!lost)
            {
                ScopeApplied();
                return;
            }
            _sessionId = null;
            if (attempt > 0)
            {
                throw DropRefusedSession();
            }
        }
    }

    private async Task ApplyScopeAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var statements = ScopeStatements();
        for (var attempt = 0; ; attempt++)
        {
            var lost = false;
            foreach (var statement in statements)
            {
                var response = await PostAsync(statement, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    lost = true;
                    break;
                }
                if (!response.Success)
                {
                    throw Refusal(response);
                }
            }
            if (!lost)
            {
                ScopeApplied();
                return;
            }
            _sessionId = null;
            if (attempt > 0)
            {
                throw DropRefusedSession();
            }
        }
    }

    private void ScopeApplied()
    {
        _scopeApplied = true;
        _dirty = false;
        _database = null;
    }

    /// <summary>
    /// The engine no longer holds the session (it expired, was released, or the server restarted),
    /// and nothing ran. With a transaction or a moved context gone with it, re-running would put the
    /// statement somewhere its author did not intend, so that is refused; otherwise a fresh session
    /// on the connection string's scope takes over and the statement is sent once more.
    /// </summary>
    private SqlResponse Recover(string sql, int timeoutSeconds, int? multiStatementCount)
    {
        ThrowIfIrreplaceable();
        ApplyScope(timeoutSeconds);
        return Post(sql, timeoutSeconds, multiStatementCount) ?? throw DropRefusedSession();
    }

    private async Task<SqlResponse> RecoverAsync(string sql, int timeoutSeconds,
        CancellationToken cancellationToken, int? multiStatementCount)
    {
        ThrowIfIrreplaceable();
        await ApplyScopeAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);
        return await PostAsync(sql, timeoutSeconds, cancellationToken, multiStatementCount).ConfigureAwait(false)
               ?? throw DropRefusedSession();
    }

    /// <summary>Drops the lost session, and raises when what it held cannot be put back.</summary>
    private void ThrowIfIrreplaceable()
    {
        var hadTransaction = _inTransaction;
        var hadContext = _dirty;
        _sessionId = null;
        ForgetSession();
        if (hadTransaction)
        {
            throw new FrostlakeSessionLostException(
                "the engine no longer holds this connection's session (it expired, was released, or the server "
                + "restarted), so its open transaction is gone; the statement did not run");
        }
        if (hadContext)
        {
            throw new FrostlakeSessionLostException(
                "the engine no longer holds this connection's session (it expired, was released, or the server "
                + "restarted), and the context set up on it (USE, SET, ALTER SESSION or a temporary object) went "
                + "with it, so the statement was not re-run; the next statement starts a fresh session on the "
                + "connection's scope");
        }
    }

    /// <summary>Forgets what the session held: it is gone, and the next statement puts the scope back first.</summary>
    private void ForgetSession()
    {
        _scopeApplied = false;
        _dirty = false;
        _inTransaction = false;
        _database = null;
        ActiveTransaction?.Lose();
        ActiveTransaction = null;
        AutoCommit = true;
    }

    /// <summary>The fresh session was refused too: it is dropped, and the next statement starts over.</summary>
    private FrostlakeSessionLostException DropRefusedSession()
    {
        _sessionId = null;
        _scopeApplied = false;
        return new FrostlakeSessionLostException("the engine refused a session it had just started");
    }

    private static FrostlakeException Refusal(SqlResponse response)
    {
        return new FrostlakeException(response.ErrorMessage ?? "statement failed");
    }

    /// <summary>Raises a failed statement's error, and otherwise notes what it left on the session.</summary>
    private SqlResponse Finish(string sql, SqlResponse response)
    {
        if (!response.Success)
        {
            throw Refusal(response);
        }
        foreach (var statement in SessionEffects.Statements(sql))
        {
            if (SessionEffects.TouchesSession(statement))
            {
                _dirty = true;
            }
            switch (SessionEffects.TransactionEffectOf(statement))
            {
                case TransactionEffect.Begins:
                    _inTransaction = true;
                    break;
                case TransactionEffect.Ends:
                    _inTransaction = false;
                    break;
            }
        }
        return response;
    }

    private HttpRequestMessage BuildRequest(string sql, int? multiStatementCount = null)
    {
        var payload = new Dictionary<string, object?> { ["sql"] = sql, ["autoCommit"] = AutoCommit };
        // Absent unless the command declared one, so a command that says nothing leaves the session's
        // MULTI_STATEMENT_COUNT in charge. Zero is a real answer - any number - not nothing to say.
        if (multiStatementCount is not null)
        {
            payload["multiStatementCount"] = multiStatementCount;
        }
        if (_sessionId is not null)
        {
            payload["sessionId"] = _sessionId;
            if (_tracksSessions == true)
            {
                // Resume this session or refuse: otherwise an engine that no longer holds it starts a
                // fresh one under the same id, and the statement runs in the wrong context. Never sent
                // to an engine that predates the field.
                payload["requireSession"] = true;
            }
        }
        return new HttpRequestMessage(HttpMethod.Post, _options!.BaseUrl + "/api/execute")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// One <c>POST /api/execute</c>, without recovery: the engine's answer, successful or not, or null
    /// when the engine refused the session id as one it no longer holds, and nothing ran.
    /// </summary>
    private SqlResponse? Post(string sql, int timeoutSeconds, int? multiStatementCount = null)
    {
        var sentId = _sessionId is not null;
        using var deadline = Deadline(timeoutSeconds);
        HttpResponseMessage response;
        try
        {
            response = _http.Send(BuildRequest(sql, multiStatementCount),
                deadline?.Token ?? CancellationToken.None);
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
            return Interpret(reader.ReadToEnd(), response, sentId);
        }
    }

    private async Task<SqlResponse?> PostAsync(string sql, int timeoutSeconds,
        CancellationToken cancellationToken, int? multiStatementCount = null)
    {
        var sentId = _sessionId is not null;
        using var deadline = Deadline(timeoutSeconds);
        using var linked = deadline is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var token = linked?.Token ?? cancellationToken;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(BuildRequest(sql, multiStatementCount), token).ConfigureAwait(false);
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
            return Interpret(body, response, sentId);
        }
    }

    private static CancellationTokenSource? Deadline(int timeoutSeconds)
    {
        return timeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)) : null;
    }

    /// <summary>
    /// Failed statements answer with a non-2xx status AND the error payload in the body, and the
    /// caller decides what the failure means. A 404 naming no session, to a request that named one,
    /// is the engine refusing a session it no longer holds (<c>requireSession</c>): null.
    /// </summary>
    private SqlResponse? Interpret(string body, HttpResponseMessage response, bool sentId)
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
        if (response.StatusCode == HttpStatusCode.NotFound && sentId && !parsed.Success && parsed.SessionId is null)
        {
            return null;
        }
        Absorb(parsed, sentId);
        return parsed;
    }

    /// <summary>Takes the session an answer names, and what the answer says about the engine.</summary>
    private void Absorb(SqlResponse parsed, bool sentId)
    {
        if (parsed.SessionId is null)
        {
            return;
        }
        _sessionId = parsed.SessionId;
        if (parsed.NewSession is { } started)
        {
            _tracksSessions = true;
            if (started && sentId)
            {
                // The engine ran the statement in a fresh session in place of ours: whatever the old
                // one held is gone, and the scope goes back on before the next statement.
                ForgetSession();
            }
        }
        else
        {
            _tracksSessions ??= false;
        }
    }

    // A plain name is sent bare, so it folds to upper case the way it does in SQL:
    // "my_db" selects MY_DB. Quoted as given, a lower-case name would ask for a
    // lower-case object, which USE refuses — it resolves names exactly, as live
    // does. Anything that cannot be written bare is quoted as given.
    private static string QuoteIdentifier(string name)
    {
        return Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_$]*$")
            ? name
            : "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
