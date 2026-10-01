using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Frostlake.Data.Tests;

/// <summary>
/// Closing against real sockets rather than a scripted transport: a server that drops the release
/// request's connection without a word, and one that never answers it. No engine required.
/// </summary>
public class CloseTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingNeverRaisesWhenTheReleaseIsCutOffOrNeverAnswered(bool hang)
    {
        using var server = new SocketEngine(hang);
        var connection = new FrostlakeConnection($"frostlake://127.0.0.1:{server.Port}/APP") { ReleaseDeadline = TimeSpan.FromMilliseconds(500) };
        connection.Open();
        var clock = Stopwatch.StartNew();
        connection.Close();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Close took {clock.Elapsed}");
        Assert.Equal(ConnectionState.Closed, connection.State);
        if (hang)
        {
            Assert.Equal(1, server.Deletes);
        }
        else
        {
            // The connection sends one DELETE. .NET's HTTP stack itself re-sends a request without a
            // body whose connection closed before any answer came, up to three times, all of it
            // inside the release deadline.
            Assert.InRange(server.Deletes, 1, 4);
        }
    }

    /// <summary>
    /// Answers the health check and every statement like an engine that reports <c>newSession</c>,
    /// and cuts a <c>DELETE</c> off: it closes the socket, or holds it open and says nothing.
    /// </summary>
    private sealed class SocketEngine : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _held = new();
        private readonly bool _hang;
        private int _deletes;

        public SocketEngine(bool hang)
        {
            _hang = hang;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true }.Start();
        }

        public int Port { get; }

        public int Deletes => Volatile.Read(ref _deletes);

        private void Serve()
        {
            try
            {
                while (true)
                {
                    Handle(_listener.AcceptTcpClient());
                }
            }
            catch (SocketException)
            {
                // stopped
            }
            catch (ObjectDisposedException)
            {
                // stopped
            }
        }

        private void Handle(TcpClient client)
        {
            var stream = client.GetStream();
            var received = new List<byte>();
            var buffer = new byte[8192];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    client.Dispose();
                    return;
                }
                received.AddRange(buffer[..read]);
                headerEnd = Encoding.ASCII.GetString(received.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            var head = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd).Split("\r\n");
            var length = 0;
            foreach (var line in head)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(line[15..].Trim());
                }
            }
            while (received.Count < headerEnd + 4 + length)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                received.AddRange(buffer[..read]);
            }
            if (head[0].StartsWith("DELETE ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _deletes);
                if (_hang)
                {
                    _held.Add(client);
                }
                else
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Dispose();
                }
                return;
            }
            var body = head[0].StartsWith("GET /api/health", StringComparison.Ordinal)
                ? """{"status":"healthy","activeSessions":0}"""
                : """{"success":true,"sessionId":"k1","newSession":true,"resultSets":[]}""";
            var reply = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            stream.Write(reply, 0, reply.Length);
            client.Dispose();
        }

        public void Dispose()
        {
            _listener.Stop();
            foreach (var client in _held)
            {
                client.Dispose();
            }
        }
    }
}
