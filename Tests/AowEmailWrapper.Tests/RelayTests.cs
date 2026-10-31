using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using AowEmailWrapper.ASG;
using AowEmailWrapper.CSES;
using AowEmailWrapper.Helpers;
using EricDaugherty.CSES.Net;
using EricDaugherty.CSES.SmtpServer;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// Sends a game email through the wrapper's local SMTP listener the way the games do,
    /// with a standard SMTP client, and keeps what the spool produced for the tests below.
    /// </summary>
    public sealed class RelayFixture : IDisposable
    {
        public const string AttachmentName = "Test Game (Dave, Fred).asg";
        public const string BodyText = "Your turn!\r\nLine starting with a dot:\r\n.hidden dot line\r\n";

        public readonly byte[] Asg = new byte[4096];
        public MimeMessage Spooled;
        public Exception ServerError;

        private readonly SimpleServer _server;
        private readonly ManualResetEvent _done = new ManualResetEvent(false);

        public RelayFixture()
        {
            new Random(1).NextBytes(Asg);

            int port = FreePort();
            _server = new SimpleServer(port, ProcessConnection);
            Thread thread = new Thread(_server.Start);
            thread.IsBackground = true;
            thread.Start();
            Thread.Sleep(300);

            using (SmtpClient client = new SmtpClient("127.0.0.1", port))
            {
                MailMessage mail = new MailMessage("player1@example.com", "player2@example.com", "PBEM turn 5", BodyText);
                mail.Attachments.Add(new Attachment(new MemoryStream(Asg), AttachmentName));
                client.Send(mail);
            }

            if (!_done.WaitOne(10000))
            {
                throw new TimeoutException("The local SMTP listener never spooled the message.");
            }
        }

        private void ProcessConnection(Socket socket)
        {
            try
            {
                SmtpSpool spool = new SmtpSpool();
                using (SMTPProcessor processor = new SMTPProcessor("test.local", new AcceptAllRecipients(), spool))
                {
                    processor.ProcessConnection(socket);
                }
                Spooled = spool.SpooledEmail;
            }
            catch (Exception ex)
            {
                ServerError = ex;
            }
            finally
            {
                _done.Set();
            }
        }

        public static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _server.Stop();
        }
    }

    public class RelayTests : IClassFixture<RelayFixture>
    {
        private readonly RelayFixture _f;

        public RelayTests(RelayFixture fixture)
        {
            _f = fixture;
        }

        [Fact]
        public void ServerSpoolsTheMessageWithoutError()
        {
            Assert.Null(_f.ServerError);
            Assert.NotNull(_f.Spooled);
        }

        [Fact]
        public void AttachmentSurvivesWithNameAndBytesIntact()
        {
            var attachments = MailHelper.GetAttachments(_f.Spooled);
            Assert.Single(attachments);
            Assert.Equal(RelayFixture.AttachmentName, attachments[0].FileName);
            Assert.Equal(_f.Asg, MailHelper.GetAttachmentBytes(attachments[0]));
        }

        [Fact]
        public void BodyIsKeptDotStuffingUndoneAndFooterAppended()
        {
            string text = MailHelper.GetPlainText(_f.Spooled);
            Assert.Contains("Your turn!", text);
            Assert.Contains(".hidden dot line", text);
            Assert.Contains("Autosent with the Age of Wonders Email Wrapper", text);
        }

        [Fact]
        public void AddressesAndMessageIdAreSet()
        {
            Assert.Equal("player2@example.com", MailHelper.GetFirstToAddress(_f.Spooled));
            Assert.Equal("player1@example.com", MailHelper.GetFromAddress(_f.Spooled));
            Assert.False(string.IsNullOrEmpty(_f.Spooled.MessageId));
        }

        [Fact]
        public void TheSpooledTurnDoesNotNameThisComputer()
        {
            //The local server's Received line named the game's computer and the server, and MimeKit makes its
            //Message-IDs on the computer's name
            string headers = string.Join("\n", _f.Spooled.Headers.Select(header => header.ToString()));
            Assert.DoesNotContain(_f.Spooled.Headers, header => header.Id == HeaderId.Received);
            Assert.DoesNotContain("test.local", headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, headers, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("@example.com", _f.Spooled.MessageId);
        }

        [Fact]
        public void AsgFileInfoReadsTheAttachment()
        {
            using (ASGFileInfo info = new ASGFileInfo(MailHelper.GetFirstAttachment(_f.Spooled)))
            {
                Assert.Equal(_f.Asg.Length, info.Length);
                Assert.Equal(RelayFixture.AttachmentName, info.FileName);
                //Random bytes are not a real save, so the attachment name is used
                Assert.Equal(RelayFixture.AttachmentName, info.FileNameTrue);
            }
        }

        [Fact]
        public void ResendFileRoundTripKeepsEverything()
        {
            MimeMessage message = _f.Spooled;
            message.Prepare(EncodingConstraint.SevenBit);
            string eml = Path.Combine(TestEnvironment.TestAppData, "resend-roundtrip.eml");
            message.WriteTo(eml);

            MimeMessage loaded = MimeMessage.Load(eml);
            var attachments = MailHelper.GetAttachments(loaded);
            Assert.Single(attachments);
            Assert.Equal(_f.Asg, MailHelper.GetAttachmentBytes(attachments[0]));
            Assert.Contains(".hidden dot line", MailHelper.GetPlainText(loaded));
            Assert.Equal(message.MessageId, loaded.MessageId);
        }

        [Fact]
        public void SetPlainTextRebuildsAnAttachmentOnlyMessage()
        {
            MimeMessage bare = new MimeMessage();
            MimePart part = new MimePart("application", "octet-stream");
            part.FileName = "x.asg";
            part.Content = new MimeContent(new MemoryStream(_f.Asg));
            bare.Body = part;

            MailHelper.SetPlainText(bare, "hello");

            Assert.Equal("hello", MailHelper.GetPlainText(bare));
            var attachments = MailHelper.GetAttachments(bare);
            Assert.Single(attachments);
            Assert.Equal(_f.Asg, MailHelper.GetAttachmentBytes(attachments[0]));
        }
    }
}
