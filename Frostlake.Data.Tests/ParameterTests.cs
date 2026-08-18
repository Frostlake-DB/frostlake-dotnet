using System.Data;
using System.Data.Common;
using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>The parameter, collection and factory surfaces. No engine required.</summary>
public class ParameterTests
{
    [Fact]
    public void ParameterDefaultsAndGuards()
    {
        var parameter = new FrostlakeParameter();
        Assert.Equal(DbType.Object, parameter.DbType);
        Assert.Equal(ParameterDirection.Input, parameter.Direction);
        Assert.True(parameter.IsNullable);
        Assert.Equal("", parameter.ParameterName);
        Assert.Equal("", parameter.SourceColumn);

        parameter.ParameterName = null;
        Assert.Equal("", parameter.ParameterName);
        parameter.SourceColumn = null;
        Assert.Equal("", parameter.SourceColumn);
        parameter.DbType = DbType.Int32;
        parameter.ResetDbType();
        Assert.Equal(DbType.Object, parameter.DbType);

        parameter.Direction = ParameterDirection.Input;
        Assert.Throws<FrostlakeException>(() => parameter.Direction = ParameterDirection.Output);
    }

    [Fact]
    public void CollectionAddsFindsAndRemoves()
    {
        var parameters = new FrostlakeParameterCollection();
        var id = parameters.AddWithValue("id", 1);
        parameters.AddWithValue("name", "Ada");
        Assert.Equal(2, parameters.Count);
        Assert.True(parameters.Contains("id"));
        Assert.True(parameters.Contains("ID"));
        Assert.False(parameters.Contains("nope"));
        Assert.Equal(0, parameters.IndexOf("id"));
        Assert.Equal(-1, parameters.IndexOf("nope"));
        Assert.True(parameters.Contains(id));
        Assert.Equal(0, parameters.IndexOf(id));

        parameters.RemoveAt("name");
        Assert.Equal(1, parameters.Count);
        parameters.Remove(id);
        Assert.Equal(0, parameters.Count);

        parameters.Add(new FrostlakeParameter("a", 1));
        parameters.Insert(0, new FrostlakeParameter("b", 2));
        Assert.Equal("b", ((DbParameter)parameters[0]).ParameterName);
        parameters.RemoveAt(0);
        Assert.Equal("a", ((DbParameter)parameters[0]).ParameterName);

        parameters.AddRange(new object[] { new FrostlakeParameter("c", 3) });
        Assert.Equal(2, parameters.Count);

        var target = new FrostlakeParameter[2];
        parameters.CopyTo(target, 0);
        Assert.Equal("a", target[0].ParameterName);
        Assert.NotNull(parameters.SyncRoot);

        var seen = 0;
        foreach (var _ in parameters)
        {
            seen++;
        }
        Assert.Equal(2, seen);

        parameters.Clear();
        Assert.Equal(0, parameters.Count);
    }

    [Fact]
    public void CollectionRejectsForeignParameterTypes()
    {
        var parameters = new FrostlakeParameterCollection();
        Assert.Throws<FrostlakeException>(() => parameters.Add("not a parameter"));
    }

    [Fact]
    public void IndexerByNameReadsAndWrites()
    {
        var parameters = new FrostlakeParameterCollection();
        parameters.AddWithValue("id", 1);
        Assert.Equal(1, ((DbParameter)parameters["id"]).Value);
        Assert.Throws<FrostlakeException>(() => parameters["missing"]);
        parameters["id"] = new FrostlakeParameter("id", 2);
        Assert.Equal(2, ((DbParameter)parameters["id"]).Value);
        parameters["fresh"] = new FrostlakeParameter("fresh", 3);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BindsStripPrefixesAndKeepOrder()
    {
        var parameters = new FrostlakeParameterCollection();
        parameters.AddWithValue("@id", 1);
        parameters.AddWithValue(":name", "Ada");
        parameters.AddWithValue("", 3);
        var binds = parameters.Binds();
        Assert.Equal("id", binds[0].Name);
        Assert.Equal("name", binds[1].Name);
        Assert.Null(binds[2].Name);
        Assert.Equal(3, binds[2].Value);
    }

    [Fact]
    public void FactoryBuildsEveryPiece()
    {
        var factory = FrostlakeProviderFactory.Instance;
        Assert.IsType<FrostlakeConnection>(factory.CreateConnection());
        Assert.IsType<FrostlakeCommand>(factory.CreateCommand());
        Assert.IsType<FrostlakeParameter>(factory.CreateParameter());
        Assert.NotNull(factory.CreateConnectionStringBuilder());
    }

    [Fact]
    public void FactoryIsUsableThroughDbProviderFactories()
    {
        DbProviderFactories.UnregisterFactory("Frostlake.Data.Test");
        DbProviderFactories.RegisterFactory("Frostlake.Data.Test", FrostlakeProviderFactory.Instance);
        var factory = DbProviderFactories.GetFactory("Frostlake.Data.Test");
        using var connection = factory.CreateConnection();
        Assert.IsType<FrostlakeConnection>(connection);
    }

    [Fact]
    public void CommandGuardsItsInputs()
    {
        using var command = new FrostlakeCommand();
        Assert.Equal(CommandType.Text, command.CommandType);
        command.CommandType = CommandType.Text;
        Assert.Throws<FrostlakeException>(() => command.CommandType = CommandType.StoredProcedure);

        Assert.Equal(0, command.CommandTimeout);
        command.CommandTimeout = 30;
        Assert.Equal(30, command.CommandTimeout);
        Assert.Throws<FrostlakeException>(() => command.CommandTimeout = -1);

        command.CommandText = null;
        Assert.Equal("", command.CommandText);
        Assert.Throws<FrostlakeException>(() => command.ExecuteNonQuery());

        command.CommandText = "SELECT 1";
        Assert.Throws<FrostlakeException>(() => command.ExecuteNonQuery());

        command.Cancel();
        command.Prepare();
        Assert.IsType<FrostlakeParameter>(command.CreateParameter());
    }

    [Fact]
    public void ExceptionsAreDbExceptions()
    {
        var error = new FrostlakeException("boom", new InvalidOperationException("inner"));
        Assert.IsAssignableFrom<DbException>(error);
        Assert.Equal("boom", error.Message);
        Assert.NotNull(error.InnerException);
    }
}
