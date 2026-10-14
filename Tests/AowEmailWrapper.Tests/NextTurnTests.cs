using System;
using System.Collections.Generic;
using System.IO;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// A double-click on the tray icon opens the waiting turn at the top of the Activity Log, and each double-click
    /// after it the next one down. A game already running cannot be told to load a turn: it is brought to the
    /// front, and when it was started without that turn, the player is told which one to load.
    /// </summary>
    public class NextTurnTests : IDisposable
    {
        private readonly string _root;

        public NextTurnTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private static Activity Turn(string fileName, ActivityState status, int minutesAgo)
        {
            Activity turn = new Activity(status, AowGameType.Aow1, fileName, "Map", "1");
            turn.DateTicks = DateTime.Now.AddMinutes(-minutesAgo).Ticks.ToString();
            return turn;
        }

        private static HashSet<string> Opened()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Waiting_turns_are_taken_newest_first_as_the_Activity_Log_shows_them()
        {
            List<Activity> log = new List<Activity>
            {
                Turn("Old.asg", ActivityState.Received, 300),
                Turn("Sent.asg", ActivityState.Sent, 1),
                Turn("New.asg", ActivityState.Received, 5),
                Turn("Put aside.asg", ActivityState.Paused, 2),
                Turn("Middle.asg", ActivityState.Received, 60),
            };

            List<Activity> waiting = Main.WaitingInListOrder(log);

            Assert.Equal(new[] { "New.asg", "Middle.asg", "Old.asg" }, waiting.ConvertAll(turn => turn.FileName));
        }

        [Fact]
        public void Each_click_opens_the_next_waiting_turn_and_then_the_top_one_again()
        {
            List<Activity> waiting = new List<Activity> { Turn("A.asg", ActivityState.Received, 1), Turn("B.asg", ActivityState.Received, 2), Turn("C.asg", ActivityState.Received, 3) };
            HashSet<string> opened = Opened();
            List<string> clicks = new List<string>();

            for (int click = 0; click < 4; click++)
            {
                Activity next = Main.NextWaitingTurn(waiting, opened);
                clicks.Add(next.FileName);
                opened.Add(next.FileName);
            }

            Assert.Equal(new[] { "A.asg", "B.asg", "C.asg", "A.asg" }, clicks);
        }

        [Fact]
        public void A_played_turn_drops_out_and_its_next_turn_counts_as_not_opened()
        {
            Activity a = Turn("A.asg", ActivityState.Received, 1);
            Activity b = Turn("B.asg", ActivityState.Received, 2);
            HashSet<string> opened = Opened();
            opened.Add("A.asg");

            //A was played and sent: B is next
            Assert.Same(b, Main.NextWaitingTurn(new List<Activity> { b }, opened));
            opened.Add("B.asg");

            //A's next turn arrives under the same file name, at the top: it has not been opened
            Assert.Same(a, Main.NextWaitingTurn(new List<Activity> { a, b }, opened));
        }

        [Fact]
        public void With_no_turn_waiting_there_is_none_to_open()
        {
            Assert.Null(Main.NextWaitingTurn(new List<Activity>(), Opened()));
        }

        [Fact]
        public void A_running_game_has_the_turn_only_when_it_was_started_with_it()
        {
            string folder = Path.Combine(_root, "game");
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(folder, AowGame.Aow1ExeName));
            AowGame game = new AowGame(AowGameType.Aow1, folder, InstallSource.Manual);
            //The same copy as a rescan or a save on the Games tab builds it afresh
            AowGame rebuilt = new AowGame(AowGameType.Aow1, folder, InstallSource.Manual);
            StartedTaskWatcher watcher = new StartedTaskWatcher(game, @"EmailIn\A.asg", null);

            Assert.True(watcher.Loads(rebuilt, @"EmailIn\a.asg"));
            Assert.False(watcher.Loads(game, @"EmailIn\B.asg"));
            Assert.False(watcher.Loads(game, null));
        }
    }
}
