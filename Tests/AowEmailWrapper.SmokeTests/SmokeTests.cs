using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using MimeKit;
using Xunit;

namespace AowEmailWrapper.SmokeTests
{
    /// <summary>
    /// Each test starts the real Wrapper and does what a player does. Every one of them guards a bug that
    /// reached players: Show on the tray menu not bringing the window back, games not starting from the
    /// tray menu, a mod's downloaded executable stopping at a hidden security prompt, the bug report
    /// window cutting off its buttons, and an arriving turn.
    /// </summary>
    public class SmokeTests
    {
        private const string GameLabel = "Smoke";
        private const string GameMenuItem = "Age of Wonders (" + GameLabel + ")";

        /// <summary>An executable that starts and exits at once, standing in for the game.</summary>
        private static readonly string StandIn = Path.Combine(Environment.SystemDirectory, "whoami.exe");

        [Fact]
        public void Show_from_the_tray_brings_the_window_on_screen_every_time()
        {
            using (AppUnderTest app = new AppUnderTest())
            {
                app.Start();
                IntPtr main = app.MainWindow();
                AppUnderTest.Until(() => !Native.IsWindowVisible(app.MainWindow()), TimeSpan.FromSeconds(20), "the Wrapper did not start in the tray:" + Environment.NewLine + app.DescribeWindows());

                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    app.DoubleClickTrayIcon();
                    AppUnderTest.Until(() => app.MainWindow() != IntPtr.Zero && Native.IsWindowVisible(app.MainWindow()) && !Native.IsIconic(app.MainWindow()),
                        TimeSpan.FromSeconds(10), $"Show #{attempt} did not bring the window up");
                    main = app.MainWindow();
                    Native.RECT bounds = Native.Rect(main);
                    Assert.True(bounds.Width >= 400 && bounds.Height >= 400, $"Show #{attempt}: the window is only {bounds.Width}x{bounds.Height}");
                    Assert.True(Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height))),
                        $"Show #{attempt}: the window is off every screen at {bounds}");

