using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RumbleReShaded
{
    // The socket half of the live link (LiveLink.cs is the half that touches the game). One client
    // at a time, on this PC only, sending one JSON message per line. A thread waits on the socket;
    // nothing here runs game code, so messages queue up for the main thread to take in TryRead.
    //
    // WHY A HANDSHAKE ON A LOCALHOST PORT: any web page can make a browser send bytes to 127.0.0.1.
    // Its first line is "POST / HTTP/1.1", though, not the hello below, so it is dropped before
    // anything it sends is looked at.
    internal sealed class LiveLinkServer : IDisposable
    {
        public const int DefaultPort = 47810;
        public const string Hello = "rrs-live-link";
        public const int ProtocolVersion = 1;

        private const int LongestLineBytes = 1 << 20;
        private const int HandshakeMilliseconds = 2000;

        private readonly TcpListener listener;
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private volatile TcpClient client;
        private volatile bool stopped;

        /// <summary>The port really listened on: asking for 0 picks a free one.</summary>
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        /// <summary>Starts listening. Throws if the port is taken.</summary>
        public LiveLinkServer(int port = DefaultPort)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            new Thread(Listen) { IsBackground = true, Name = "RRS live link" }.Start();
        }

        /// <summary>The next message for the main thread. A null line means the client left.</summary>
        public bool TryRead(out string line) => incoming.TryDequeue(out line);

        /// <summary>Sends one line to the client, if there is one. A client that has gone is dropped by the reader.</summary>
        public void Send(string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                client?.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        private void Listen()
        {
            while (!stopped)
            {
                try
                {
                    using var accepted = listener.AcceptTcpClient();
                    Serve(accepted);
                }
                catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
                finally
                {
                    if (client != null) incoming.Enqueue(null);
                    client = null;
                }
            }
        }

        private void Serve(TcpClient accepted)
        {
            accepted.NoDelay = true;
            accepted.ReceiveTimeout = HandshakeMilliseconds;
            var stream = accepted.GetStream();
            var line = new MemoryStream();
            var buffer = new byte[8192];
            var greeted = false;

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') { line.WriteByte(buffer[i]); continue; }

                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                    line.SetLength(0);
                    if (!greeted)
                    {
                        if (!IsHello(text)) return;
                        greeted = true;
                        accepted.ReceiveTimeout = 0;
                        client = accepted;
                    }
                    incoming.Enqueue(text);
                }
                if (line.Length > LongestLineBytes) return;
            }
        }

        private static bool IsHello(string text)
        {
            try
            {
                using var message = JsonDocument.Parse(text);
                return message.RootElement.ValueKind == JsonValueKind.Object
                    && message.RootElement.TryGetProperty("hello", out var hello)
                    && hello.ValueKind == JsonValueKind.String && hello.GetString() == Hello;
            }
            catch (JsonException) { return false; }
        }

        public void Dispose()
        {
            stopped = true;
            listener.Stop();
            client?.Close();
        }
    }
}
