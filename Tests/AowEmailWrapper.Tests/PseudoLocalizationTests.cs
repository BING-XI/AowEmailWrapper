using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Controls;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Localization;
using AowEmailWrapper.Localization.Framework;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>Tests that change process-wide state (the current language, APPDATA) run on their own.</summary>
    [CollectionDefinition(nameof(ProcessStateCollection), DisableParallelization = true)]
    public class ProcessStateCollection
    {
    }

    /// <summary>
    /// Pseudo-localisation: a made-up language in which every English text is wrapped in ⟦ ⟧ is loaded,
    /// the Wrapper's real windows are built, and every text they show is read back. Text that went
    /// through the translator carries the markers; English that did not, whether typed into the code,
    /// left in a designer file or missing from the table, stands out. The only exceptions are data (the
    /// version, game and mod names, credits) listed below with the reason.
    /// </summary>
    [Collection(nameof(ProcessStateCollection))]
    public class PseudoLocalizationTests : IDisposable
    {
        private const string PseudoCode = "qps";
        private const string Open = "⟦";
        private const string Close = "⟧";

        //Controls that show data a translation leaves alone: people's names in the credits, the version,
        //the dedication's quotation and who wrote it, the Discord invitations, and the language picker,
        //which lists each language in its own name
        private static readonly Regex ControlNamesShowingData = new Regex(@"^(lblAuthor\d+|lblTrans\d+|lblTester\d+|lblCodeCon\d+|lblVersion|lblDedicationQuote|lblDedicationBy|lblDiscord(Zig|Aow1|Aowx|Aow2)|linkDiscord(Zig|Aow1|Aowx|Aow2))$");
        private static readonly string[] DataWords =
        {
            "Age of Wonders", "Shadow Magic", "MP Evolution", "Vanilla", "Ziggurat", "AoWx", "Evolved", "Dark Lord", "Discord",
        };

        private readonly string _root;
        private readonly string _oldAppData;

        public PseudoLocalizationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _oldAppData);
            ForgetAppDataFolders();
            DataManagerHelper.LanguagesOverride = null;
            Translator.SetLanguage(Translator.DefaultLanguageCode, LocalizationTests.Load());
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>AppDataHelper keeps the folders it found in static fields; they must follow APPDATA.</summary>
        private static void ForgetAppDataFolders()
        {
            foreach (FieldInfo field in typeof(AppDataHelper).GetFields(BindingFlags.Static | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(DirectoryInfo)))
            {
                field.SetValue(null, null);
            }
        }

        /// <summary>Points the Wrapper's settings at a folder under this test's directory, and makes sure it took.</summary>
        private void UseIsolatedAppData()
        {
            string appData = Path.Combine(_root, "AppData");
            Directory.CreateDirectory(appData);
            Environment.SetEnvironmentVariable("APPDATA", appData);
            ForgetAppDataFolders();
            Assert.StartsWith(_root, AppDataHelper.Root.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static Languages WithPseudoLanguage()
        {
            Languages languages = LocalizationTests.Load();
            Language pseudo = new Language { Code = PseudoCode, DisplayName = "Pseudo", LoopupList = new List<Lookup>() };
            foreach (Lookup lookup in LocalizationTests.English(languages).LoopupList)
            {
                pseudo.LoopupList.Add(new Lookup { Key = lookup.Key, Value = string.IsNullOrEmpty(lookup.Value) ? lookup.Value : Open + lookup.Value + Close });
            }
            languages.LanguageList.Add(pseudo);
            return languages;
        }

        /// <summary>Runs the body on an STA thread, as WinForms windows expect, and rethrows what it threw.</summary>
        private static void OnStaThread(Action body)
        {
            Exception failure = null;
            Thread thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static bool IsTranslated(string text)
        {
            string rest = Regex.Replace(text ?? string.Empty, Regex.Escape(Open) + ".*?" + Regex.Escape(Close), " ", RegexOptions.Singleline);
            foreach (string word in DataWords)
            {
                rest = rest.Replace(word, " ");
            }
            //Anything left that reads as a word is English that bypassed the translator
            return !Regex.IsMatch(rest, @"\p{L}{2,}");
        }

        private static void Check(List<string> problems, string where, string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && !IsTranslated(text))
            {
                problems.Add($"{where}: \"{text}\"");
            }
        }

        private static void CheckItems(List<string> problems, string where, ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                Check(problems, $"{where} > menu item {item.Name}", item.Text);
                if (item is ToolStripDropDownItem dropDown && dropDown.HasDropDownItems)
                {
                    CheckItems(problems, $"{where} > {item.Text}", dropDown.DropDownItems);
                }
            }
        }

        /// <summary>Reads every text a control and its children show; the contents of edit boxes are the player's data.</summary>
        private static void Walk(List<string> problems, Control control, string path)
        {
            string where = path + "/" + (string.IsNullOrEmpty(control.Name) ? control.GetType().Name : control.Name);

            bool showsData = control is TextBoxBase || control is NumericUpDown || control is DateTimePicker || ControlNamesShowingData.IsMatch(control.Name ?? string.Empty);
            if (!showsData && !(control is ComboBox) && !(control is ListView) && !(control is UserControl))
            {
                Check(problems, where, control.Text);
            }

            //The language picker lists each language in its own name
            if (control is ComboBox combo && combo.DropDownStyle == ComboBoxStyle.DropDownList && control.Parent?.Name != "fbLocalization")
            {
                foreach (object item in combo.Items)
                {
                    Check(problems, where + " item", combo.GetItemText(item));
                }
            }

            if (control is ListView list)
            {
                foreach (ColumnHeader column in list.Columns)
                {
                    Check(problems, $"{where} column {column.Index}", column.Text);
                }
            }

            if (control.ContextMenuStrip != null)
            {
                CheckItems(problems, where + " context menu", control.ContextMenuStrip.Items);
            }

            foreach (Control child in control.Controls)
            {
                Walk(problems, child, where);
            }
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static object Private(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static T Create<T>(params object[] args)
        {
            return (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args, null);
        }

        [Fact]
        public void The_main_window_and_the_tray_menu_show_no_untranslated_text()
        {
            //An isolated settings folder with one copy of the game and no account: with an account the
            //window would point the games found on this PC at the test in the registry
            UseIsolatedAppData();
            string gameFolder = Path.Combine(_root, "game");
            Directory.CreateDirectory(gameFolder);
            File.WriteAllText(Path.Combine(gameFolder, AowGame.Aow1ExeName), "Copyright (C) 1999,2000 Triumph Studios");

            Config config = new Config(true);
            config.PreferencesConfig.LanguageCode = PseudoCode;
            config.PreferencesConfig.GameWrapperDataPort = FreePort();
            config.PreferencesConfig.Theme = Theme.ClassicName;
            config.PreferencesConfig.AutoInstallUpdates = false;
            config.GamesConfig = new GamesConfigValues();
            config.GamesConfig.Installs.Add(new GameInstallConfigValues(new AowGame(AowGameType.Aow1, gameFolder, InstallSource.Manual)) { Label = "Vanilla", IsDefault = true });
            DataManagerHelper.SaveConfig(config);
            DataManagerHelper.LanguagesOverride = WithPseudoLanguage();

            List<string> problems = new List<string>();
            OnStaThread(() =>
            {
                using (Main main = new Main())
                {
                    try
                    {
                        Assert.Equal(PseudoCode, Translator.CurrentLanguageCode);
                        Check(problems, "Main title", main.Text);
                        Walk(problems, main, "Main");

                        ContextMenuStrip tray = (ContextMenuStrip)Private(main, "_contextMenu");
                        CheckItems(problems, "Tray menu", tray.Items);
                    }
                    finally
                    {
                        typeof(Main).GetMethod("StopAllPolling", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
                        typeof(Main).GetMethod("StopServer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
                        ((NotifyIcon)Private(main, "notifyIcon")).Visible = false;
                    }
                }
            });

            Assert.True(problems.Count == 0, "Text shown without going through the translator:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void The_dialogs_show_no_untranslated_text()
        {
            UseIsolatedAppData();
            Translator.SetLanguage(PseudoCode, WithPseudoLanguage());
            string gameFolder = Path.Combine(_root, "game");
            Directory.CreateDirectory(gameFolder);
            File.WriteAllText(Path.Combine(gameFolder, AowGame.Aow1ExeName), "Copyright (C) 1999,2000 Triumph Studios");
            AowGame game = new AowGame(AowGameType.Aow1, gameFolder, InstallSource.Manual) { Label = "Vanilla" };

            List<string> problems = new List<string>();
            OnStaThread(() =>
            {
                List<Form> dialogs = new List<Form>
                {
                    new AccountsCreationForm(),
                    new ServerChoiceForm(),
                    new MessageStore("player@example.com", "mail.example.com"),
                    Create<BugReportForm>((AccountConfigValues)null),
                    Create<UpdateForm>(new UpdateInfo { Tag = "v9.9.9", Name = "9.9.9", AssetName = "AowEmailWrapper-9.9.9-setup.exe", Size = 1 }),
                    Create<LabelDialog>(game.DisplayName, game.Label, LabelDialog.BuildOptions(game, new Dictionary<string, string> { { "AoWx", Path.Combine(_root, "other") } })),
                    Create<AliasDialog>(new PlayerAlias("Bob", "bob@example.com"), new AliasList(), new[] { "bob@example.com" }),
                    //The caller passes a translated caption and buttons; the message comes from the error itself
                    Create<ExceptionDialog>(Translator.Translate("Main"), new Exception(Open + "error text" + Close), MessageBoxIcon.Error, new[] { Translator.Translate("buttonOK") }),
                };

                foreach (Form dialog in dialogs)
                {
                    using (dialog)
                    {
                        string name = dialog.GetType().Name;
                        //The label dialog's title is the game's name
                        if (!(dialog is LabelDialog))
                        {
                            Check(problems, name + " title", dialog.Text);
                        }
                        Walk(problems, dialog, name);
                    }
                }
            });

            Assert.True(problems.Count == 0, "Text shown without going through the translator:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }
}