                    //Minimizing sends it back to the tray, as the player does between turns
                    Native.PostMessage(main, 0x0112, (IntPtr)0xF020, IntPtr.Zero);
                    AppUnderTest.Until(() => !Native.IsWindowVisible(app.MainWindow()), TimeSpan.FromSeconds(10), $"after Show #{attempt} the window did not go back to the tray");
                }
            }
        }

        [Fact]
        public void The_tray_menu_starts_the_game()
        {
            using (AppUnderTest app = new AppUnderTest())
            {
                app.AddGameCopy(StandIn, GameLabel);
                app.Start();

                app.ChooseFromTrayMenu(GameMenuItem);

                string exe = Path.Combine(app.GameFolder, "AoW.exe");
                AppUnderTest.Until(() => app.ReadLog().Contains("Started " + exe), TimeSpan.FromSeconds(15), "the game did not start:" + Environment.NewLine + app.ReadLog());
                Assert.DoesNotContain("Could not start", app.ReadLog());
                List<IntPtr> dialogs = app.Dialogs();
                Assert.True(dialogs.Count == 0, "a dialog appeared:" + Environment.NewLine + app.DescribeWindows());
            }
        }

        [Fact]
        public void A_game_whose_executable_came_from_a_download_starts_without_a_prompt()
        {
            using (AppUnderTest app = new AppUnderTest())
            {
                app.AddGameCopy(StandIn, GameLabel);
                //The mark of the web a mod's executable carries when it came out of a downloaded zip
                string exe = Path.Combine(app.GameFolder, "AoW.exe");
                File.WriteAllText(exe + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                app.Start();

                app.ChooseFromTrayMenu(GameMenuItem);

                AppUnderTest.Until(() => app.ReadLog().Contains("Started " + exe), TimeSpan.FromSeconds(15), "the game did not start:" + Environment.NewLine + app.ReadLog());
                //An "Open File - Security Warning" would belong to the Wrapper's process
                Thread.Sleep(3000);
                List<IntPtr> dialogs = app.Dialogs();
                Assert.True(dialogs.Count == 0, "a dialog appeared: " + string.Join(", ", dialogs.Select(Native.Text)));
            }
        }

        [Fact]
        public void An_arriving_turn_is_stored_and_recorded_as_received()
        {
            using (FakePop3Server mail = new FakePop3Server())
            using (AppUnderTest app = new AppUnderTest())
            {
                app.AddPop3Account(mail.Port);
                app.Start();
                //The first check records what is already in the mailbox as dealt with, so the turn comes after it
                AppUnderTest.Until(() => mail.Sessions >= 1, TimeSpan.FromSeconds(60), "the Wrapper did not check the mailbox:" + Environment.NewLine + mail.Log + Environment.NewLine + app.ReadLog());

                mail.Add(TurnEmail("Smoke test.asg"));
                app.ChooseFromTrayMenu("Poll now");

                string stored = Path.Combine(app.AppData, "AowEmailWrapper", "CheckEmail", "Smoke test.asg");
                AppUnderTest.Until(() => File.Exists(stored), TimeSpan.FromSeconds(30), "the turn was not stored:" + Environment.NewLine + app.ReadLog());
                AppUnderTest.Until(() => app.ReadActivityLog().Contains("file_name=\"Smoke test.asg\"") && app.ReadActivityLog().Contains("status=\"Received\""),
                    TimeSpan.FromSeconds(15), "the turn is not in the activity log:" + Environment.NewLine + app.ReadActivityLog());
            }
        }

        [Fact]
        public void The_bug_report_window_shows_all_its_controls()
        {
            using (FakePop3Server mail = new FakePop3Server())
            using (AppUnderTest app = new AppUnderTest())
            {
                //With an account the window also shows the "attach the log" check box
                app.AddPop3Account(mail.Port);
                app.Start();
                app.ShowAndSettle();

                app.SelectTab("Settings");
                app.ClickButton(app.MainWindow(), "Report a bug...");
                IntPtr dialog = IntPtr.Zero;
                AppUnderTest.Until(() => (dialog = app.Dialogs().FirstOrDefault(h => Native.Text(h) == "Report a bug")) != IntPtr.Zero, TimeSpan.FromSeconds(10), "the bug report window did not open");

                Native.RECT window = Native.Rect(dialog);
                List<IntPtr> controls = Native.Children(dialog).Where(Native.IsWindowVisible).ToList();
                foreach (IntPtr control in controls)
                {
                    Native.RECT r = Native.Rect(control);
                    Assert.True(r.Left >= window.Left && r.Top >= window.Top && r.Right <= window.Right && r.Bottom <= window.Bottom,
                        $"'{Native.Text(control)}' at {r} is outside the window {window}");
                }

                Native.RECT send = Native.Rect(controls.Single(h => Native.Text(h) == "Send"));
                Native.RECT cancel = Native.Rect(controls.Single(h => Native.Text(h) == "Cancel"));
                Native.RECT attach = Native.Rect(controls.Single(h => Native.Text(h).StartsWith("Attach", StringComparison.Ordinal)));
                Assert.True(send.Top >= attach.Bottom && cancel.Top >= attach.Bottom, $"the buttons {send} {cancel} overlap the check box {attach}");

                Native.PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static MimeMessage TurnEmail(string fileName)
        {
            MimeMessage message = new MimeMessage();
            message.From.Add(new MailboxAddress("Opponent", "opponent@example.com"));
            message.To.Add(new MailboxAddress("Player", "player@example.com"));
            message.Subject = "AoW email game (Smoke test)";
            BodyBuilder body = new BodyBuilder { TextBody = "Age of Wonders email game" };
            //Not a save the parser recognises, so it is kept in the check folder rather than a game's
            body.Attachments.Add(fileName, new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 }, new ContentType("application", "octet-stream"));
            message.Body = body.ToMessageBody();
            return message;
        }
    }
}
