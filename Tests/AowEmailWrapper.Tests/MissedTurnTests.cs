using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Pollers;
using MimeKit;
using Xunit;
using FakeInbox = AowEmailWrapper.Tests.ReadWrapperMessageTests.FakeInbox;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// A Wrapper that closed in the middle of a mail check (an automatic update did, right after start-up) could
    /// store a turn and mark its email as read without the activity log recording it. The game stayed Sent and the
    /// player was never told the turn had come. A check now leaves such an email unread, and every connection looks
    /// for turns of games shown as sent that arrived after the player's last copy of the game.
    /// </summary>
    [Collection(nameof(ProcessStateCollection))]
    public class MissedTurnTests : IDisposable
    {
        private const string Me = "me@example.com";
        private const string Opponent = "opponent@example.com";
        private const string Game = "Swolterwood 1v1.asg";

        //The game this was found in: the player's turn went at 10:50 local time, the opponent's came back at 02:21
        private static readonly DateTimeOffset MySend = new DateTimeOffset(2026, 10, 25, 23, 50, 8, TimeSpan.Zero);
        private static readonly DateTimeOffset TheirTurn = new DateTimeOffset(2026, 10, 26, 15, 21, 28, TimeSpan.Zero);

        private readonly string _root;
        private readonly string _oldAppData;

        public MissedTurnTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            string appData = Path.Combine(_root, "AppData");
            Directory.CreateDirectory(appData);
            _oldAppData = Environment.GetEnvironmentVariable("APPDATA");
            Environment.SetEnvironmentVariable("APPDATA", appData);
            ForgetAppDataFolders();
            Assert.StartsWith(_root, AppDataHelper.Root.FullName, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _oldAppData);
            ForgetAppDataFolders();
            try { Directory.Delete(_root, true); } catch { }
        }

        private static void ForgetAppDataFolders()
        {
            foreach (FieldInfo field in typeof(AppDataHelper).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(DirectoryInfo)))
            {
                field.SetValue(null, null);
            }
        }

        #region Choosing the missed turn

        private static SentGame Sent(DateTimeOffset when, string fileName = Game)
        {
            return new SentGame { FileName = fileName, Sent = when, Players = new List<string> { Opponent } };
        }

        private static MailboxTurn Mail(uint uid, string from, DateTimeOffset arrived, string fileName = Game)
        {
            return new MailboxTurn { Uid = uid, FileName = fileName, From = from, Date = arrived, Arrived = arrived };
        }

        private static List<MailboxTurn> Pick(IEnumerable<MailboxTurn> inbox, IEnumerable<MailboxTurn> sentFolder, params SentGame[] games)
        {
            return ContactHistory.PickMissed(inbox, sentFolder, games, new[] { Me });
        }

        [Fact]
        public void A_turn_that_arrived_after_the_last_send_is_found()
        {
            List<MailboxTurn> missed = Pick(
                new[] { Mail(7, Opponent, TheirTurn) },
                new[] { Mail(90, Me, MySend.AddSeconds(2)) },
                Sent(MySend));

            Assert.Equal(new uint[] { 7 }, missed.Select(turn => turn.Uid).ToArray());
        }

        [Fact]
        public void The_turn_the_player_answered_is_not_found()
        {
            List<MailboxTurn> missed = Pick(
                new[] { Mail(6, Opponent, MySend.AddHours(-5)) },
                new[] { Mail(90, Me, MySend.AddSeconds(2)) },
                Sent(MySend));

            Assert.Empty(missed);
        }

        [Fact]
        public void A_computer_clock_behind_the_servers_cannot_make_the_answered_turn_look_new()
        {
            //The player answered within minutes on a computer eight minutes slow: the activity log dates the send
            //before the answered turn arrived, but the server's copy of the send is after it
            DateTimeOffset copy = MySend;
            DateTimeOffset answered = copy.AddMinutes(-3);
            SentGame slowClock = Sent(copy.AddMinutes(-8));

            Assert.Empty(Pick(new[] { Mail(6, Opponent, answered) }, new[] { Mail(90, Me, copy) }, slowClock));
            Assert.Equal(new uint[] { 8 }, Pick(new[] { Mail(6, Opponent, answered), Mail(8, Opponent, copy.AddHours(3)) }, new[] { Mail(90, Me, copy) }, slowClock)
                .Select(turn => turn.Uid).ToArray());
        }

        [Fact]
        public void A_game_whose_send_has_no_copy_on_the_server_is_left_alone()
        {
            //Sent from another account, or through a provider that keeps no Sent folder
            Assert.Empty(Pick(new[] { Mail(7, Opponent, TheirTurn) }, new MailboxTurn[0], Sent(MySend)));
        }

        [Fact]
        public void A_copy_from_an_earlier_send_does_not_stand_for_the_last_one()
        {
            //The last send left no copy here; the newest copy is the send before it, which the turn answered
            DateTimeOffset earlierCopy = MySend.AddDays(-1);
            Assert.Empty(Pick(new[] { Mail(7, Opponent, MySend.AddHours(-2)) }, new[] { Mail(90, Me, earlierCopy) }, Sent(MySend)));
        }

        [Fact]
        public void A_copy_the_player_sent_themselves_counts_as_the_send()
        {
            List<MailboxTurn> missed = Pick(
                new[] { Mail(5, Me, MySend.AddSeconds(3)), Mail(7, Opponent, TheirTurn) },
                null,
                Sent(MySend));

            Assert.Equal(new uint[] { 7 }, missed.Select(turn => turn.Uid).ToArray());
        }

        [Fact]
        public void Only_the_games_players_count_and_the_newest_of_their_turns_is_chosen()
        {
            List<MailboxTurn> missed = Pick(
                new[]
                {
                    Mail(7, Opponent, TheirTurn),
                    Mail(8, "stranger@example.com", TheirTurn.AddHours(2)),
                    Mail(9, Opponent, TheirTurn.AddHours(1)),
                    Mail(10, Opponent, TheirTurn.AddHours(4), "Another game.asg")
                },
                new[] { Mail(90, Me, MySend) },
                Sent(MySend));

            Assert.Equal(new uint[] { 9 }, missed.Select(turn => turn.Uid).ToArray());
        }

        #endregion

        #region Looking it up in the mailbox

        private static MimeMessage TurnEmail(string from, string to, string fileName = Game)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = "AoW email game (" + Path.GetFileNameWithoutExtension(fileName) + ")";
            BodyBuilder body = new BodyBuilder { TextBody = "Age of Wonders email game" };
            body.Attachments.Add(fileName, new byte[] { 1, 2, 3, 4 });
            message.Body = body.ToMessageBody();
            return message;
        }

        [Fact]
        public void The_mailbox_gives_the_read_turn_that_came_after_the_send_and_the_inbox_is_left_open()
        {
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Me), seen: true, now.AddDays(-3));
            inbox.Add(2, TurnEmail(Opponent, Me), seen: true, now.AddHours(-20));
            inbox.Add(3, TurnEmail(Opponent, Me, "Another game.asg"), seen: true, now.AddHours(-19));
            inbox.Add(4, TurnEmail(Opponent, Me), seen: false, now.AddHours(-1));
            FakeInbox sent = FakeInbox.CreateBeside(inbox);
            sent.Add(50, TurnEmail(Me, Opponent), seen: true, now.AddDays(-2));

            List<MailboxTurn> missed = ContactHistory.FindMissedTurns(inbox.Folder, sent.Folder,
                new[] { new SentGame { FileName = Game, Sent = now.AddDays(-2), Players = new List<string> { Opponent } } },
                new[] { Me }, ImapPoller.LookBackDays, CancellationToken.None);

            //The unread turn is the poller's to file
            Assert.Equal(new uint[] { 2 }, missed.Select(turn => turn.Uid).ToArray());
            Assert.True(inbox.IsOpen);
        }

        [Fact]
        public void Without_a_sent_folder_nothing_vouches_for_the_send()
        {
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(2, TurnEmail(Opponent, Me), seen: true, now.AddHours(-20));

            List<MailboxTurn> missed = ContactHistory.FindMissedTurns(inbox.Folder, null,
                new[] { new SentGame { FileName = Game, Sent = now.AddDays(-2), Players = new List<string> { Opponent } } },
                new[] { Me }, ImapPoller.LookBackDays, CancellationToken.None);

            Assert.Empty(missed);
        }

        #endregion

        #region A check cut off by closing

        private ImapPoller Poller(Action<AowGameSavedEventArgs> record)
        {
            string holding = Path.Combine(_root, "CheckEmail");
            Directory.CreateDirectory(holding);
            AowGameManager games = new AowGameManager(holding, new AowGame[0], null);
            games.OnGameSaved += (sender, e) => record(e);
            return new ImapPoller("imap.example.com", 993, SSLType.SSL, Me, "secret", 10, EmailSaveFolder.EmailIn, games);
        }

        [Fact]
        public void A_turn_the_activity_log_did_not_take_stays_unread_and_the_check_stops()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Me), seen: false, DateTime.Now.AddHours(-2));
            inbox.Add(2, TurnEmail(Opponent, Me, "Another game.asg"), seen: false, DateTime.Now.AddHours(-1));

            int offered = 0;
            Poller(e => offered++).ScanMailbox(inbox.Folder);

            Assert.Equal(1, offered);
            Assert.False(inbox.IsSeen(1));
            Assert.False(inbox.IsSeen(2));
        }

        [Fact]
        public void A_recorded_turn_is_marked_as_read()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Me), seen: false, DateTime.Now.AddHours(-2));
            inbox.Add(2, TurnEmail(Opponent, Me, "Another game.asg"), seen: false, DateTime.Now.AddHours(-1));

            Assert.True(Poller(e => e.Recorded = true).ScanMailbox(inbox.Folder));

            Assert.True(inbox.IsSeen(1));
            Assert.True(inbox.IsSeen(2));
        }

        #endregion

        #region Games shown as sent

        private static Activity Line(string fileName, ActivityState status, DateTime when, string players = null, string recipients = null)
        {
            return new Activity(status, AowGameType.Aow1, fileName, "Map", "3") { DateTicks = when.Ticks.ToString(), Players = players, Recipients = recipients };
        }

        [Fact]
        public void Only_games_whose_last_line_is_Sent_are_looked_up_with_their_other_players()
        {
            DateTime now = DateTime.Now;
            ActivityList log = new ActivityList();
            log.Activities.Add(Line(Game, ActivityState.Sent, now.AddHours(-30), Me + ";" + Opponent));
            log.Activities.Add(Line("Waiting.asg", ActivityState.Received, now.AddHours(-5), Me + ";" + Opponent));
            log.Activities.Add(Line("Paused.asg", ActivityState.Paused, now.AddHours(-5), Me + ";" + Opponent));
            log.Activities.Add(Line("Over.asg", ActivityState.Ended, now.AddHours(-5), Me + ";" + Opponent));
            log.Activities.Add(Line("Moved on.asg", ActivityState.Sent, now.AddHours(-9), Me + ";" + Opponent));
            log.Activities.Add(Line("Moved on.asg", ActivityState.Received, now.AddHours(-2), Me + ";" + Opponent));
            log.Activities.Add(Line("Old line.asg", ActivityState.Sent, now.AddHours(-50), null, "OPPONENT@example.com;third@example.com"));

            List<SentGame> games = Main.SentGames(log, new[] { "ME@example.com" }).OrderBy(game => game.FileName).ToList();

            Assert.Equal(new[] { "Old line.asg", Game }, games.Select(game => game.FileName).ToArray());
            Assert.Equal(new[] { "OPPONENT@example.com", "third@example.com" }, games[0].Players.ToArray());
            Assert.Equal(new[] { Opponent }, games[1].Players.ToArray());
            Assert.Equal(new DateTimeOffset(now.AddHours(-30)), games[1].Sent);
        }

        #endregion
    }
}
