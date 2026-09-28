using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using Microsoft.Win32;
using Xunit;

namespace AowEmailWrapper.SmokeTests
{
    /// <summary>
    /// The real, built Wrapper running in its own process from a copy of its output folder, with its own
    /// settings folder, driven through Windows the way a player drives it.
    ///
    /// Settings folders are not the only state the Wrapper touches: with an account configured it writes
    /// the email settings of every installed game into that game's registry key, which is shared per game
    /// type, and saving settings writes the autostart entry. A test therefore uses an account or a copy of
    /// a game, never both; every copy found on this PC is ignored; and those registry areas are compared
    /// before and after, restored and reported if anything changed.
    /// </summary>
    public sealed class AppUnderTest : IDisposable
    {
        public const string MainTitle = "Age of Wonders Email Wrapper";
        private const string ExeName = "AowSmoke.exe";
        private const string GamesRegistryPath = @"Software\Triumph Studios";
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly Dictionary<string, object> _registryBefore;
        private Process _process;

        public string Root { get; }
        public string AppFolder { get; }
        public string AppData { get; }
        public string GameFolder { get; }
        public Config Config { get; }

        public AppUnderTest()
        {
            _registryBefore = SnapshotRegistry();
            Root = Path.Combine(Path.GetTempPath(), "AowEmailWrapper.SmokeTests", Guid.NewGuid().ToString("N"));
            AppFolder = Path.Combine(Root, "app");
            AppData = Path.Combine(Root, "appdata");
            GameFolder = Path.Combine(Root, "game");
            Directory.CreateDirectory(AppData);

            CopyBuiltWrapper();

            Config = new Config(true);
            Config.PreferencesConfig.Autostart = false;
            Config.PreferencesConfig.AutoInstallUpdates = false;
            Config.PreferencesConfig.PlaySoundOnEmail = false;
            Config.PreferencesConfig.PlaySoundOnSend = false;
            Config.PreferencesConfig.Theme = Theme.ClassicName;
            Config.PreferencesConfig.LanguageCode = "en";
            Config.PreferencesConfig.GameWrapperDataPort = FakePop3Server.FreePort();
            Config.GamesConfig = new GamesConfigValues();
            //Every copy of every game on this PC stays out of the test
            foreach (AowGame found in GameDetector.Detect(null, false))
            {
                Config.GamesConfig.Ignore(found);
            }
            //A known copy, even a missing one, spares the test the background scan of every drive
            Config.GamesConfig.Installs.Add(new GameInstallConfigValues { GameType = AowGameType.Aow1, Folder = Path.Combine(Root, "missing"), Manual = true, Source = InstallSource.Manual });
        }

        /// <summary>A copy of Age of Wonders whose executable is <paramref name="standIn"/>; not allowed together with an account.</summary>
        public void AddGameCopy(string standIn, string label)
        {
            Assert.True(Config.AccountsList.Accounts.Count == 0, "a game copy and an account together would write the game's registry key");
            Directory.CreateDirectory(GameFolder);
            File.Copy(standIn, Path.Combine(GameFolder, AowGame.Aow1ExeName), true);
            AowGame game = new AowGame(AowGameType.Aow1, GameFolder, InstallSource.Manual) { Label = label };
            Config.GamesConfig.Installs.Add(new GameInstallConfigValues(game) { Label = label, IsDefault = true });
        }

        /// <summary>A POP3 account on this PC's fake mail server; not allowed together with a game copy.</summary>
        public void AddPop3Account(int port)
        {
            Assert.True(Config.GamesConfig.Installs.All(install => !Directory.Exists(install.Folder)), "a game copy and an account together would write the game's registry key");
            AccountConfigValues account = new AccountConfigValues();
            account.Name = "Smoke test";
            account.PollingConfig = new PollingConfigValues();
            account.SmtpConfig = new SmtpConfigValues();
            account.PollingConfig.UsePolling = true;
            account.PollingConfig.EmailType = EmailType.POP3;
            account.PollingConfig.Server = "127.0.0.1";
            account.PollingConfig.Port = port;
            account.PollingConfig.SSLType = SSLType.None;
            account.PollingConfig.Username = "player@example.com";
            account.PollingConfig.PasswordTrue = "not a real password";
            account.PollingConfig.PollInterval = 1;
            account.SmtpConfig.SmtpServer = "127.0.0.1";
            account.SmtpConfig.Port = FakePop3Server.FreePort();
            account.SmtpConfig.EmailAddress = "player@example.com";
            account.SmtpConfig.SmtpSSLType = SSLType.None;
            account.SmtpConfig.UsePollingCredentials = true;
            Config.AccountsList.Accounts.Add(account);
            Config.AccountsList.ActiveAccountName = account.Name;
            Config.AccountsList.StartUpAccountName = account.Name;
        }

