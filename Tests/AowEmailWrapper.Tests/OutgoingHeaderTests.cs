using System;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.CSES;
using AowEmailWrapper.Helpers;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// What an email's headers give away. MimeKit makes its Message-IDs on this computer's name, and the Wrapper's own
    /// mail server put a Received line naming the computer on every turn, so anyone sent a turn could read the name.
    /// A Resend went out with the first send's Message-ID, and a mailbox that already had that email dropped it.
    /// </summary>
    public class OutgoingHeaderTests
    {
        private const string Me = "me@example.com";
        private const string Bob = "bob@example.org";

        private static MimeMessage Turn()
        {
            MimeMessage email = new MimeMessage();
            email.From.Add(MailboxAddress.Parse(Me));
            email.To.Add(MailboxAddress.Parse(Bob));
            email.Subject = "AoW email game (Highpass)";
            BodyBuilder body = new BodyBuilder { TextBody = "Age of Wonders email game" };
            body.Attachments.Add("Highpass.asg", new byte[] { 1, 2, 3, 4 });
            email.Body = body.ToMessageBody();
            return email;
        }

        [Fact]
        public void A_new_Message_ID_is_on_the_senders_domain_and_new_each_time()
        {
            string first = MailHelper.NewMessageId(Me);

            Assert.EndsWith("@example.com", first);
            Assert.DoesNotContain(Environment.MachineName, first, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(first, MailHelper.NewMessageId(Me));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no address")]
        [InlineData("me@")]
        public void Without_a_senders_domain_the_Message_ID_names_no_computer(string from)
        {
            Assert.EndsWith("@localhost.localdomain", MailHelper.NewMessageId(from));
        }

        [Fact]
        public void Wrapper_messages_carry_Message_IDs_on_the_senders_domain()
        {
            MimeMessage query = TurnQuery.BuildQuery(Me, Bob, "Highpass.asg", "abc123");
            MimeMessage reply = TurnQuery.BuildReply(new TurnQueryRequest { From = Me, Game = "Highpass.asg", QueryId = "abc123" }, Bob, new TurnState { Status = ActivityState.Received });
            MimeMessage notice = TurnQuery.BuildNotice(Bob, Me, "Highpass.asg", DateTimeOffset.Now, "carol@example.net");

            Assert.EndsWith("@example.com", query.MessageId);
            Assert.EndsWith("@example.org", reply.MessageId);
            Assert.EndsWith("@example.org", notice.MessageId);
        }

        [Fact]
        public void A_turn_going_out_loses_the_local_servers_Received_line_and_keeps_its_Message_ID()
        {
            //A copy kept for Resend by an older Wrapper still has the line; a failed send tried again keeps its Message-ID,
            //as the first never arrived
            MimeMessage email = Turn();
            email.Headers.Add(HeaderId.Received, "from DESKTOP-ABC1234 (DESKTOP-ABC1234 [127.0.0.1]) by DESKTOP-ABC1234.com (Age of Wonders Email Wrapper)");
            email.MessageId = "kept@example.com";

            SmtpSender.PrepareForSending(email);

            Assert.DoesNotContain(email.Headers, header => header.Id == HeaderId.Received);
            Assert.Equal("kept@example.com", email.MessageId);
        }

        [Fact]
        public void A_turn_going_out_without_a_Message_ID_gets_one_on_the_senders_domain()
        {
            //MimeKit gives every new message one on the computer's name, so the test takes it off
            MimeMessage email = Turn();
            email.Headers.Remove(HeaderId.MessageId);
            Assert.True(string.IsNullOrEmpty(email.MessageId));

            SmtpSender.PrepareForSending(email);

            Assert.EndsWith("@example.com", email.MessageId);
        }

        [Fact]
        public void A_resend_goes_out_as_a_new_email()
        {
            MimeMessage email = Turn();
            email.MessageId = "first@desktop-abc1234";
            DateTimeOffset firstSent = DateTimeOffset.Now.AddDays(-3);
            email.Date = firstSent;

            ResendHelper.Renew(email);

            Assert.NotEqual("first@desktop-abc1234", email.MessageId);
            Assert.EndsWith("@example.com", email.MessageId);
            Assert.True(email.Date > firstSent.AddDays(2));
        }

        [Fact]
        public void A_resend_says_when_the_turn_was_first_sent_through_any_number_of_resends()
        {
            MimeMessage email = new MimeMessage();
            email.From.Add(MailboxAddress.Parse("me@example.com"));
            DateTimeOffset first = new DateTimeOffset(2026, 10, 20, 21, 15, 0, TimeSpan.FromHours(2));
            email.Date = first;
            Assert.Null(ResendHelper.ResentOf(email));

            ResendHelper.Renew(email);
            Assert.Equal(first, ResendHelper.ResentOf(email));
            Assert.NotEqual(first, email.Date);

            ResendHelper.Renew(email);
            Assert.Equal(first, ResendHelper.ResentOf(email));
            Assert.Single(email.Headers, header => header.Field == TurnQuery.ResendOfHeader);
        }
    }
}
