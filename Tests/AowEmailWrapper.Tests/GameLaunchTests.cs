using System;
using System.IO;
using System.Threading;
using AowEmailWrapper.Classes;
using AowEmailWrapper.Games;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// The tray menu starts a game through StartedTaskWatcher. Since .NET Core a process started
    /// from a bare file name is looked up beside the Wrapper and on the PATH rather than in the
    /// working directory, so the watcher has to hand over the executable's full path.
    /// </summary>
    public class GameLaunchTests : IDisposable
    {
        private readonly string _root;

        public GameLaunchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void The_watcher_starts_the_executable_in_the_game_folder_and_reports_when_it_ends()
        {
            //A real executable that exits at once, under the game's file name, in a folder that is not on any path
            string folder = Path.Combine(_root, "game");
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(folder, AowGame.Aow1ExeName));
            AowGame game = new AowGame(AowGameType.Aow1, folder, InstallSource.Manual);
            Assert.True(game.IsInstalled);

            using (ManualResetEvent ended = new ManualResetEvent(false))
            {
                AowGameType reported = AowGameType.Unknown;
                StartedTaskWatcher watcher = new StartedTaskWatcher(game, (sender, gameType) => { reported = gameType; ended.Set(); });

                watcher.Start();

                Assert.True(ended.WaitOne(TimeSpan.FromSeconds(30)), "the started process did not end, or its end was not reported");
                Assert.Equal(AowGameType.Aow1, reported);
            }
        }
    }
}
