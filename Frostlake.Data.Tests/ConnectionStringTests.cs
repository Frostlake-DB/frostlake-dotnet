using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>Connection-string parsing, both spellings. No engine required.</summary>
public class ConnectionStringTests
{
    [Theory]
    [InlineData("frostlake://localhost:18082/MY_DB?schema=PUBLIC", "http://localhost:18082", "MY_DB", "PUBLIC")]
    [InlineData("frostlake://localhost/MY_DB", "http://localhost:18082", "MY_DB", null)]
    [InlineData("frostlake://127.0.0.1:1234", "http://127.0.0.1:1234", null, null)]
    [InlineData("http://localhost:8080/DB", "http://localhost:8080", "DB", null)]
    [InlineData("frostlake://h:1/DB?schema=a%20b", "http://h:1", "DB", "a b")]
    [InlineData("frostlake://h:1/DB?other=1&schema=S", "http://h:1", "DB", "S")]
    public void ParsesDsnUrls(string dsn, string baseUrl, string? database, string? schema)
    {
        var options = FrostlakeConnectionOptions.Parse(dsn);
        Assert.Equal(baseUrl, options.BaseUrl);
        Assert.Equal(database, options.Database);
        Assert.Equal(schema, options.Schema);
    }

    [Theory]
    [InlineData("Host=localhost;Port=18082;Database=MY_DB;Schema=PUBLIC", "http://localhost:18082", "MY_DB", "PUBLIC")]
    [InlineData("Host=h", "http://h:18082", null, null)]
    [InlineData("Server=h;Port=99", "http://h:99", null, null)]
    [InlineData("Database=D", "http://localhost:18082", "D", null)]
    public void ParsesKeyValueStrings(string text, string baseUrl, string? database, string? schema)
    {
        var options = FrostlakeConnectionOptions.Parse(text);
        Assert.Equal(baseUrl, options.BaseUrl);
        Assert.Equal(database, options.Database);
        Assert.Equal(schema, options.Schema);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Host=localhost;Port=abc")]
    [InlineData("Host=localhost;Port=0")]
    [InlineData("Host=localhost;Port=70000")]
    [InlineData("notaurl")]
    [InlineData("ftp://host/db")]
    [InlineData("frostlake://host:99999/db")]
    [InlineData("frostlake://host/db/extra")]
    public void BadInputAlwaysRaisesFrostlakeException(string text)
    {
        // Every failure path must surface as the driver's own exception type.
        Assert.Throws<FrostlakeException>(() => FrostlakeConnectionOptions.Parse(text));
    }

    [Fact]
    public void ConnectionSurfacesParseFailuresFromOpen()
    {
        using var connection = new FrostlakeConnection("Host=localhost;Port=abc");
        Assert.Throws<FrostlakeException>(() => connection.Open());
    }

    [Fact]
    public void ClosedConnectionReportsEmptyMetadata()
    {
        using var connection = new FrostlakeConnection();
        Assert.Equal("", connection.Database);
        Assert.Equal("", connection.DataSource);
        Assert.Equal("", connection.ServerVersion);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Throws<FrostlakeException>(() => connection.Open());
    }

    [Fact]
    public void ConnectionStringIsAssignable()
    {
        var connection = new FrostlakeConnection { ConnectionString = "frostlake://h:1/DB" };
        Assert.Equal("frostlake://h:1/DB", connection.ConnectionString);
        connection.ConnectionString = null;
        Assert.Equal("", connection.ConnectionString);
    }

    [Fact]
    public void UnreachableServerFailsWithADriverException()
    {
        using var connection = new FrostlakeConnection("frostlake://127.0.0.1:1/DB");
        var error = Assert.Throws<FrostlakeException>(() => connection.Open());
        Assert.Contains("cannot reach", error.Message);
    }

    [Fact]
    public async Task OpenAsyncSurfacesUnreachableServersAsDriverErrors()
    {
        await using var connection = new FrostlakeConnection("frostlake://127.0.0.1:1/DB");
        var error = await Assert.ThrowsAsync<FrostlakeException>(() => connection.OpenAsync());
        Assert.Contains("cannot reach", error.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task OpenAsyncRejectsABadConnectionString()
    {
        await using var connection = new FrostlakeConnection("Host=localhost;Port=abc");
        await Assert.ThrowsAsync<FrostlakeException>(() => connection.OpenAsync());
    }

    [Fact]
    public async Task AsyncStatementsOnAClosedConnectionFail()
    {
        await using var connection = new FrostlakeConnection("frostlake://127.0.0.1:1/DB");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await Assert.ThrowsAsync<FrostlakeException>(() => command.ExecuteScalarAsync());
        await Assert.ThrowsAsync<FrostlakeException>(() => command.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<FrostlakeException>(() => command.ExecuteReaderAsync());
    }

    [Fact]
    public void ConnectionStringCannotChangeWhileOpen()
    {
        using var connection = new FrostlakeConnection("frostlake://127.0.0.1:1/DB");
        // Closed: assignment is allowed.
        connection.ConnectionString = "frostlake://127.0.0.1:2/DB";
        Assert.Equal("frostlake://127.0.0.1:2/DB", connection.ConnectionString);
    }

    [Fact]
    public void StatementsOnAClosedConnectionFail()
    {
        using var connection = new FrostlakeConnection("frostlake://127.0.0.1:1/DB");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Throws<FrostlakeException>(() => command.ExecuteScalar());
    }
}
