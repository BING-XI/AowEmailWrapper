using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MimeKit;

namespace AowEmailWrapper.SmokeTests
{
    /// <summary>
    /// A POP3 server on this PC holding one mailbox, for the Wrapper under test to check. It accepts any
    /// sign-in, answers the commands MailKit uses (CAPA, USER, PASS, STAT, LIST, UIDL, RETR, DELE, QUIT)
    /// and counts finished sessions, so a test can wait for a mail check to end.
    /// </summary>
    public sealed class FakePop3Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<byte[]> _messages = new List<byte[]>();
        private readonly object _lock = new object();
        private readonly Thread _thread;
        private int _sessions;
        private readonly List<string> _log = new List<string>();

        /// <summary>Every command received and every first line answered, for failure messages.</summary>
        public string Log { get { lock (_log) { return string.Join(Environment.NewLine, _log); } } }
        private volatile bool _stopping;

        public int Port { get; }

        /// <summary>Mail checks that ran to QUIT.</summary>
        public int Sessions { get { return Volatile.Read(ref _sessions); } }

        public FakePop3Server()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Accept) { IsBackground = true };
            _thread.Start();
        }

        public static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Add(MimeMessage message)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                message.WriteTo(FormatOptions.Default, stream);
                lock (_lock)
                {
                    _messages.Add(stream.ToArray());
                }
            }
        }

        private void Accept()
        {
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.ASCII))
            {
                HashSet<int> deleted = new HashSet<int>();
                void Send(string line)
                {
                    lock (_log) { _log.Add("S: " + line); }
                    byte[] bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
                void SendData(byte[] data)
                {
                    //Dot-stuffing, then the terminating line
                    string text = Encoding.ASCII.GetString(data).Replace("\r\n.", "\r\n..");
                    //An empty list is the terminating line alone; a blank line before it would be a malformed entry
                    if (text.Length > 0 && !text.EndsWith("\r\n", StringComparison.Ordinal))
                    {
                        text += "\r\n";
                    }
                    byte[] bytes = Encoding.ASCII.GetBytes(text + ".\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                }

                List<byte[]> messages;
                lock (_lock)
                {
                    messages = _messages.ToList();
                }
                IEnumerable<int> Live() { return Enumerable.Range(0, messages.Count).Where(i => !deleted.Contains(i)); }

                Send("+OK fake POP3 ready");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (_log) { _log.Add("C: " + (line.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? "PASS ***" : line)); }
                    string[] parts = line.Split(' ');
                    switch (parts[0].ToUpperInvariant())
                    {
                        case "CAPA":
                            Send("+OK");
                            SendData(Encoding.ASCII.GetBytes("USER\r\nUIDL\r\n"));
                            break;
                        case "USER":
                        case "PASS":
                        case "NOOP":
                        case "RSET":
                            Send("+OK");
                            break;
                        case "STAT":
                            Send($"+OK {Live().Count()} {Live().Sum(i => messages[i].Length)}");
                            break;
                        case "LIST":
                            Send("+OK");
                            SendData(Encoding.ASCII.GetBytes(string.Concat(Live().Select(i => $"{i + 1} {messages[i].Length}\r\n"))));
                            break;
                        case "UIDL":
                            Send("+OK");
                            SendData(Encoding.ASCII.GetBytes(string.Concat(Live().Select(i => $"{i + 1} turn-{i + 1}\r\n"))));
                            break;
                        case "RETR":
                            int index = int.Parse(parts[1]) - 1;
                            Send($"+OK {messages[index].Length} octets");
                            SendData(messages[index]);
                            break;
                        case "DELE":
                            deleted.Add(int.Parse(parts[1]) - 1);
                            Send("+OK");
                            break;
                        case "QUIT":
                            Send("+OK bye");
                            Interlocked.Increment(ref _sessions);
                            return;
                        default:
                            Send("-ERR unknown command");
                            break;
                    }
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _listener.Stop();
        }
    }
}
