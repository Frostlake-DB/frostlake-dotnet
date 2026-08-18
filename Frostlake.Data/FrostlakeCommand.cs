using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

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
        var response = connection.Execute(RenderSql(), CommandTimeout);
        return NewReader(response, connection, behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken)
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
        return DmlCount(connection.Execute(RenderSql(), CommandTimeout)) ?? -1;
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return DmlCount(response) ?? -1;
    }

    public override object? ExecuteScalar()
    {
        var connection = Validate();
        return FirstCell(connection.Execute(RenderSql(), CommandTimeout));
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var connection = Validate();
        var response = await connection
            .ExecuteAsync(RenderSql(), CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return FirstCell(response);
    }

    private object? FirstCell(SqlResponse response)
    {
        using var reader = new FrostlakeDataReader(response.ResultSets ?? new List<ResultSetDto>());
        if (reader.FieldCount == 0 || !reader.Read())
        {
            return null;
        }
        var value = reader.GetValue(0);
        return value is DBNull ? null : value;
    }

    private FrostlakeConnection Validate()
    {
        if (DbConnection is not FrostlakeConnection connection)
        {
            throw new FrostlakeException("command has no FrostlakeConnection");
        }
        if (CommandText.Length == 0)
        {
            throw new FrostlakeException("CommandText is empty");
        }
        if (DbTransaction is not null && !ReferenceEquals(DbTransaction.Connection, connection))
        {
            throw new FrostlakeException("the command's transaction belongs to a different connection");
        }
        return connection;
    }

    private string RenderSql()
    {
        return _parameters.Count == 0 ? CommandText : SqlSubstitution.Substitute(CommandText, _parameters.Binds());
    }

    /// <summary>DML answers with a one-cell "number of rows …" result; anything else has no update count.</summary>
    internal static int? DmlCount(SqlResponse response)
    {
        return response.ResultSets is { Count: > 0 } sets ? DmlCountOf(sets[0]) : null;
    }

    internal static int? DmlCountOf(ResultSetDto resultSet)
    {
        if (resultSet is { Columns.Count: 1, Rows.Count: 1 }
            && resultSet.Columns[0].Name.StartsWith("number of rows", StringComparison.OrdinalIgnoreCase)
            && resultSet.Rows[0].Count > 0
            && resultSet.Rows[0][0].TryGetInt32(out var count))
        {
            return count;
        }
        return null;
    }
}
