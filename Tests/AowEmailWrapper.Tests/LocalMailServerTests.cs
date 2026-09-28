using System.Net;
using System.Net.Sockets;
using EricDaugherty.CSES.Net;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// The local mail server binds its port in Listen, on the caller's thread. Bound on the thread that runs
    /// the server, a port already in use threw there, where nothing could catch it, and ended the Wrapper.
    /// </summary>
    public class LocalMailServerTests
    {
        [Fact]
        public void A_busy_port_throws_from_Listen_where_the_caller_can_catch_it()
        {
            TcpListener squatter = new TcpListener(IPAddress.Loopback, 0);
            squatter.Start();
            try
            {
                int port = ((IPEndPoint)squatter.LocalEndpoint).Port;
                SimpleServer server = new SimpleServer(port, socket => socket.Close());

                Assert.Throws<SocketException>(() => server.Listen());
                Assert.False(server.IsRunning);
            }
            finally
            {
                squatter.Stop();
            }
        }

        [Fact]
        public void A_free_port_is_bound_by_Listen_before_the_server_thread_starts()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            SimpleServer server = new SimpleServer(port, socket => socket.Close());
            server.Listen();
            try
            {
                Assert.True(server.IsRunning);
                TcpListener second = new TcpListener(IPAddress.Loopback, port);
                Assert.Throws<SocketException>(() => second.Start());
            }
            finally
            {
                server.Stop();
            }
        }
    }
}
