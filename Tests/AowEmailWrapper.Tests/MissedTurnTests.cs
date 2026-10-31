using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AowEmailWrapper.ASG;
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
    /// player was never told the turn had come. A check now leaves such an email unread, and each connection files a
    /// turn that was stored after its game's send but never recorded.
    /// </summary>
    [Collection(nameof(ProcessStateCollection))]
    public class MissedTurnTests : IDisposable
    {
        private const string Me = "me@example.com";
        private const string Opponent = "opponent@example.com";
        private const string Game = "Swolterwood 1v1.asg";

        private static readonly byte[] Answered = { 1, 1, 1, 1 };
        private static readonly byte[] TheirTurn = { 2, 2, 2, 2 };

        private readonly string _root;
        private readonly string _holding;
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

            //Turns of unknown games go here; the test saves are not real games
            _holding = Path.Combine(_root, "CheckEmail");
            Directory.CreateDirectory(_holding);
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

        private static MimeMessage TurnEmail(string from, byte[] save, string fileName = Game)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(from == Me ? Opponent : Me));
            message.Subject = "AoW email game (" + Path.GetFileNameWithoutExtension(fileName) + ")";
            BodyBuilder body = new BodyBuilder { TextBody = "Age of Wonders email game" };
            body.Attachments.Add(fileName, save);
            message.Body = body.ToMessageBody();
            return message;
        }

        private ImapPoller Poller(Action<AowGameSavedEventArgs> record)
        {
            AowGameManager games = new AowGameManager(_holding, new AowGame[0], null);
            games.OnGameSaved += (sender, e) => record(e);
            return new ImapPoller("imap.example.com", 993, SSLType.SSL, Me, "secret", 10, EmailSaveFolder.EmailIn, games);
        }

        /// <summary>The game's turn file as this install holds it.</summary>
        private void Stored(byte[] save, DateTime written, string fileName = Game)
        {
            string path = Path.Combine(_holding, fileName);
            File.WriteAllBytes(path, save);
            File.SetLastWriteTime(path, written);
        }

        /// <summary>A game shown as sent at <paramref name="when"/>, with the copy kept for Resend written then.</summary>
        private static SentGame Sent(DateTime when, string fileName = Game)
        {
            SentCopy(when, fileName);
            return new SentGame { FileName = fileName, Sent = when, Players = new List<string> { Opponent } };
        }

        private static void SentCopy(DateTime written, string fileName = Game)
        {
            string path = Path.Combine(AppDataHelper.Resend.FullName, fileName + "_resend.eml");
            File.WriteAllText(path, "the sent turn");
            File.SetLastWriteTime(path, written);
        }

        #region Turns that never reached the activity log

        [Fact]
        public void A_turn_stored_after_the_send_but_never_recorded_is_filed()
        {
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Answered), seen: true, now.AddDays(-3));
            inbox.Add(2, TurnEmail(Opponent, TheirTurn), seen: true, now.AddDays(-1));
            Stored(TheirTurn, now.AddDays(-1));

            List<AowGameSavedEventArgs> recorded = new List<AowGameSavedEventArgs>();
            int filed = Poller(e => { e.Recorded = true; recorded.Add(e); }).RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None);

            Assert.Equal(1, filed);
            Assert.Equal(Opponent, Assert.Single(recorded).Sender);
        }

        [Fact]
        public void The_turn_the_player_answered_is_left_alone_without_a_download()
        {
            //Stored when it came, before the player's answer went
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Answered), seen: true, now.AddDays(-2).AddHours(-1));
            Stored(Answered, now.AddDays(-2).AddHours(-1));

            int offered = 0;
            int filed = Poller(e => offered++).RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None);

            Assert.Equal(0, filed);
            Assert.Equal(0, offered);
            Assert.Equal(0, inbox.Downloads);
        }

        [Fact]
        public void A_turn_read_on_another_computer_or_a_phone_is_left_alone()
        {
            //A newer turn waits in the mailbox, already read, but this install never stored it
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(2, TurnEmail(Opponent, TheirTurn), seen: true, now.AddDays(-1));
            Stored(Answered, now.AddDays(-2).AddHours(-1));

            int offered = 0;
            Assert.Equal(0, Poller(e => offered++).RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None));
            Assert.Equal(0, offered);
            Assert.Equal(0, inbox.Downloads);
        }

        [Fact]
        public void A_newer_file_that_no_email_carries_is_left_alone_and_looked_up_once()
        {
            //The game rewrote its file, or the player saved over it
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(2, TurnEmail(Opponent, TheirTurn), seen: true, now.AddDays(-1));
            Stored(new byte[] { 9, 9, 9, 9 }, now.AddDays(-1));

            int offered = 0;
            ImapPoller poller = Poller(e => offered++);
            Assert.Equal(0, poller.RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None));
            Assert.Equal(0, poller.RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None));

            Assert.Equal(0, offered);
            Assert.Equal(1, inbox.Downloads);
        }

        [Fact]
        public void Only_the_games_players_count_and_unread_mail_is_left_to_the_scan()
        {
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(2, TurnEmail("stranger@example.com", TheirTurn), seen: true, now.AddDays(-1));
            inbox.Add(3, TurnEmail(Opponent, TheirTurn), seen: false, now.AddDays(-1));
            Stored(TheirTurn, now.AddDays(-1));

            int offered = 0;
            Assert.Equal(0, Poller(e => offered++).RecoverStrandedTurns(inbox.Folder, new[] { Sent(now.AddDays(-2)) }, CancellationToken.None));
            Assert.Equal(0, offered);
        }

        [Fact]
        public void Without_a_copy_of_its_last_send_a_turn_stored_days_after_it_is_filed()
        {
            //Up to 2.1.11 the turn's arrival deleted the copy before the activity log was saved, so a turn stored while
            //the Wrapper was still downloading an update lost the copy along with its line
            DateTime now = DateTime.Now;
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Answered), seen: true, now.AddDays(-4));
            inbox.Add(2, TurnEmail(Opponent, TheirTurn), seen: true, now.AddDays(-1).AddHours(-1));
            Stored(TheirTurn, now.AddDays(-1));
            SentGame game = new SentGame { FileName = Game, Sent = now.AddDays(-3), Players = new List<string> { Opponent } };

            List<AowGameSavedEventArgs> recorded = new List<AowGameSavedEventArgs>();
            int filed = Poller(e => { e.Recorded = true; recorded.Add(e); }).RecoverStrandedTurns(inbox.Folder, new[] { game }, CancellationToken.None);

            Assert.Equal(1, filed);
            Assert.Equal(Opponent, Assert.Single(recorded).Sender);
        }

        [Fact]
        public void Without_a_copy_of_its_last_send_a_change_of_time_zone_cannot_make_the_answered_turn_look_new()
        {
            //The player answered half an hour after the turn came, then the computer moved 25 hours east, nearly the 26
            //that separate the two furthest time zones: with no copy, the line's time is all there is
            DateTime now = DateTime.Now;
            DateTime arrived = now.AddDays(-2);
            DateTime answered = arrived.AddMinutes(30);
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Answered), seen: true, arrived);
            Stored(Answered, arrived);
            SentGame game = new SentGame { FileName = Game, Sent = answered.AddHours(-25), Players = new List<string> { Opponent } };

            int offered = 0;
            Assert.Equal(0, Poller(e => offered++).RecoverStrandedTurns(inbox.Folder, new[] { game }, CancellationToken.None));
            Assert.Equal(0, offered);
            Assert.Equal(0, inbox.Downloads);
        }

        [Fact]
        public void A_time_zone_changed_since_the_send_cannot_make_the_answered_turn_look_new()
        {
            //The player answered half an hour after the turn came, then the computer moved three hours east: the line's
            //time, kept as the clock showed it then, now reads earlier than the turn file
            DateTime now = DateTime.Now;
            DateTime arrived = now.AddDays(-1);
            DateTime answered = arrived.AddMinutes(30);
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, Answered), seen: true, arrived);
            Stored(Answered, arrived);
            SentCopy(answered);
            SentGame game = new SentGame { FileName = Game, Sent = answered.AddHours(-3), Players = new List<string> { Opponent } };

            int offered = 0;
            Assert.Equal(0, Poller(e => offered++).RecoverStrandedTurns(inbox.Folder, new[] { game }, CancellationToken.None));
            Assert.Equal(0, offered);
        }

        #endregion

        #region Closing waits for the checks under way

        /// <summary>A poller whose check and connection the test begins and ends by hand.</summary>
        private sealed class HandPoller : BasePoller
        {
            public HandPoller()
                : base("imap.example.com", 993, SSLType.SSL, Me, "secret", 10, EmailSaveFolder.EmailIn, null)
            { }

            public bool BeginCheck() { return TryPollBegin(); }
            public void EndCheck() { PollEnd(false, null); }
            public bool BeginConnection() { return TryBeginConnectionWork(); }
            public void EndConnection() { EndConnectionWork(false); }
        }

        [Fact]
        public void A_stopped_poller_begins_no_check_and_no_connection()
        {
            HandPoller poller = new HandPoller();
            poller.Stop();

            Assert.False(poller.BeginCheck());
            Assert.False(poller.BeginConnection());
            Assert.False(poller.IsPolling);
        }

        [Fact]
        public void Signing_in_and_checking_count_as_polling_with_no_gap_between()
        {
            HandPoller poller = new HandPoller();
            Assert.True(poller.BeginConnection());
            Assert.True(poller.IsPolling);
            //As the connection loop does: the scan begins, then the connection's count ends
            Assert.True(poller.BeginCheck());
            poller.EndConnection();
            Assert.True(poller.IsPolling);
            poller.EndCheck();
            Assert.False(poller.IsPolling);
        }

        [Fact]
        public async System.Threading.Tasks.Task Closing_waits_for_the_check_under_way_and_no_longer_than_its_limit()
        {
            HandPoller poller = new HandPoller();
            Assert.True(poller.BeginCheck());
            poller.Stop();

            Assert.False(await BasePoller.WhenIdle(new[] { poller }, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));

            System.Threading.Tasks.Task<bool> waiting = BasePoller.WhenIdle(new[] { poller }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));
            await System.Threading.Tasks.Task.Delay(50);
            Assert.False(waiting.IsCompleted);
            poller.EndCheck();
            Assert.True(await waiting);
        }

        #endregion

        #region A check whose turn the activity log did not take

        [Fact]
        public void A_turn_the_activity_log_did_not_take_stays_unread()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, TheirTurn), seen: false, DateTime.Now.AddHours(-2));
            inbox.Add(2, TurnEmail(Opponent, Answered, "Another game.asg"), seen: false, DateTime.Now.AddHours(-1));

            int offered = 0;
            Assert.False(Poller(e => offered++).ScanMailbox(inbox.Folder));

            Assert.Equal(2, offered);
            Assert.False(inbox.IsSeen(1));
            Assert.False(inbox.IsSeen(2));
        }

        [Fact]
        public void A_turn_that_could_not_be_stored_stays_unread()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, TheirTurn), seen: false, DateTime.Now.AddHours(-2));

            Poller(e => { throw new IOException("The file is in use"); }).ScanMailbox(inbox.Folder);

            Assert.False(inbox.IsSeen(1));
        }

        [Fact]
        public void A_recorded_turn_is_marked_as_read()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, TheirTurn), seen: false, DateTime.Now.AddHours(-2));
            inbox.Add(2, TurnEmail(Opponent, Answered, "Another game.asg"), seen: false, DateTime.Now.AddHours(-1));

            Assert.True(Poller(e => e.Recorded = true).ScanMailbox(inbox.Folder));

            Assert.True(inbox.IsSeen(1));
            Assert.True(inbox.IsSeen(2));
        }

        [Fact]
        public void A_turn_stored_before_anything_records_turns_is_not_taken_as_recorded()
        {
            AowGameManager games = new AowGameManager(_holding, new AowGame[0], null);
            using (ASGFileInfo info = new ASGFileInfo(Game, TheirTurn))
            {
                Assert.False(games.StoreDownloadFile(info, EmailSaveFolder.EmailIn, "Account", null, Opponent, null, false));
            }
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
            //A newer line of a game whose name contains this one's does not hide it
            log.Activities.Add(Line("Duel.asg", ActivityState.Sent, now.AddHours(-8), Me + ";" + Opponent));
            log.Activities.Add(Line("Big Duel.asg", ActivityState.Received, now.AddHours(-1), Me + ";" + Opponent));

            List<SentGame> games = Main.SentGames(log, new[] { "ME@example.com" }).OrderBy(game => game.FileName).ToList();

            Assert.Equal(new[] { "Duel.asg", "Old line.asg", Game }, games.Select(game => game.FileName).ToArray());
            Assert.Equal(new[] { "OPPONENT@example.com", "third@example.com" }, games[1].Players.ToArray());
            Assert.Equal(new[] { Opponent }, games[2].Players.ToArray());
            Assert.Equal(now.AddHours(-30), games[2].Sent);
        }

        #endregion

        #region A resend of a turn the player has answered

        /// <summary>A turn resent by its sender: it says when it was first sent.</summary>
        private static MimeMessage Resend(byte[] save, DateTimeOffset firstSent)
        {
            MimeMessage email = TurnEmail(Opponent, save);
            email.Date = firstSent;
            ResendHelper.Renew(email);
            return email;
        }

        [Fact]
        public void A_resend_of_a_turn_answered_since_is_not_stored()
        {
            //The player got the turn, played it and sent their own; the opponent then resent the old turn
            Stored(Answered, DateTime.Now.AddHours(-3));
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, Resend(TheirTurn, DateTimeOffset.Now.AddDays(-1)), seen: false, DateTime.Now.AddHours(-1));

            int offered = 0;
            ImapPoller poller = Poller(e => { offered++; e.Recorded = true; });
            string askedFor = null;
            poller.IsAnsweredResend = (fileName, firstSent) => { askedFor = fileName; return true; };
            poller.ScanMailbox(inbox.Folder);

            Assert.Equal(Game, askedFor);
            Assert.Equal(0, offered);
            Assert.Equal(Answered, File.ReadAllBytes(Path.Combine(_holding, Game)));
            //Dealt with: it is not fetched again at every check
            Assert.True(inbox.IsSeen(1));
        }

        [Fact]
        public void A_resend_of_a_turn_not_yet_answered_is_stored()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, Resend(TheirTurn, DateTimeOffset.Now.AddDays(-1)), seen: false, DateTime.Now.AddHours(-1));

            int offered = 0;
            ImapPoller poller = Poller(e => { offered++; e.Recorded = true; });
            poller.IsAnsweredResend = (fileName, firstSent) => false;
            poller.ScanMailbox(inbox.Folder);

            Assert.Equal(1, offered);
            Assert.Equal(TheirTurn, File.ReadAllBytes(Path.Combine(_holding, Game)));
        }

        [Fact]
        public void A_turn_sent_once_is_not_asked_about()
        {
            FakeInbox inbox = FakeInbox.Create();
            inbox.Add(1, TurnEmail(Opponent, TheirTurn), seen: false, DateTime.Now.AddHours(-1));

            bool asked = false;
            ImapPoller poller = Poller(e => e.Recorded = true);
            poller.IsAnsweredResend = (fileName, firstSent) => asked = true;
            poller.ScanMailbox(inbox.Folder);

            Assert.False(asked);
        }

        private static Activity Line(ActivityState state, DateTime when, string fileName = Game)
        {
            return new Activity(state, AowGameType.Aow1, fileName, "Map", "3") { DateTicks = when.Ticks.ToString() };
        }

        [Fact]
        public void A_resend_counts_as_answered_only_when_the_player_sent_after_its_first_send()
        {
            DateTime now = DateTime.Now;
            DateTimeOffset firstSent = new DateTimeOffset(now.AddDays(-2));

            ActivityList answered = new ActivityList();
            answered.Activities.Add(Line(ActivityState.Received, now.AddDays(-2)));
            answered.Activities.Add(Line(ActivityState.Sent, now.AddDays(-1)));
            Assert.True(Main.IsAnsweredResend(answered, Game, firstSent));

            //Sent before the turn first came: the turn never reached this player
            ActivityList earlier = new ActivityList();
            earlier.Activities.Add(Line(ActivityState.Sent, now.AddDays(-3)));
            Assert.False(Main.IsAnsweredResend(earlier, Game, firstSent));

            //Waiting, not played: the resend is the same turn
            ActivityList waiting = new ActivityList();
            waiting.Activities.Add(Line(ActivityState.Received, now.AddDays(-1)));
            Assert.False(Main.IsAnsweredResend(waiting, Game, firstSent));

            //Another game, even one whose name contains this one's
            ActivityList other = new ActivityList();
            other.Activities.Add(Line(ActivityState.Sent, now.AddDays(-1), "Big Swolterwood 1v1.asg"));
            Assert.False(Main.IsAnsweredResend(other, Game, firstSent));
            Assert.False(Main.IsAnsweredResend(new ActivityList(), Game, firstSent));
        }

        #endregion
    }
}
