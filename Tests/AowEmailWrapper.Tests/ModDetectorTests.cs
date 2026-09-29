using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>
    /// Mods are recognised from what they leave in a game folder. The markers were taken from the
    /// mods' own installers and checked against real copies on disk.
    /// </summary>
    public class ModDetectorTests : IDisposable
    {
        private readonly string _root;

        public ModDetectorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void A_stock_copy_has_no_mods_and_is_labelled_as_the_stock_game()
        {
            string folder = GameFolder("stock", resStrVersion: "Version: %s");

            Assert.Empty(ModDetector.Detect(folder));
            Assert.Empty(ModDetector.Detect(Path.Combine(_root, "missing")));
            Assert.Empty(ModDetector.Detect(null));
            Assert.Equal("Vanilla 1.36", ModDetector.LabelFor(ModDetector.Detect(folder), AowGameType.Aow1));
            Assert.Equal("Vanilla", ModDetector.LabelFor(null, AowGameType.AowSm));
        }

        [Fact]
        public void Evolved_is_recognised_from_its_version_string_when_nothing_else_is_found()
        {
            string folder = GameFolder("evolved", resStrVersion: "Version: Evolved %s");

            ModInfo mod = Assert.Single(ModDetector.Detect(folder));
            Assert.Equal(ModDetector.Evolved, mod.Name);
            Assert.Equal("Evolved", ModDetector.LabelFor(ModDetector.Detect(folder), AowGameType.Aow1));
        }

        [Theory]
        [InlineData("Ziggurat;Dark Lord", "Dark Lord")]
        [InlineData("Ziggurat;AoWx", "AoWx")]
        [InlineData("Ziggurat", "Ziggurat")]
        [InlineData("Evolved", "Evolved")]
        [InlineData("", "Vanilla 1.36")]
        public void The_most_specific_mod_decides_the_label(string found, string expected)
        {
            List<ModInfo> mods = found.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(name => new ModInfo { Name = name }).ToList();
            Assert.Equal(expected, ModDetector.LabelFor(mods, AowGameType.Aow1));
        }

        [Fact]
        public void Ziggurat_is_recognised_from_its_version_string_and_dated_by_its_readme()
        {
            string folder = GameFolder("zig", resStrVersion: "Version: Ziggurat %s");
            File.WriteAllText(Path.Combine(folder, "README.txt"), "ZIGGURAT \u2014 a total rebalance mod for Age of Wonders 1\r\nRelease 2026-09-08\r\n\r\nINSTALL\r\n", Encoding.Latin1);

            ModInfo mod = Assert.Single(ModDetector.Detect(folder));
            Assert.Equal(ModDetector.Ziggurat, mod.Name);
            Assert.Equal("2026-09-08", mod.Version);
            Assert.Equal("Ziggurat 2026-09-08", mod.ToString());
            Assert.Contains("ResStr.txt", mod.Evidence);
        }

        [Fact]
        public void An_older_Ziggurat_without_a_readme_is_still_recognised()
        {
            //The stock game's own readme must not be mistaken for the mod's
            string folder = GameFolder("oldzig", resStrVersion: "Version: Ziggurat %s");
            File.WriteAllText(Path.Combine(folder, "readme.txt"), "IPXWrapper README\r\n");

            ModInfo mod = Assert.Single(ModDetector.Detect(folder));
            Assert.Equal(ModDetector.Ziggurat, mod.Name);
            Assert.Null(mod.Version);
            Assert.Equal("Ziggurat", mod.ToString());
        }

        [Theory]
        [InlineData("ZIGGURAT - a total rebalance mod\nRelease 2026-09-08", "2026-09-08")]
        [InlineData("  ziggurat notes\r\n\r\nrelease 2025-01-31 fixes", "2025-01-31")]
        [InlineData("ZIGGURAT\nno date here", null)]
        [InlineData("IPXWrapper README\nRelease 2026-09-08", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void The_readme_release_date_is_only_read_from_a_Ziggurat_readme(string text, string expected)
        {
            Assert.Equal(expected, ModDetector.ZigguratRelease(text));
        }

        [Fact]
        public void AoWx_is_recognised_from_the_branded_executable()
        {
            string folder = GameFolder("aowx", resStrVersion: "Version: Evolved %s");
            WriteExe(Path.Combine(folder, "AoW.exe"), "Age of Wonders X mod by IniochReborn");

            ModInfo mod = Assert.Single(ModDetector.Detect(folder));
            Assert.Equal(ModDetector.AowX, mod.Name);
            Assert.Contains("AoW.exe", mod.Evidence);
        }

        [Fact]
        public void A_stock_executable_is_not_AoWx()
        {
            string folder = GameFolder("stockexe", resStrVersion: "Version: %s");
            WriteExe(Path.Combine(folder, "AoW.exe"), "Copyright (C) 1999,2000 Triumph Studios");

            Assert.Empty(ModDetector.Detect(folder));
        }

        [Fact]
        public void A_folder_with_both_mods_reports_both()
        {
            //A Ziggurat data set launched through an AoWx executable, as one real copy on this machine is
            string folder = GameFolder("mixed", resStrVersion: "Version: Ziggurat %s");
            WriteExe(Path.Combine(folder, "AoWx.exe"), "Age of Wonders X mod by IniochReborn");
            File.WriteAllText(Path.Combine(folder, "readme_DarkLordMod.txt"), "Dark Lord");

            List<string> names = ModDetector.Detect(folder).Select(mod => mod.Name).ToList();
            Assert.Equal(new[] { ModDetector.Ziggurat, ModDetector.AowX, ModDetector.DarkLord }, names);
        }

        [Fact]
        public void An_AoWx_copy_saves_into_its_X_subfolder()
        {
            string folder = GameFolder("aowxsave", resStrVersion: "Version: Evolved %s");
            WriteExe(Path.Combine(folder, "AoW.exe"), "Age of Wonders X mod by IniochReborn");
            Directory.CreateDirectory(Path.Combine(folder, "Save"));
            Directory.CreateDirectory(Path.Combine(folder, "X", "Save"));

            AowGame game = new AowGame(AowGameType.Aow1, folder, InstallSource.Manual);

            Assert.True(game.IsInstalled);
            Assert.Equal("AoWx", game.DetectedModNames);
            Assert.Equal(Path.Combine(folder, "X", "Save"), game.Save.FullName);
            Assert.Contains(game.TurnFolders, turnFolder => turnFolder.FullName == game.Save.FullName);
        }

        [Fact]
        public void An_AoWx_copy_and_its_turns_show_the_grey_dragon_and_a_stock_copy_does_not()
        {
            string aowx = GameFolder("aowxicon", resStrVersion: "Version: Evolved %s");
            WriteExe(Path.Combine(aowx, "AoW.exe"), "Age of Wonders X mod by IniochReborn");
            string stock = GameFolder("stockicon", resStrVersion: "Version: %s");
            WriteExe(Path.Combine(stock, "AoW.exe"), "Copyright (C) 1999,2000 Triumph Studios");

            //Neither copy is labelled yet: the manager labels each by its contents, and the label picks the icon
            AowGameManager manager = new AowGameManager(_root, new[]
            {
                new AowGame(AowGameType.Aow1, stock, InstallSource.Folder),
                new AowGame(AowGameType.Aow1, aowx, InstallSource.Folder),
            }, null);

            List<AowGame> installs = manager.GetInstalls(AowGameType.Aow1);
            Assert.Equal(AowGame.AowXIcon, installs.Single(game => game.IsFolder(aowx)).ImageKey);
            Assert.Equal(AowGameType.Aow1.ToString(), installs.Single(game => game.IsFolder(stock)).ImageKey);

            Activity inAowx = new Activity(ActivityState.Sent, AowGameType.Aow1, "a.asg", "Map", "1") { InstallFolder = aowx };
            Activity inStock = new Activity(ActivityState.Sent, AowGameType.Aow1, "b.asg", "Map", "1") { InstallFolder = stock };
            Activity labelOnly = new Activity(ActivityState.Received, AowGameType.Aow1, "c.asg", "Map", "1") { ModLabel = "AoW X" };
            Assert.Equal(AowGame.AowXIcon, manager.ImageKeyFor(inAowx));
            Assert.Equal(AowGameType.Aow1.ToString(), manager.ImageKeyFor(inStock));
            Assert.Equal(AowGame.AowXIcon, manager.ImageKeyFor(labelOnly));
        }

        [Theory]
        [InlineData(AowGameType.Aow1, "AoWx", AowGame.AowXIcon)]
        [InlineData(AowGameType.Aow1, "aow-x", AowGame.AowXIcon)]
        [InlineData(AowGameType.Aow1, "Ziggurat", AowGame.ZigguratIcon)]
        [InlineData(AowGameType.Aow1, "Evolved", "Aow1")]
        [InlineData(AowGameType.AowSm, "AoWx", "AowSm")]
        public void Only_an_Age_of_Wonders_1_label_of_AoWx_picks_the_grey_dragon(AowGameType type, string label, string key)
        {
            Assert.Equal(key, AowGame.ImageKeyFor(type, label));
        }

        [Fact]
        public void Every_unlabelled_copy_is_labelled_by_its_contents_once_per_label()
        {
            string first = GameFolder("label1", resStrVersion: "Version: Ziggurat %s");
            string second = GameFolder("label2", resStrVersion: "Version: Ziggurat %s");
            string stock = GameFolder("label3", resStrVersion: "Version: %s");
            string evolved = GameFolder("label4", resStrVersion: "Version: Evolved %s");
            foreach (string folder in new[] { first, second, stock, evolved })
            {
                WriteExe(Path.Combine(folder, "AoW.exe"), "Copyright (C) 1999,2000 Triumph Studios");
            }

            AowGameManager manager = new AowGameManager(_root, new[]
            {
                new AowGame(AowGameType.Aow1, first, InstallSource.Folder),
                new AowGame(AowGameType.Aow1, second, InstallSource.Folder),
                new AowGame(AowGameType.Aow1, stock, InstallSource.Folder),
                new AowGame(AowGameType.Aow1, evolved, InstallSource.Folder),
            }, null);

            List<AowGame> installs = manager.GetInstalls(AowGameType.Aow1);
            Assert.Equal("Ziggurat", installs.Single(game => game.IsFolder(first)).Label);
            Assert.Equal("Vanilla 1.36", installs.Single(game => game.IsFolder(stock)).Label);
            Assert.Equal("Evolved", installs.Single(game => game.IsFolder(evolved)).Label);

            //The second Ziggurat copy cannot share the label, but still shows what it is
            AowGame duplicate = installs.Single(game => game.IsFolder(second));
            Assert.Equal(string.Empty, duplicate.Label);
            Assert.Equal("Ziggurat", duplicate.DisplayLabel);
        }

        [Fact]
        public void A_label_the_player_set_is_kept_over_the_suggestion()
        {
            string folder = GameFolder("kept", resStrVersion: "Version: Ziggurat %s");
            WriteExe(Path.Combine(folder, "AoW.exe"), "Copyright (C) 1999,2000 Triumph Studios");
            AowGame game = new AowGame(AowGameType.Aow1, folder, InstallSource.Folder);

            GamesConfigValues config = new GamesConfigValues();
            config.Installs.Add(new GameInstallConfigValues(game) { Label = "Zig Test" });

            AowGameManager manager = new AowGameManager(_root, new[] { game }, config);
            AowGame merged = manager.GetInstalls(AowGameType.Aow1).Single();
            Assert.Equal("Zig Test", merged.Label);
            Assert.Equal("Zig Test", merged.DisplayLabel);
            Assert.Equal("Ziggurat", merged.SuggestedLabel);
        }

        [Fact]
        public void Real_copies_on_this_machine_are_recognised_when_present()
        {
            //Checked by hand against these folders; skipped on a machine without them
            string zigguratCopy = @"E:\Age of Wonders";
            string mixedCopy = @"E:\SteamLibrary\steamapps\common\Age of Wonders zig";
            string stockCopy = @"E:\SteamLibrary\steamapps\common\Age of Wonders";

            if (Directory.Exists(zigguratCopy))
            {
                Assert.Contains(ModDetector.Detect(zigguratCopy), mod => mod.Name == ModDetector.Ziggurat);
            }
            if (Directory.Exists(mixedCopy))
            {
                List<string> names = ModDetector.Detect(mixedCopy).Select(mod => mod.Name).ToList();
                Assert.Contains(ModDetector.Ziggurat, names);
                Assert.Contains(ModDetector.AowX, names);
            }
            if (Directory.Exists(stockCopy))
            {
                //The Steam release is built on the Evolved community patch
                ModInfo mod = Assert.Single(ModDetector.Detect(stockCopy));
                Assert.Equal(ModDetector.Evolved, mod.Name);
            }

            //A real AoWx install made with the Inno Setup installer: branded AoWx.exe beside a small AoW.exe launcher
            string aowxCopy = @"E:\Age of Wonders X";
            if (Directory.Exists(aowxCopy))
            {
                ModInfo mod = Assert.Single(ModDetector.Detect(aowxCopy));
                Assert.Equal(ModDetector.AowX, mod.Name);
                AowGame game = new AowGame(AowGameType.Aow1, aowxCopy, InstallSource.Folder);
                Assert.True(game.IsInstalled);
                Assert.Equal(Path.Combine(aowxCopy, "X", "Save"), game.Save.FullName);
            }
        }

        private string GameFolder(string name, string resStrVersion)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(folder, "Dict"));
            string table = "[:12:]\r\nNATIVE    = [Version: %s]\r\n[US]      = [" + resStrVersion + "]\r\n[DE]      = [Version: %s]\r\n";
            File.WriteAllText(Path.Combine(folder, "Dict", "ResStr.txt"), table, Encoding.Latin1);
            return folder;
        }

        private static void WriteExe(string path, string embedded)
        {
            byte[] noise = new byte[4096];
            new Random(3).NextBytes(noise);
            byte[] text = Encoding.ASCII.GetBytes(embedded);
            byte[] data = new byte[noise.Length * 2 + text.Length];
            Buffer.BlockCopy(noise, 0, data, 0, noise.Length);
            Buffer.BlockCopy(text, 0, data, noise.Length, text.Length);
            Buffer.BlockCopy(noise, 0, data, noise.Length + text.Length, noise.Length);
            File.WriteAllBytes(path, data);
        }
    }
}
