using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Frostlake.Data;

/// <summary>An input bind value; parameters apply positionally, in collection order, to <c>?</c> placeholders.</summary>
public sealed class FrostlakeParameter : DbParameter
{
    public FrostlakeParameter() { }

    public FrostlakeParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new FrostlakeException("only input parameters are supported");
            }
        }
    }

    public override bool IsNullable { get; set; } = true;

    private string _parameterName = "";

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? "";
    }

    public override int Size { get; set; }

    private string _sourceColumn = "";

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? "";
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
        DbType = DbType.Object;
    }
}
