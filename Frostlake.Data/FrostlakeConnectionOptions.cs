using System.Data.Common;
using System.Globalization;

namespace Frostlake.Data;

/// <summary>
/// Parsed connection settings. Two spellings are accepted:
/// a DSN URL (<c>frostlake://host:port/DB?schema=S</c>) or an ADO.NET key=value string
/// (<c>Host=localhost;Port=18082;Database=MY_DB;Schema=PUBLIC</c>).
/// </summary>
internal sealed class FrostlakeConnectionOptions
{
    private const int DefaultPort = 18082;

    public string BaseUrl { get; }
    public string? Database { get; }
    public string? Schema { get; }

    private FrostlakeConnectionOptions(string baseUrl, string? database, string? schema)
    {
        BaseUrl = baseUrl;
        Database = database;
        Schema = schema;
    }

    public static FrostlakeConnectionOptions Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new FrostlakeException("connection string is empty");
        }
        return connectionString.Contains("://") ? ParseUrl(connectionString) : ParseKeyValue(connectionString);
    }

    private static FrostlakeConnectionOptions ParseUrl(string dsn)
    {
        Uri uri;
        try
        {
            uri = new Uri(dsn);
        }
        catch (UriFormatException e)
        {
            throw new FrostlakeException($"invalid DSN: {dsn}", e);
        }
        if (uri.Scheme != "frostlake" && uri.Scheme != "http")
        {
            throw new FrostlakeException("DSN must start with frostlake:// or http://");
        }
        var port = uri.Port > 0 ? uri.Port : DefaultPort;
        var database = uri.AbsolutePath.Trim('/');
        if (database.Contains('/'))
        {
            throw new FrostlakeException($"DSN path must name at most one database, got '{database}'");
        }
        string? schema = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals("schema", StringComparison.OrdinalIgnoreCase))
            {
                schema = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return new FrostlakeConnectionOptions(
            $"http://{uri.Host}:{port}",
            database.Length > 0 ? Uri.UnescapeDataString(database) : null,
            schema);
    }

    private static FrostlakeConnectionOptions ParseKeyValue(string connectionString)
    {
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException e)
        {
            throw new FrostlakeException($"invalid connection string: {e.Message}", e);
        }
        var host = TakeString(builder, "Host") ?? TakeString(builder, "Server") ?? "localhost";
        var portText = TakeString(builder, "Port");
        var port = DefaultPort;
        if (portText is not null
            && (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port is <= 0 or > 65535))
        {
            throw new FrostlakeException($"invalid Port '{portText}'");
        }
        return new FrostlakeConnectionOptions(
            $"http://{host}:{port}",
            TakeString(builder, "Database"),
            TakeString(builder, "Schema"));
    }

    private static string? TakeString(DbConnectionStringBuilder builder, string key)
    {
        if (!builder.TryGetValue(key, out var value))
        {
            return null;
        }
        var text = value.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