        /// <summary>The output folder of a project in this repository, for the configuration these tests were built in.</summary>
        private static string BuiltFolder(params string[] project)
        {
            string configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar).Contains("Debug") ? "Debug" : "Release";
            string repository = AppContext.BaseDirectory;
            while (repository != null && !Directory.Exists(Path.Combine(repository, "Solution")))
            {
                repository = Path.GetDirectoryName(repository.TrimEnd(Path.DirectorySeparatorChar));
            }
            Assert.NotNull(repository);
            return Path.Combine(new[] { repository }.Concat(project).Concat(new[] { "bin", configuration, "net8.0-windows" }).ToArray());
        }

        private static void CopyFolder(string from, string to)
        {
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private void CopyBuiltWrapper()
        {
            string built = BuiltFolder("Projects", "AowEmailWrapper");
            Assert.True(File.Exists(Path.Combine(built, "AowEmailWrapper.exe")), "the Wrapper has not been built: " + built);
            CopyFolder(built, AppFolder);
            //Its own name, so it does not take a running Wrapper for itself and exit
            File.Move(Path.Combine(AppFolder, "AowEmailWrapper.exe"), Path.Combine(AppFolder, ExeName));
        }

        /// <summary>A copy of Age of Wonders whose AoW.exe is the stand-in game, a window that stays open.</summary>
        public void AddStandInGameCopy(string label)
        {
            string built = BuiltFolder("Tests", "StandInGame");
            Assert.True(File.Exists(Path.Combine(built, "StandInGame.exe")), "the stand-in game has not been built: " + built);
            CopyFolder(built, GameFolder);
            //The executable may be renamed: it finds StandInGame.dll by the name built into it
            AddGameCopy(Path.Combine(GameFolder, "StandInGame.exe"), label);
        }

        /// <summary>A second start of the same executable with the same settings, as from the Start menu.</summary>
        public Process StartAgain()
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(AppFolder, ExeName)) { UseShellExecute = false, WorkingDirectory = AppFolder };
            start.Environment["APPDATA"] = AppData;
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) && !string.IsNullOrEmpty(host))
            {
                start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host);
            }
            return Process.Start(start);
        }

        /// <summary>The stand-in game's window, when the copy started from the tray is running.</summary>
        public IntPtr GameWindow()
        {
            foreach (Process process in Process.GetProcessesByName("AoW"))
            {
                using (process)
                {
                    string path;
                    try { path = process.MainModule.FileName; } catch { continue; }
                    if (path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr window = Native.TopWindows((uint)process.Id).FirstOrDefault(h => Native.Text(h) == "Stand-in game");
                        if (window != IntPtr.Zero)
                        {
                            return window;
                        }
                    }
                }
            }
            return IntPtr.Zero;
        }

        public void Start()
        {
            string configFolder = Path.Combine(AppData, "AowEmailWrapper", "Config");
            Directory.CreateDirectory(configFolder);
            FileHelper.SaveXmlFile(Path.Combine(configFolder, "config.xml"), Config);

            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(AppFolder, ExeName))
            {
                UseShellExecute = false,
                WorkingDirectory = AppFolder,
                //.NET writes an unhandled exception to standard error before it ends the process, even one that
                //happens before the Wrapper's own log can record it
                RedirectStandardError = true,
            };
            start.Environment["APPDATA"] = AppData;
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) && !string.IsNullOrEmpty(host))
            {
                start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host);
            }
            _process = Process.Start(start);
            _process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    lock (_standardError)
                    {
                        _standardError.AppendLine(e.Data);
                    }
                }
            };
            _process.BeginErrorReadLine();

            //Up once its tray icon's window exists and the main window has been created
            Until(() => NotifyWindows().Any() && MainWindow() != IntPtr.Zero, TimeSpan.FromSeconds(60), () => "the Wrapper did not start: " + StartFailureDetails());
            //and the splash screen has faded out, which it once failed to do when the main window was quicker
            Until(() => !TopWindows().Any(h => Native.IsWindowVisible(h) && Native.Text(h) == "Splash"), TimeSpan.FromSeconds(10),
                "the splash screen stayed up:" + Environment.NewLine + DescribeWindows());
        }

        public int ProcessId { get { return _process.Id; } }

        public bool HasExited { get { return _process.HasExited; } }

        #region Windows

        public IntPtr MainWindow()
        {
            return TopWindows().FirstOrDefault(h => Native.Class(h).StartsWith("WindowsForms10.Window.8", StringComparison.Ordinal) && Native.Text(h).StartsWith(MainTitle, StringComparison.Ordinal));
        }

        public IEnumerable<IntPtr> TopWindows()
        {
            return Native.TopWindows((uint)_process.Id);
        }

        private readonly StringBuilder _standardError = new StringBuilder();

        /// <summary>What the Wrapper wrote to standard error, where .NET reports an exception that ended it.</summary>
        public string StandardError
        {
            get
            {
                lock (_standardError)
                {
                    return _standardError.ToString();
                }
            }
        }

        /// <summary>Why a start did not come up: the process's fate, its windows with any dialog's text, and its log.</summary>
        private string StartFailureDetails()
        {
            StringBuilder details = new StringBuilder();
            _process.Refresh();
            details.AppendLine(_process.HasExited ? $"the process exited with code {_process.ExitCode}" : "the process is still running");
            if (_process.HasExited)
            {
                //Standard error is read asynchronously; give the last of it a moment to arrive
                _process.WaitForExit();
                string errors = StandardError;
                details.AppendLine(string.IsNullOrWhiteSpace(errors) ? "nothing on standard error" : "standard error:" + Environment.NewLine + errors);
            }
            details.AppendLine($"{Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length} process(es) named {ExeName} are running");
            if (!_process.HasExited)
            {
                foreach (IntPtr h in TopWindows())
                {
                    details.AppendLine($"  0x{h.ToInt64():X} class={Native.Class(h)} visible={Native.IsWindowVisible(h)} text='{Native.Text(h)}'");
                    if (Native.Class(h) == "#32770")
                    {
                        foreach (IntPtr child in Native.Children(h))
                        {
                            string text = Native.Text(child);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                details.AppendLine("      " + text);
                            }
                        }
                    }
                }
            }
            string log = ReadLog();
            string[] lines = log.Split('\n');
            details.AppendLine("log (last 25 lines):");
            details.Append(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 25))));
            return details.ToString();
        }

        /// <summary>Every window of the process, for failure messages.</summary>
        public string DescribeWindows()
        {
            return string.Join(Environment.NewLine, TopWindows().Select(h => $"  0x{h.ToInt64():X} class={Native.Class(h)} visible={Native.IsWindowVisible(h)} iconic={Native.IsIconic(h)} rect={Native.Rect(h)} text='{Native.Text(h)}'"));
        }

        /// <summary>Visible windows other than the main window, message boxes included.</summary>
        public List<IntPtr> Dialogs()
        {
            IntPtr main = MainWindow();
            return TopWindows().Where(h => h != main && Native.IsWindowVisible(h) && Native.Text(h).Length > 0 && (Native.Class(h) == "#32770" || Native.Class(h).StartsWith("WindowsForms10.Window.8", StringComparison.Ordinal))).ToList();
        }

        private IEnumerable<IntPtr> NotifyWindows()
        {
            return TopWindows().Where(h => Native.Class(h).StartsWith("WindowsForms10.Window.0", StringComparison.Ordinal));
        }

        private const int WM_USER = 0x0400;
        private const int TrayMessage = WM_USER + 1024;

        /// <summary>What the tray icon receives when the player double-clicks it.</summary>
        public void DoubleClickTrayIcon()
        {
            foreach (IntPtr h in NotifyWindows())
            {
                Native.PostMessage(h, TrayMessage, IntPtr.Zero, (IntPtr)0x203);
            }
        }

        /// <summary>Opens the tray menu as a right-click does and invokes the item with this text.</summary>
        public List<string> ChooseFromTrayMenu(string item)
        {
            foreach (IntPtr h in NotifyWindows())
            {
                Native.PostMessage(h, TrayMessage, IntPtr.Zero, (IntPtr)0x205);
            }
            IntPtr menu = IntPtr.Zero;
            //A menu's class carries CS_DROPSHADOW (Window.20808) only where Windows draws shadows; build
            //machines often have visual effects off, and then it is Window.808
            Until(() => (menu = TopWindows().FirstOrDefault(h => Native.IsWindowVisible(h) && IsMenuClass(Native.Class(h)))) != IntPtr.Zero,
                TimeSpan.FromSeconds(10), "the tray menu did not open:" + Environment.NewLine + DescribeWindows());

            AutomationElement root = AutomationElement.FromHandle(menu);
            List<AutomationElement> items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)).Cast<AutomationElement>().ToList();
            List<string> names = items.Select(element => element.Current.Name).ToList();
            AutomationElement target = items.FirstOrDefault(element => element.Current.Name == item);
            Assert.True(target != null, $"no tray menu item '{item}' among: {string.Join(", ", names)}");
            //Invoked off this thread: the call returns only once the click handler has run
            Task.Run(() => ((InvokePattern)target.GetCurrentPattern(InvokePattern.Pattern)).Invoke()).Wait(TimeSpan.FromSeconds(10));
            return names;
        }

        /// <summary>
        /// Shows the window from the tray and waits until it has settled: bringing it back recreates its
        /// handle once the taskbar button returns, and UI Automation needs the final one.
        /// </summary>
        public IntPtr ShowAndSettle()
        {
            DoubleClickTrayIcon();
            IntPtr last = IntPtr.Zero;
            Stopwatch stable = Stopwatch.StartNew();
            Until(() =>
            {
                IntPtr now = MainWindow();
                if (now != last || now == IntPtr.Zero || !Native.IsWindowVisible(now) || Native.IsIconic(now))
                {
                    last = now;
                    stable.Restart();
                    return false;
                }
                return stable.ElapsedMilliseconds > 1000;
            }, TimeSpan.FromSeconds(15), "the window did not come up and settle:" + Environment.NewLine + DescribeWindows());
            return last;
        }

        /// <summary>
        /// Selects a tab by its caption the way a click does: TCM_SETCURFOCUS on the tab control, which sends
        /// the notification WinForms switches pages on (UI Automation's Select moves only the header).
        /// Each index is tried until the page with that caption is the one showing.
        /// </summary>
        private static bool IsMenuClass(string name)
        {
            return name.StartsWith("WindowsForms10.Window.20808.", StringComparison.Ordinal) || name.StartsWith("WindowsForms10.Window.808.", StringComparison.Ordinal);
        }

        public void SelectTab(string name)
        {
            const int TCM_GETITEMCOUNT = 0x1304, TCM_SETCURFOCUS = 0x1330;
            IntPtr main = MainWindow();
            bool Showing() { return Native.Children(main).Any(h => Native.Text(h) == name && Native.IsWindowVisible(h) && Native.Class(h).StartsWith("WindowsForms10.Window", StringComparison.Ordinal)); }

            foreach (IntPtr tabControl in Native.Children(main).Where(h => Native.Class(h).Contains("SysTabControl32") && Native.IsWindowVisible(h)))
            {
                int count = (int)Native.SendMessage(tabControl, TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                for (int index = 0; index < count; index++)
                {
                    Native.SendMessage(tabControl, TCM_SETCURFOCUS, (IntPtr)index, IntPtr.Zero);
                    Thread.Sleep(200);
                    if (Showing())
                    {
                        return;
                    }
                }
            }
            Assert.Fail($"no tab '{name}'");
        }

        /// <summary>Clicks a button of a window, as BM_CLICK; posted, so a dialog it opens does not block the test.</summary>
        public void ClickButton(IntPtr window, string text)
        {
            //A tab page creates its controls when it is first shown, so give it a moment
            IntPtr button = IntPtr.Zero;
            Until(() => (button = Native.Children(window).FirstOrDefault(h => Native.Text(h) == text && Native.IsWindowVisible(h))) != IntPtr.Zero,
                TimeSpan.FromSeconds(10), $"no button '{text}' in '{Native.Text(window)}'; visible controls: "
                    + string.Join(" | ", Native.Children(window).Where(Native.IsWindowVisible).Select(Native.Text).Where(t => t.Length > 0)));
            Native.PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
        }

        #endregion

        #region Files

        public string ReadLog()
        {
            string path = Path.Combine(AppData, "AowEmailWrapper", "Logs", "wrapper.log");
            if (!File.Exists(path))
            {
                return string.Empty;
            }
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        public string ReadActivityLog()
        {
            string path = Path.Combine(AppData, "AowEmailWrapper", "ActivityLog", "activity.xml");
            if (!File.Exists(path))
            {
                return string.Empty;
            }
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        #endregion

        /// <summary>Like Until with a message, but the message is only put together when it fails.</summary>
        public static void Until(Func<bool> condition, TimeSpan timeout, Func<string> failure)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed >= timeout)
                {
                    Assert.Fail(failure());
                }
                Thread.Sleep(200);
            }
        }

        public static void Until(Func<bool> condition, TimeSpan timeout, string failure)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(watch.Elapsed < timeout, failure);
                Thread.Sleep(200);
            }
        }

        #region Registry guard

        private static Dictionary<string, object> SnapshotRegistry()
        {
            Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            using (RegistryKey games = Registry.CurrentUser.OpenSubKey(GamesRegistryPath))
            {
                Collect(games, GamesRegistryPath, values);
            }
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunRegistryPath))
            {
                if (run != null)
                {
                    foreach (string name in run.GetValueNames())
                    {
                        values[RunRegistryPath + "|" + name] = run.GetValue(name);
                    }
                }
            }
            return values;
        }

        private static void Collect(RegistryKey key, string path, Dictionary<string, object> values)
        {
            if (key == null)
            {
                return;
            }
            foreach (string name in key.GetValueNames())
            {
                values[path + "|" + name] = key.GetValue(name);
            }
            foreach (string sub in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(sub))
                {
                    Collect(child, path + "\\" + sub, values);
                }
            }
        }

        /// <summary>Puts back anything the Wrapper under test changed in the shared registry, and says what.</summary>
        private List<string> RestoreRegistry()
        {
            Dictionary<string, object> after = SnapshotRegistry();
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, object> value in _registryBefore)
            {
                if (!after.TryGetValue(value.Key, out object now) || !Equals(Convert.ToString(now), Convert.ToString(value.Value)))
                {
                    changes.Add($"{value.Key} was '{value.Value}', now '{now}'");
                    string[] parts = value.Key.Split('|');
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(parts[0]))
                    {
                        key.SetValue(parts[1], value.Value);
                    }
                }
            }
            foreach (string added in after.Keys.Except(_registryBefore.Keys, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add($"{added} was added");
                string[] parts = added.Split('|');
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(parts[0], true))
                {
                    key?.DeleteValue(parts[1], false);
                }
            }
            return changes;
        }

        #endregion

        public void Dispose()
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                    _process.WaitForExit(10000);
                }
                catch (InvalidOperationException)
                {
                }
            }
            _process?.Dispose();
            //Anything started from the test's folders, the stand-in game or a second start
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule.FileName.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            List<string> changes = RestoreRegistry();
            try { Directory.Delete(Root, true); } catch { }
            Assert.True(changes.Count == 0, "The Wrapper under test changed the shared registry (now restored):" + Environment.NewLine + string.Join(Environment.NewLine, changes));
        }
    }

    internal static class Native
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public override string ToString() { return $"({Left},{Top})-({Right},{Bottom})"; }
        }

        public static List<IntPtr> TopWindows(uint pid)
        {
            List<IntPtr> list = new List<IntPtr>();
            EnumWindows((h, l) => { GetWindowThreadProcessId(h, out uint owner); if (owner == pid) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static List<IntPtr> Children(IntPtr parent)
        {
            List<IntPtr> list = new List<IntPtr>();
            EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static string Text(IntPtr h)
        {
            StringBuilder text = new StringBuilder(512);
            GetWindowText(h, text, text.Capacity);
            return text.ToString();
        }

        public static string Class(IntPtr h)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(h, name, name.Capacity);
            return name.ToString();
        }

        public static RECT Rect(IntPtr h)
        {
            GetWindowRect(h, out RECT r);
            return r;
        }
    }
}
