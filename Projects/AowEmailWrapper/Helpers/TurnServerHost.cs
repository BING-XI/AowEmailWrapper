using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AowEmailWrapper.Classes;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// The turn server a player can host: a small HTTP server on this machine only, which Tailscale
    /// Funnel publishes. Other Wrappers post a record when they receive or send a turn and read a
    /// game's records to see who has it.
    ///
    ///   POST …/records          one TurnRecord as JSON; 204 when stored
    ///   GET  …/records?game=X   the records of game X as a JSON list
    ///   GET  anything else      a line saying what this is, for checking the address works
    ///
    /// Routes match on the end of the path, so the server answers the same whether or not the proxy
    /// in front passes on the path it is published under. Whatever arrives is held to fixed limits,
    /// since the published address is reachable by anyone.
    /// </summary>
    public class TurnServerHost : IDisposable
    {
        public const int DefaultPort = 49253;
        public const string RecordsPath = "/records";
        public const string Banner = "Age of Wonders Email Wrapper turn server";
        public const int MaxHeaderBytes = 8192;
        public const int MaxBodyBytes = 4096;
        public const int RequestsPerMinute = 120;
        private const int ReadTimeoutMilliseconds = 10000;

        private readonly TurnServerStore _store;
        private readonly Dictionary<string, Queue<DateTime>> _requests = new Dictionary<string, Queue<DateTime>>();
        private TcpListener _listener;
        private CancellationTokenSource _stop;

        public TurnServerHost(TurnServerStore store)
        {
            _store = store;
        }

        public bool IsRunning
        {
            get { return _listener != null; }
        }

        /// <summary>The port listened on; the one the system chose when started on port 0.</summary>
        public int Port { get; private set; }

        public string LocalAddress
        {
            get { return string.Format("http://127.0.0.1:{0}", Port); }
        }

        /// <summary>Starts listening on this machine only. Throws when the port is taken.</summary>
        public void Start(int port)
        {
            Stop();

            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _listener = listener;
            _stop = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;

            CancellationToken token = _stop.Token;
            Task.Run(() => AcceptLoop(listener, token));
            Trace.TraceInformation("Turn server listening on {0}", LocalAddress);
        }

        public void Stop()
        {
            if (_listener != null)
            {
                _stop.Cancel();
                _listener.Stop();
                _listener = null;
                Trace.TraceInformation("Turn server stopped");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task AcceptLoop(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (Exception)
                {
                    //Stopped
                    return;
                }

                _ = Task.Run(() => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = ReadTimeoutMilliseconds;
                    client.SendTimeout = ReadTimeoutMilliseconds;
                    NetworkStream stream = client.GetStream();
                    Response response = Respond(stream);
                    Write(stream, response);
                    LingerClose(client.Client, stream);
                }
                catch (Exception ex)
                {
                    //A client that hangs up or stalls only loses its own request
                    Trace.TraceInformation("Turn server request dropped: {0}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Closing a socket with request bytes still unread resets the connection, and the client may then
        /// lose the response, as with a body refused for its size. So the server says it is done
        /// sending and reads off what is left, within limits, before closing.
        /// </summary>
        private static void LingerClose(Socket socket, Stream stream)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                socket.ReceiveTimeout = 2000;
                byte[] discard = new byte[4096];
                int total = 0;
                int read;
                while (total < 64 * 1024 && (read = stream.Read(discard, 0, discard.Length)) > 0)
                {
                    total += read;
                }
            }
            catch (Exception)
            {
                //The client went first
            }
        }

        #region Requests

        private class Response
        {
            public int Status;
            public string Reason;
            public string ContentType = "text/plain; charset=utf-8";
            public string Body = string.Empty;

            public Response(int status, string reason, string body)
            {
                Status = status;
                Reason = reason;
                Body = body ?? string.Empty;
            }
        }

        private Response Respond(Stream stream)
        {
            byte[] buffer = new byte[MaxHeaderBytes + MaxBodyBytes];
            int length = 0;
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                if (length >= MaxHeaderBytes)
                {
                    return new Response(431, "Request Header Fields Too Large", "Headers too large");
                }
                int read = stream.Read(buffer, length, MaxHeaderBytes - length);
                if (read <= 0)
                {
                    throw new IOException("connection closed before the request was complete");
                }
                length += read;
                headerEnd = IndexOfHeaderEnd(buffer, length);
            }

            string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3)
            {
                return new Response(400, "Bad Request", "Bad request line");
            }
            string method = requestLine[0];
            string target = requestLine[1];

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
            }

            if (!Allow(ClientKey(headers)))
            {
                return new Response(429, "Too Many Requests", "Too many requests");
            }

            if (headers.ContainsKey("Transfer-Encoding"))
            {
                return new Response(411, "Length Required", "Send a Content-Length");
            }

            int contentLength = 0;
            string lengthValue;
            if (headers.TryGetValue("Content-Length", out lengthValue) && (!int.TryParse(lengthValue, out contentLength) || contentLength < 0))
            {
                return new Response(400, "Bad Request", "Bad Content-Length");
            }
            if (contentLength > MaxBodyBytes)
            {
                return new Response(413, "Content Too Large", "Body too large");
            }

            int bodyStart = headerEnd + 4;
            while (length - bodyStart < contentLength)
            {
                int read = stream.Read(buffer, length, bodyStart + contentLength - length);
                if (read <= 0)
                {
                    throw new IOException("connection closed before the body was complete");
                }
                length += read;
            }
            string body = Encoding.UTF8.GetString(buffer, bodyStart, contentLength);

            return Route(method, target, body);
        }

        private Response Route(string method, string target, string body)
        {
            int question = target.IndexOf('?');
            string path = (question >= 0 ? target.Substring(0, question) : target).TrimEnd('/');
            string query = question >= 0 ? target.Substring(question + 1) : string.Empty;

            if (!path.EndsWith(RecordsPath, StringComparison.OrdinalIgnoreCase))
            {
                return method == "GET" ? new Response(200, "OK", Banner) : new Response(404, "Not Found", "Not found");
            }

            if (method == "GET")
            {
                string game = QueryValue(query, "game");
                if (string.IsNullOrWhiteSpace(game))
                {
                    return new Response(400, "Bad Request", "Name the game: ?game=");
                }
                return new Response(200, "OK", TurnRecord.ToJson(_store.ForGame(game))) { ContentType = "application/json; charset=utf-8" };
            }

            if (method == "POST")
            {
                TurnRecord record = TurnRecord.FromJson(body);
                return _store.Put(record) ? new Response(204, "No Content", null) : new Response(400, "Bad Request", "Not a valid turn record");
            }

            return new Response(405, "Method Not Allowed", "GET or POST");
        }

        private static void Write(Stream stream, Response response)
        {
            byte[] body = Encoding.UTF8.GetBytes(response.Body);
            StringBuilder head = new StringBuilder();
            head.AppendFormat("HTTP/1.1 {0} {1}\r\n", response.Status, response.Reason);
            if (response.Status != 204)
            {
                head.AppendFormat("Content-Type: {0}\r\n", response.ContentType);
                head.AppendFormat("Content-Length: {0}\r\n", body.Length);
            }
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (response.Status != 204)
            {
                stream.Write(body, 0, body.Length);
            }
            stream.Flush();
        }

        private static int IndexOfHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static string QueryValue(string query, string name)
        {
            foreach (string pair in query.Split('&'))
            {
                int equals = pair.IndexOf('=');
                string key = equals >= 0 ? pair.Substring(0, equals) : pair;
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return equals >= 0 ? Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' ')) : string.Empty;
                }
            }
            return null;
        }

        #endregion

        #region Rate limit

        /// <summary>
        /// The client's address as the proxy in front reports it: the proxy appends the address it saw to
        /// X-Forwarded-For, so only the last entry is not the client's own claim. Without the header the
        /// request came from this machine.
        /// </summary>
        private static string ClientKey(Dictionary<string, string> headers)
        {
            string forwarded;
            if (headers.TryGetValue("X-Forwarded-For", out forwarded))
            {
                string last = forwarded.Split(',').Select(entry => entry.Trim()).LastOrDefault(entry => entry.Length > 0);
                if (!string.IsNullOrEmpty(last))
                {
                    return last;
                }
            }
            return "local";
        }

        private bool Allow(string client)
        {
            DateTime now = DateTime.UtcNow;
            lock (_requests)
            {
                foreach (string idle in _requests.Where(pair => pair.Value.Count == 0 || now - pair.Value.Last() > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToList())
                {
                    _requests.Remove(idle);
                }

                Queue<DateTime> times;
                if (!_requests.TryGetValue(client, out times))
                {
                    times = new Queue<DateTime>();
                    _requests[client] = times;
                }
                while (times.Count > 0 && now - times.Peek() > TimeSpan.FromMinutes(1))
                {
                    times.Dequeue();
                }
                if (times.Count >= RequestsPerMinute)
                {
                    return false;
                }
                times.Enqueue(now);
                return true;
            }
        }

        #endregion
    }
}
