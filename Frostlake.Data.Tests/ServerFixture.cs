using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Frostlake.Data.Tests;

/// <summary>
/// Boots a real <c>DatabaseHttpServer</c> from <c>FROSTLAKE_CLASSPATH</c> on a free port.
/// When the variable is unset, <see cref="ConnectionString"/> stays null and the
/// integration tests skip themselves.
/// </summary>
public sealed class ServerFixture : IDisposable
{
    public string? ConnectionString { get; }

    private readonly Process? _server;

    public ServerFixture()
    {
        var classpath = Environment.GetEnvironmentVariable("FROSTLAKE_CLASSPATH");
        if (string.IsNullOrEmpty(classpath))
        {
            return;
        }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var java = javaHome is null ? "java" : Path.Combine(javaHome, "bin", "java");
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
        _server = Process.Start(startInfo)!;
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 100; attempt++)
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
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("Frostlake server did not become healthy");
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
