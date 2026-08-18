using System.Data.Common;

namespace Frostlake.Data;

/// <summary>
/// The ADO.NET entry point for provider-agnostic code; register with
/// <c>DbProviderFactories.RegisterFactory("Frostlake.Data", FrostlakeProviderFactory.Instance)</c>.
/// </summary>
public sealed class FrostlakeProviderFactory : DbProviderFactory
{
    public static readonly FrostlakeProviderFactory Instance = new();

    private FrostlakeProviderFactory() { }

    public override DbConnection CreateConnection()
    {
        return new FrostlakeConnection();
    }

    public override DbCommand CreateCommand()
    {
        return new FrostlakeCommand();
    }

    public override DbParameter CreateParameter()
    {
        return new FrostlakeParameter();
    }

    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new DbConnectionStringBuilder();
    }
}
