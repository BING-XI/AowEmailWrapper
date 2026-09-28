using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AowEmailWrapper.ASG;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// Wrapper-to-wrapper "who has the turn?" mail: recognised by its headers, answered only for a
    /// known player of a game the player has, and the answer recorded against the game.
    /// </summary>
    public class TurnQueryTests
    {
        private const string Game = "Highpass (Dave, Fred).asg";

        #region Messages

        [Fact]
        public void A_query_survives_the_wire_and_is_recognised()
        {
            MimeMessage query = RoundTrip(TurnQuery.BuildQuery("me@example.com", "bob@example.com", Game, "abc123"));

            Assert.True(TurnQuery.IsWrapperMessage(query));
            Assert.True(TurnQuery.IsQuery(query));
            Assert.False(TurnQuery.IsReply(query));
            Assert.True(TurnQuery.IsWrapperMessage(query.Headers));

            TurnQueryRequest parsed = TurnQuery.ParseQuery(query);
            Assert.Equal(Game, parsed.Game);
            Assert.Equal("abc123", parsed.QueryId);
            Assert.Equal("me@example.com", parsed.From);
            Assert.Null(TurnQuery.ParseReply(query));
            Assert.Contains(Game, query.Subject);
            Assert.Equal("auto-generated", query.Headers["Auto-Submitted"]);
        }

        [Fact]
        public void A_reply_carries_the_state_and_survives_the_wire()
        {
            TurnQueryRequest query = new TurnQueryRequest { Game = Game, QueryId = "abc123", From = "me@example.com" };
            DateTimeOffset when = new DateTimeOffset(2026, 9, 5, 18, 30, 0, TimeSpan.FromHours(10));
            TurnState state = new TurnState { Status = ActivityState.Sent, Date = when, SentTo = "carol@example.org" };

            MimeMessage reply = RoundTrip(TurnQuery.BuildReply(query, "bob@example.com", state));

            Assert.True(TurnQuery.IsReply(reply));
            Assert.False(TurnQuery.IsQuery(reply));
            Assert.Equal("me@example.com", reply.To.Mailboxes.Single().Address);

            TurnState parsed = TurnQuery.ParseReply(reply);
            Assert.Equal(Game, parsed.Game);
            Assert.Equal("abc123", parsed.QueryId);
            Assert.Equal("bob@example.com", parsed.Responder);
            Assert.Equal(ActivityState.Sent, parsed.Status);
            Assert.Equal(when, parsed.Date);
            Assert.Equal("carol@example.org", parsed.SentTo);
            Assert.False(parsed.Holds);
            Assert.Null(TurnQuery.ParseQuery(reply));
        }

        [Fact]
        public void Ordinary_mail_is_not_wrapper_mail()
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(new MailboxAddress("Bob", "bob@example.com"));
            message.Subject = "AoW Wrapper: where is Highpass?";
            message.Body = new TextPart("plain") { Text = "hi" };

            Assert.False(TurnQuery.IsWrapperMessage(message));
            Assert.Null(TurnQuery.ParseQuery(message));
            Assert.Null(TurnQuery.ParseReply(message));
            Assert.False(TurnQuery.IsWrapperMessage((MimeMessage)null));

            message.Headers.Add(TurnQuery.KindHeader, "something-else");
            Assert.False(TurnQuery.IsWrapperMessage(message));
        }

        [Fact]
        public void A_query_missing_its_id_or_game_is_dropped()
        {
            MimeMessage query = TurnQuery.BuildQuery("me@example.com", "bob@example.com", Game, "abc123");
            query.Headers.Remove(TurnQuery.IdHeader);
            Assert.Null(TurnQuery.ParseQuery(query));

            query = TurnQuery.BuildQuery("me@example.com", "bob@example.com", Game, "abc123");
            query.Headers.Remove(TurnQuery.GameHeader);
            Assert.Null(TurnQuery.ParseQuery(query));
        }

        #endregion

        #region Answering rules

        [Fact]
        public void Only_a_known_player_of_a_game_we_have_is_answered()
        {
            ActivityList log = new ActivityList();
            log.Activities.Add(new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4")
            {
                Players = "alice@example.com;bob@example.com;me@example.com",
                Recipients = "carol@example.org",
                Sender = "dave@example.net"
            });
            log.AddContacts(new[] { "alice@example.com", "bob@example.com", "carol@example.org", "dave@example.net", "stranger@example.com" });
            string[] own = { "me@example.com" };

            Assert.True(TurnQuery.MayAnswer(Query("alice@example.com"), log, own));   //listed in the save
            Assert.True(TurnQuery.MayAnswer(Query("carol@example.org"), log, own));   //we sent it to them
            Assert.True(TurnQuery.MayAnswer(Query("DAVE@example.net"), log, own));    //they sent it to us
            Assert.False(TurnQuery.MayAnswer(Query("stranger@example.com"), log, own)); //a contact, but not in this game
            Assert.False(TurnQuery.MayAnswer(Query("me@example.com"), log, own));     //our own copy
            Assert.False(TurnQuery.MayAnswer(Query("nobody@example.com"), log, own)); //never heard of them
            Assert.False(TurnQuery.MayAnswer(Query("alice@example.com", "Other Game.asg"), log, own)); //a game we do not have
            Assert.False(TurnQuery.MayAnswer(null, log, own));
        }

        [Fact]
        public void An_unknown_contact_who_is_listed_in_the_save_is_still_refused()
        {
            //Being named in a save file we received is not the same as having exchanged turns with us
            ActivityList log = new ActivityList();
            log.Activities.Add(new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4") { Players = "eve@example.com" });

            Assert.False(TurnQuery.MayAnswer(Query("eve@example.com"), log, null));
        }

        [Fact]
        public void The_state_reported_is_the_activity_status_and_date()
        {
            Activity received = new Activity(ActivityState.Received, AowGameType.Aow1, Game, "Highpass", "4") { Recipients = "carol@example.org" };
            TurnState holding = TurnQuery.StateOf(received, Game, "q1");
            Assert.True(holding.Holds);
            Assert.NotNull(holding.Date);
            Assert.Null(holding.SentTo);

            Activity sent = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "5") { Recipients = "carol@example.org" };
            TurnState passed = TurnQuery.StateOf(sent, Game, "q1");
            Assert.False(passed.Holds);
            Assert.Equal("carol@example.org", passed.SentTo);

            Assert.Equal(ActivityState.None, TurnQuery.StateOf(null, Game, "q1").Status);
        }

        [Fact]
        public void Every_other_player_is_asked_once_and_never_ourselves()
        {
            Activity activity = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4")
            {
                Players = "Alice@example.com;bob@example.com;me@example.com",
                Recipients = "bob@example.com;carol@example.org",
                Sender = "dave@example.net"
            };

            List<string> asked = TurnQuery.PlayersToAsk(activity, new[] { "ME@example.com" });

            Assert.Equal(new[] { "Alice@example.com", "bob@example.com", "carol@example.org", "dave@example.net" }, asked);
            Assert.Empty(TurnQuery.PlayersToAsk(null, null));
        }

        #endregion

        #region Whereabouts

        [Fact]
        public void Replies_are_recorded_per_responder_and_the_holder_follows_the_turn()
        {
            Activity activity = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4");
            DateTimeOffset when = new DateTimeOffset(2026, 9, 5, 18, 30, 0, TimeSpan.Zero);

            TurnQuery.RecordWhereabouts(activity, new TurnState { Responder = "bob@example.com", Status = ActivityState.Sent, SentTo = "carol@example.org", Date = when });
            TurnQuery.RecordWhereabouts(activity, new TurnState { Responder = "carol@example.org", Status = ActivityState.Received, Date = when });

            Assert.Equal("carol@example.org", activity.Holder);
            Assert.Contains("bob@example.com sent", activity.Whereabouts);
            Assert.Contains("carol@example.org has held", activity.Whereabouts);
            Assert.Equal(2, activity.Whereabouts.Split(new[] { TurnQuery.WhereaboutsSeparator }, StringSplitOptions.None).Length);

            //Carol answers again, later, having sent it on: she is no longer the holder and her old line is replaced
            TurnQuery.RecordWhereabouts(activity, new TurnState { Responder = "carol@example.org", Status = ActivityState.Sent, SentTo = "dave@example.net", Date = when.AddDays(1) });

            Assert.Null(activity.Holder);
            Assert.Equal(2, activity.Whereabouts.Split(new[] { TurnQuery.WhereaboutsSeparator }, StringSplitOptions.None).Length);
            Assert.DoesNotContain("has held", activity.Whereabouts);
            Assert.Contains("carol@example.org sent 'Highpass (Dave, Fred).asg' to dave@example.net", activity.Whereabouts);
        }

        #endregion

        #region Who probably has it

        private const string Me = "me@example.com";
        private const string Bob = "bob@example.com";
        private const string Carol = "carol@example.org";
        private const string Dave = "dave@example.net";
        private static readonly DateTimeOffset Day1 = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

        /// <summary>A turn the player sent to Bob on day 1, as the activity log has it.</summary>
        private static Activity SentToBob()
        {
            Activity activity = new Activity(ActivityState.Sent, AowGameType.Aow1, Game, "Highpass", "4");
            activity.Recipients = Bob;
            activity.DateTicks = Day1.LocalDateTime.Ticks.ToString();
            return activity;
        }

        private static void Answer(Activity activity, string from, ActivityState status, int day, string sentTo = null)
        {
            TurnQuery.RecordWhereabouts(activity, new TurnState { Responder = from, Status = status, SentTo = sentTo, Date = Day1.AddDays(day - 1) });
        }

        [Fact]
        public void Three_wrappers_saying_not_us_point_at_the_fourth_player()
        {
            //Four players; Dave has no Wrapper and never answers
            Activity activity = SentToBob();
            Answer(activity, Bob, ActivityState.Sent, 2, Carol);
            Answer(activity, Carol, ActivityState.Sent, 3, Dave);

            TurnSend likely = TurnQuery.LikelyHolder(activity, new[] { Me });

            Assert.NotNull(likely);
            Assert.Equal(Dave, likely.To);
            Assert.Equal(Carol, likely.From);
            Assert.Equal(Day1.AddDays(2), likely.Date);
        }

        [Fact]
        public void The_players_own_send_is_part_of_the_chain()
        {
            //Carol answered about an older turn; the newest send is the player's own, to Bob, who has not answered
            Activity activity = SentToBob();
            activity.DateTicks = Day1.AddDays(4).LocalDateTime.Ticks.ToString();
            Answer(activity, Carol, ActivityState.Sent, 3, Me);

            TurnSend likely = TurnQuery.LikelyHolder(activity, new[] { Me });

            Assert.Equal(Bob, likely.To);
            Assert.Null(likely.From);
        }

        [Fact]
        public void No_guess_when_a_wrapper_says_it_holds_the_turn()
        {
            Activity activity = SentToBob();
            Answer(activity, Bob, ActivityState.Sent, 2, Carol);
            Answer(activity, Carol, ActivityState.Received, 2);

            Assert.Null(TurnQuery.LikelyHolder(activity, new[] { Me }));
            Assert.Equal(Carol, activity.Holder);
        }

        [Fact]
        public void No_guess_when_the_suspected_player_has_answered_without_claiming_it()
        {
            Activity activity = SentToBob();
            Answer(activity, Bob, ActivityState.Sent, 2, Carol);
            Answer(activity, Carol, ActivityState.None, 0);

            Assert.Null(TurnQuery.LikelyHolder(activity, new[] { Me }));
        }

        [Fact]
        public void The_senders_own_copy_is_not_a_recipient_but_two_recipients_are_no_guess()
        {
            //Carol keeps a copy for herself: the turn still went to Dave alone
            Activity activity = SentToBob();
            Answer(activity, Bob, ActivityState.Sent, 2, Carol);
            Answer(activity, Carol, ActivityState.Sent, 3, Dave + ";" + Carol);
            Assert.Equal(Dave, TurnQuery.LikelyHolder(activity, new[] { Me }).To);

            //Sent to two players at once: which one plays next cannot be told
            Answer(activity, Carol, ActivityState.Sent, 3, Dave + ";" + "erin@example.com");
            Assert.Null(TurnQuery.LikelyHolder(activity, new[] { Me }));
        }

        [Fact]
        public void Answers_survive_the_activity_log_and_older_logs_still_load()
        {
            Activity activity = SentToBob();
            Answer(activity, Carol, ActivityState.Sent, 3, Dave);
            activity.LikelyHolder = Dave;

            System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(Activity));
            string xml;
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, activity);
                xml = writer.ToString();
            }
            Activity back;
            using (StringReader reader = new StringReader(xml))
            {
                back = (Activity)serializer.Deserialize(reader);
            }

            TurnAnswer answer = Assert.Single(back.Answers);
            Assert.Equal(Carol, answer.Responder);
            Assert.Equal(ActivityState.Sent, answer.Status);
            Assert.Equal(Dave, answer.SentTo);
            Assert.Equal(Dave, back.LikelyHolder);
            Assert.Equal(Dave, TurnQuery.LikelyHolder(back, new[] { Me }).To);

            using (StringReader reader = new StringReader("<activity game_type=\"Aow1\" file_name=\"x.asg\" status=\"Sent\" ticks=\"1\" />"))
            {
                Assert.Empty(((Activity)serializer.Deserialize(reader)).Answers);
            }
        }

        [Fact]
        public void The_query_email_asks_who_has_the_turn()
        {
            MimeMessage query = TurnQuery.BuildQuery(Me, Bob, Game, TurnQuery.NewQueryId());

            Assert.Contains("who has", query.Subject);
            Assert.Contains("asking who has the turn", query.TextBody);
        }

        #endregion

        #region Player addresses from the save

        [Fact]
        public void Players_are_read_from_a_real_save_when_one_is_available()
        {
            //Verified against the AoW1 corpus; the file is only present on a machine with the game installed
            string path = Environment.GetEnvironmentVariable("AOW_SAMPLE_ASG") ?? @"E:\Age of Wonders\EmailIn\Fortress Azania.asg";
            if (!File.Exists(path))
            {
                return;
            }

            using (ASGFileInfo info = new ASGFileInfo(Path.GetFileName(path), File.ReadAllBytes(path)))
            {
                Assert.Equal(AowGameType.Aow1, info.GameType);
                Assert.True(info.PlayerEmails.Count >= 2, "a PBEM save names at least two players");
                Assert.All(info.PlayerEmails, address => Assert.Contains("@", address));
                Assert.Equal(info.PlayerEmails.Count, info.PlayerEmails.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }

        [Fact]
        public void A_file_that_is_not_a_save_names_no_players()
        {
            using (ASGFileInfo info = new ASGFileInfo("Junk.asg", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))
            {
                Assert.Empty(info.PlayerEmails);
                Assert.Equal(string.Empty, ASGFileInfo.JoinAddresses(info.PlayerEmails));
            }
            Assert.Equal(string.Empty, ASGFileInfo.JoinAddresses(null));
            Assert.Equal("a@x.com;b@y.org", ASGFileInfo.JoinAddresses(new[] { "a@x.com", "b@y.org" }));
        }

        #endregion

        private static TurnQueryRequest Query(string from, string game = Game)
        {
            return new TurnQueryRequest { Game = game, QueryId = "q1", From = from };
        }

        private static MimeMessage RoundTrip(MimeMessage message)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                message.WriteTo(stream);
                stream.Position = 0;
                return MimeMessage.Load(stream);
            }
        }
    }
}
