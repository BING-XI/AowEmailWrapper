using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// One copy of a game reached by two paths: through a junction here, as it is through a subst
    /// drive, or under Wine through its second path to the Documents folder. The copy counts once,
    /// and a game moved from the one path to the other keeps its turn.
    /// </summary>
    public class DiskIdentityTests : IDisposable
    {
        private const string GameFile = "Test Game (Dave, Fred).asg";

        private readonly string _root;
        private readonly string _game;
        private readonly string _alias;
        private readonly string _other;
        private readonly string _checkEmail;

        public DiskIdentityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", "identity-" + Guid.NewGuid().ToString("N"));
            _game = MakeGame("Age of Wonders");
            _other = MakeGame("Age of Wonders Other");
            _alias = Path.Combine(_root, "Linked");
            MakeJunction(_alias, _game);
            _checkEmail = Path.Combine(_root, "CheckEmail");
            Directory.CreateDirectory(_checkEmail);
        }

        public void Dispose()
        {
            //The junction first and on its own, so the folder behind it is not emptied through it
            try { Directory.Delete(_alias); } catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeGame(string name)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(folder, "EmailIn"));
            Directory.CreateDirectory(Path.Combine(folder, "EmailOut"));
            Directory.CreateDirectory(Path.Combine(folder, "Save"));
            File.WriteAllBytes(Path.Combine(folder, AowGame.Aow1ExeName), new byte[] { 0x4D, 0x5A });
            return folder;
        }

        private static void MakeJunction(string link, string target)
        {
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", string.Format("/c mklink /J \"{0}\" \"{1}\"", link, target))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
            }
            Assert.True(File.Exists(Path.Combine(link, AowGame.Aow1ExeName)), "The junction for the test could not be made");
        }

        [Fact]
        public void SameFolder_IsTrueForOneFolderReachedByTwoPaths()
        {
            Assert.NotEqual(AowGame.NormalizeFolder(_game), AowGame.NormalizeFolder(_alias));

            Assert.True(DiskIdentity.SameFolder(_game, _alias));
            Assert.True(AowGame.SameFolder(_game, _alias));
            Assert.True(AowGame.SameFolder(Path.Combine(_game, "EmailIn"), Path.Combine(_alias, "EmailIn") + "\\"));
        }

        [Fact]
        public void SameFolder_IsFalseForTwoFoldersAndForOneThatIsMissing()
        {
            string missing = Path.Combine(_root, "Not there");

            Assert.False(AowGame.SameFolder(_game, _other));
            Assert.False(AowGame.SameFolder(_game, missing));
            Assert.False(DiskIdentity.SameFolder(missing, missing));
            //A folder that is gone is still the same folder as text, as it was before
            Assert.True(AowGame.SameFolder(missing, missing.ToUpperInvariant() + "\\"));
        }

        [Fact]
        public void SameFile_TellsOneFileReachedByTwoPathsFromTwoFiles()
        {
            string turn = Path.Combine(_game, "EmailIn", GameFile);
            File.WriteAllBytes(turn, new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(_other, "EmailIn", GameFile), new byte[] { 1 });

            Assert.True(DiskIdentity.SameFile(turn, Path.Combine(_alias, "EmailIn", GameFile)));
            Assert.False(DiskIdentity.SameFile(turn, Path.Combine(_other, "EmailIn", GameFile)));
            Assert.False(DiskIdentity.SameFile(turn, Path.Combine(_game, "EmailIn", "Not there.asg")));
        }

        [Fact]
        public void Detect_ListsACopyReachedByTwoPathsOnce()
        {
            List<GameInstallConfigValues> known = new List<GameInstallConfigValues>
            {
                new GameInstallConfigValues { GameType = AowGameType.Aow1, Folder = _game, Manual = true },
                new GameInstallConfigValues { GameType = AowGameType.Aow1, Folder = _alias, Manual = true },
                new GameInstallConfigValues { GameType = AowGameType.Aow1, Folder = _other, Manual = true }
            };

            //Whatever this computer has installed is found as well; only the test's folders count here
            List<AowGame> found = GameDetector.Detect(known, false)
                .Where(game => game.Folder.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Equal(2, found.Count);
            Assert.Single(found, game => game.IsFolder(_game));
            Assert.Single(found, game => game.IsFolder(_other));
            Assert.True(found.Single(game => game.IsFolder(_game)).IsFolder(_alias));
        }

        [Fact]
        public void MoveGame_KeepsTheTurnOfACopyListedUnderBothItsPaths()
        {
            //A config saved before the copy counted once still lists it twice
            List<AowGame> installs = new List<AowGame>();
            installs.Add(new AowGame(AowGameType.Aow1, _game, InstallSource.Registry));
            installs.Add(new AowGame(AowGameType.Aow1, _alias, InstallSource.Folder));
            AowGameManager manager = new AowGameManager(_checkEmail, installs, null);
            AowGame real = installs[0];
            AowGame linked = installs[1];
            Assert.NotEqual(real.Id, linked.Id);

            string turn = Path.Combine(_game, "EmailIn", GameFile);
            string save = Path.Combine(_game, "Save", "Test Game (Dave, Fred)_2.asg");
            File.WriteAllBytes(turn, new byte[] { 1 });
            File.WriteAllBytes(save, new byte[] { 2 });

            manager.MoveGame(AowGameType.Aow1, GameFile, linked);
            manager.MoveGame(AowGameType.Aow1, GameFile, real);

            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(turn));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(save));
        }

        [Fact]
        public void MoveGame_StillMovesTheTurnBetweenTwoCopies()
        {
            List<AowGame> installs = new List<AowGame>();
            installs.Add(new AowGame(AowGameType.Aow1, _alias, InstallSource.Folder));
            installs.Add(new AowGame(AowGameType.Aow1, _other, InstallSource.Folder));
            AowGameManager manager = new AowGameManager(_checkEmail, installs, null);

            File.WriteAllBytes(Path.Combine(_game, "EmailIn", GameFile), new byte[] { 1 });

            manager.MoveGame(AowGameType.Aow1, GameFile, installs[1]);

            Assert.False(File.Exists(Path.Combine(_game, "EmailIn", GameFile)));
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_other, "EmailIn", GameFile)));
        }
    }
}
