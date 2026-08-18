using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Frostlake.Data.Tests;

/// <summary>
/// Boots a real <c>DatabaseHttpServer</c> from <c>FROSTLAKE_CLASSPATH</c> on a free port.
/// When the variable is unset, <see cref="ConnectionString"/> stays null and the
/// engine-backed tests report as skipped.
/// <para>
/// The server's own output is captured and replayed in the failure message: a server that
/// never comes up has almost always said why (a JDK too old for the engine's class files,
/// or a classpath carrying the engine jar without its dependencies).
/// </para>
/// </summary>
public sealed class ServerFixture : IDisposable
{
    private const int HealthAttempts = 100;
    private const int HealthIntervalMs = 200;

    public string? ConnectionString { get; }

    private readonly Process? _server;
    private readonly StringBuilder _output = new();

    public ServerFixture()
    {
        var classpath = Environment.GetEnvironmentVariable("FROSTLAKE_CLASSPATH");
        if (string.IsNullOrEmpty(classpath))
        {
            return;
        }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = javaHome is null ? "java" : Path.Combine(javaHome, "bin", "java");
        if (javaHome is not null && !File.Exists(java))
        {
            throw new InvalidOperationException(
                $"JAVA_HOME is {javaHome} but there is no java at {java}");
        }
        var port = FreePort();
        var startInfo = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(classpath);
        startInfo.ArgumentList.Add("dev.frostlake.http.DatabaseHttpServer");
        startInfo.ArgumentList.Add(port.ToString());
        try
        {
            _server = Process.Start(startInfo)!;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"could not start {java}: {e.Message}", e);
        }
        _server.OutputDataReceived += (_, e) => Capture(e.Data);
        _server.ErrorDataReceived += (_, e) => Capture(e.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var http = new HttpClient();
        for (var attempt = 0; attempt < HealthAttempts; attempt++)
        {
            try
            {
                using var response = http.Send(
                    new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health"));
                if (response.IsSuccessStatusCode)
                {
                    ConnectionString = $"frostlake://127.0.0.1:{port}";
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // not up yet
            }
            if (_server.HasExited)
            {
                // No point waiting out the rest of the budget - it is never coming back.
                throw Failed($"the server exited with code {_server.ExitCode}", java, classpath);
            }
            Thread.Sleep(HealthIntervalMs);
        }
        throw Failed(
            $"the server did not answer /api/health within {HealthAttempts * HealthIntervalMs / 1000}s",
            java,
            classpath);
    }

    private void Capture(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (_output)
        {
            _output.AppendLine(line);
        }
    }

    private InvalidOperationException Failed(string reason, string java, string classpath)
    {
        string captured;
        lock (_output)
        {
            captured = _output.ToString().TrimEnd();
        }
        var entries = classpath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var detail = new StringBuilder();
        detail.AppendLine($"Frostlake server did not become healthy: {reason}.");
        detail.AppendLine($"  java:      {java}");
        detail.AppendLine($"  classpath: {entries.Length} entr{(entries.Length == 1 ? "y" : "ies")}");
        if (entries.Length == 1)
        {
            detail.AppendLine(
                "             FROSTLAKE_CLASSPATH needs the engine jar AND its dependencies, not the jar alone.");
        }
        detail.AppendLine(captured.Length > 0
            ? $"  server output:{Environment.NewLine}{Indent(captured)}"
            : "  server output: (none - if this is UnsupportedClassVersionError territory, check JAVA_HOME is a JDK 17+)");
        return new InvalidOperationException(detail.ToString().TrimEnd());
    }

    private static string Indent(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = "    " + lines[i].TrimEnd('\r');
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        if (_server is not null && !_server.HasExited)
        {
            _server.Kill(entireProcessTree: true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
