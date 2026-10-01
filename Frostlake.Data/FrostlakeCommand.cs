using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Frostlake.Data;

/// <summary>
/// Executes one SQL text. <c>?</c> placeholders are filled from <see cref="Parameters"/> in
/// order and <c>@name</c>/<c>:name</c> placeholders by parameter name, both inlined client-side.
/// </summary>
public sealed class FrostlakeCommand : DbCommand
{
    private readonly FrostlakeParameterCollection _parameters = new();
    private string _commandText = "";
    private int _commandTimeout;

    /// <summary>
    /// The SQL to run. It goes to the engine as it is, the empty text included, so an empty or blank
    /// command is refused by the engine, with the account's own <c>Empty SQL statement.</c>, rather
    /// than here in other words.
    /// </summary>
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? "";
    }

    /// <summary>Seconds to wait for a statement; 0 (the default) waits indefinitely.</summary>
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set => _commandTimeout = value >= 0
            ? value
            : throw new FrostlakeException("CommandTimeout cannot be negative");
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new FrostlakeException("only CommandType.Text is supported");
            }
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public new FrostlakeParameterCollection Parameters => _parameters;

    public override void Cancel()
    {
        // Statements execute in one round trip; there is nothing to cancel.
    }

    public override void Prepare()
    {
        // Binding is client-side; there is nothing to prepare.
    }

    protected override DbParameter CreateDbParameter()
    {
        return new FrostlakeParameter();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var connection = Validate();
        var response = connection.Execute(RenderSql(), CommandTimeout, _parameters.MultiStatementCount());
        return NewReader(response, connection, behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken, _parameters.MultiStatementCount())
            .ConfigureAwait(false);
        return NewReader(response, connection, behavior);
    }

    private FrostlakeDataReader NewReader(
        SqlResponse response,
        FrostlakeConnection connection,
        CommandBehavior behavior)
    {
        return new FrostlakeDataReader(
            response.ResultSets ?? new List<ResultSetDto>(),
            connection,
            behavior.HasFlag(CommandBehavior.CloseConnection));
    }

    public override int ExecuteNonQuery()
    {
        var connection = Validate();
        return DmlCount(connection.Execute(RenderSql(), CommandTimeout, _parameters.MultiStatementCount())) ?? -1;
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken, _parameters.MultiStatementCount())
            .ConfigureAwait(false);
        return DmlCount(response) ?? -1;
    }

    public override object? ExecuteScalar()
    {
        var connection = Validate();
        return FirstCell(connection.Execute(RenderSql(), CommandTimeout, _parameters.MultiStatementCount()));
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken, _parameters.MultiStatementCount())
            .ConfigureAwait(false);
        return FirstCell(response);
    }

    /// <summary>Null when there is no row to read; a NULL first cell is <see cref="DBNull.Value"/>, per ADO.NET convention.</summary>
    private object? FirstCell(SqlResponse response)
    {
        using var reader = new FrostlakeDataReader(response.ResultSets ?? new List<ResultSetDto>());
        if (reader.FieldCount == 0 || !reader.Read())
        {
            return null;
        }
        return reader.GetValue(0);
    }

    private FrostlakeConnection Validate()
    {
        if (DbConnection is not FrostlakeConnection connection)
        {
            throw new FrostlakeException("command has no FrostlakeConnection");
        }
        if (DbTransaction is not null && !ReferenceEquals(DbTransaction.Connection, connection))
        {
            throw new FrostlakeException("the command's transaction belongs to a different connection");
        }
        return connection;
    }

    private string RenderSql()
    {
        var binds = _parameters.Binds();
        return binds.Count == 0 ? CommandText : SqlSubstitution.Substitute(CommandText, binds);
    }

    /// <summary>
    /// DML answers a single-row result whose count columns are named "number of rows …":
    /// one column for INSERT/DELETE, but UPDATE always carries a second "number of
    /// multi-joined rows updated" column and MERGE one column per action. The counts are
    /// summed the way the engine's own JDBC driver sums them — every column whose name
    /// starts with "number of rows", so the multi-joined column stays out. Anything else
    /// (a SELECT, or a DDL status message) has no update count.
    /// </summary>
    internal static int? DmlCount(SqlResponse response)
    {
        return response.ResultSets is { Count: > 0 } sets ? DmlCountOf(sets[0]) : null;
    }

    internal static int? DmlCountOf(ResultSetDto resultSet)
    {
        if (resultSet.Rows.Count != 1)
        {
            return null;
        }
        var row = resultSet.Rows[0];
        long? total = null;
        for (var i = 0; i < resultSet.Columns.Count && i < row.Count; i++)
        {
            if (resultSet.Columns[i].Name.StartsWith("number of rows", StringComparison.OrdinalIgnoreCase)
                && row[i].ValueKind == JsonValueKind.Number
                && row[i].TryGetInt64(out var count))
            {
                total = (total ?? 0) + count;
            }
        }
        return total is null ? null : (int)Math.Min(total.Value, int.MaxValue);
    }
}
