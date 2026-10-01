using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Localization;

namespace AowEmailWrapper.Controls
{
    /// <summary>
    /// The Games tab: every copy of every game the Wrapper found, with the label each copy sends
    /// its turns under and which copy is the default for turns that carry no label. Folders the
    /// detection misses can be added by hand.
    /// </summary>
    public class GamesConfig : UserControl
    {
        private const string NoGameInFolderKey = "msgAddFolderNoGame";
        private const string MissingKey = "msgInstallMissing";
        private const string NoModFoundKey = "msgNoModFound";
        private const string ScanningKey = "buttonRescanning";
        private const string DefaultMark = "✓";
        private const int ButtonPanelWidth = 113;
        private const int ButtonHeight = 42;

        private readonly Panel panelGames;
        private readonly ListView listViewGames;
        private readonly Panel panelButtons;
        private readonly Button buttonSetLabel;
        private readonly Button buttonOpenFolder;
        private readonly Button buttonSetDefaultInstall;
        private readonly Button buttonRemoveInstall;
        private GamesConfigValues _ignored = new GamesConfigValues();
        private const string IgnoreInstallKey = "msgIgnoreInstall";
        private const string IgnoreInstallsKey = "msgIgnoreInstalls";
        private readonly Button buttonRescan;
        private readonly Label lblGamesHelp;

        private List<AowGame> _games = new List<AowGame>();
        private bool _resizing;
        private bool _scanning;

        public EventHandler Config_Changed;

        /// <summary>Raised when a rescan has finished, with the list updated; the result is applied and saved by the main form.</summary>
        public EventHandler Rescanned;

        /// <summary>Supplies the detected copies; the tab works on its own copy of the list until settings are saved.</summary>
        public AowGameManager GameManager { get; set; }

        public GamesConfig()
        {
            Name = "GamesConfig";
            Padding = new Padding(5);

            lblGamesHelp = new Label();
            lblGamesHelp.Name = "lblGamesHelp";
            lblGamesHelp.Dock = DockStyle.Bottom;
            lblGamesHelp.Height = 78;
            lblGamesHelp.Padding = new Padding(0, 8, 0, 0);
            lblGamesHelp.Text = "Give each copy of a game a short label such as Vanilla, AoWx or Ziggurat. The label travels with the turns you send, so everyone in a game must use the same label. The default copy receives turns that carry no label.";

            panelGames = new Panel();
            panelGames.Name = "panelGames";
            panelGames.Dock = DockStyle.Fill;

            listViewGames = new ListView();
            listViewGames.Name = "listViewGames";
            listViewGames.Dock = DockStyle.Fill;
            listViewGames.View = View.Details;
            listViewGames.FullRowSelect = true;
            //Shift and Ctrl extend the selection so several copies can be removed together
            listViewGames.MultiSelect = true;
            listViewGames.HideSelection = false;
            listViewGames.ShowItemToolTips = true;
            listViewGames.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            listViewGames.Columns.Add(new ColumnHeader { Text = "Game", Tag = "ContentHeaderMax" });
            listViewGames.Columns.Add(new ColumnHeader { Text = "Mod", Tag = "ContentHeaderMax" });
            listViewGames.Columns.Add(new ColumnHeader { Text = "Default", Tag = "HeaderSize" });
            //The folder is last and fills the rest of the list, but never less than its longest path, so the list
            //scrolls sideways rather than cutting paths short
            listViewGames.Columns.Add(new ColumnHeader { Text = "Folder", Tag = "Fill" });
            ListViewColumnResizer.AllowUserResizing(listViewGames);
            listViewGames.SelectedIndexChanged += (sender, e) => UpdateButtons();
            //Sized on control resize only: reacting to the list's own client size changes loops when a scroll bar appears
            Resize += (sender, e) => FitColumns();
            listViewGames.MouseDoubleClick += ListViewGames_MouseDoubleClick;
            listViewGames.KeyDown += ListViewGames_KeyDown;
            listViewGames.ContextMenuStrip = BuildContextMenu();

            panelButtons = new Panel();
            panelButtons.Dock = DockStyle.Right;
            panelButtons.Width = ButtonPanelWidth;
            panelButtons.Padding = new Padding(5, 0, 0, 0);

            //Docked Top, so the last one added ends up at the top
            buttonRescan = AddButton("buttonRescan", "Rescan", (sender, e) => Rescan());
            buttonRemoveInstall = AddButton("buttonRemoveInstall", "Remove", (sender, e) => RemoveSelected());
            buttonSetDefaultInstall = AddButton("buttonSetDefaultInstall", "Set as default", (sender, e) => SetDefault());
            buttonSetLabel = AddButton("buttonSetLabel", "Set label...", (sender, e) => SetLabel());
            buttonOpenFolder = AddButton("buttonOpenFolder", "Open folder", (sender, e) => OpenFolder());
            AddButton("buttonAddFolder", "Add folder...", (sender, e) => AddFolder());

            panelGames.Controls.Add(listViewGames);
            panelGames.Controls.Add(panelButtons);

            Controls.Add(panelGames);
            Controls.Add(lblGamesHelp);

            UpdateButtons();
        }

        /// <summary>The folder, how the copy was found (or that it is missing), and the evidence behind its mod.</summary>
        private static string ToolTipFor(AowGame game)
        {
            string found = game.IsInstalled ? Translator.TranslateEnum(game.Source) : Translator.Translate(MissingKey);
            string mods = game.DetectedMods.Count == 0
                ? Translator.Translate(NoModFoundKey)
                : string.Join(Environment.NewLine, game.DetectedMods.Select(mod => mod.ToString() + ": " + mod.Evidence));
            return string.Concat(game.Folder, Environment.NewLine, found, Environment.NewLine, mods);
        }

        /// <summary>The list as config entries; setting it re-reads the detected copies from the game manager.</summary>
        public GamesConfigValues Config
        {
            get
            {
                GamesConfigValues config = new GamesConfigValues();
                foreach (AowGame game in _games)
                {
                    config.Installs.Add(new GameInstallConfigValues(game));
                }
                config.Ignored = _ignored.Clone().Ignored;
                return config;
            }
            set
            {
                IEnumerable<AowGame> source = GameManager != null
                    ? GameManager.Games
                    : new AowGameManager(null, value).Games;
                _games = source.Select(Clone).ToList();
                _ignored = value != null ? value.Clone() : new GamesConfigValues();
                _ignored.Installs.Clear();
                if (GameManager != null)
                {
                    _ignored.Ignored = GameManager.IgnoredInstalls.Select(ignored => new IgnoredInstallConfigValues { GameType = ignored.GameType, Folder = ignored.Folder }).ToList();
                }
                Populate();
            }
        }

        private Button AddButton(string name, string text, EventHandler onClick)
        {
            Panel holder = new Panel();
            holder.Dock = DockStyle.Top;
            holder.Height = ButtonHeight;
            holder.Padding = new Padding(0, 0, 0, 5);

            Button button = new Button();
            button.Name = name;
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.UseVisualStyleBackColor = true;
            button.Click += onClick;

            holder.Controls.Add(button);
            panelButtons.Controls.Add(holder);
            return button;
        }

        private static AowGame Clone(AowGame game)
        {
            AowGame copy = new AowGame(game.GameType, game.Folder, game.Source);
            copy.Label = game.Label;
            copy.IsDefault = game.IsDefault;
            return copy;
        }

        /// <summary>The one highlighted copy, for the actions that only make sense one copy at a time.</summary>
        private AowGame Selected
        {
            get { return listViewGames.SelectedItems.Count == 1 ? listViewGames.SelectedItems[0].Tag as AowGame : null; }
        }

        /// <summary>The list as the player left it, as copies, for the game manager to take over at once.</summary>
        public List<AowGame> Games
        {
            get { return _games.Select(Clone).ToList(); }
        }

        /// <summary>Every highlighted copy, in list order.</summary>
        private List<AowGame> SelectedGames
        {
            get { return listViewGames.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as AowGame).Where(game => game != null).ToList(); }
        }

        private void Populate()
        {
            HashSet<string> selected = new HashSet<string>(SelectedGames.Select(game => game.Id));

            listViewGames.BeginUpdate();
            listViewGames.Items.Clear();

            foreach (AowGame game in _games.OrderBy(game => game.GameType).ThenBy(game => game.IsDefault ? 0 : 1).ThenBy(game => game.Folder))
            {
                ListViewItem item = new ListViewItem(AowGame.DisplayNameFor(game.GameType));
                item.UseItemStyleForSubItems = false;
                ListViewItem.ListViewSubItem label = item.SubItems.Add(game.DisplayLabel);
                if (string.IsNullOrEmpty(game.Label))
                {
                    //Not a label yet: another copy already has this one, so turns cannot be routed by it
                    label.ForeColor = SystemColors.GrayText;
                }
                item.SubItems.Add(game.IsDefault ? DefaultMark : string.Empty);
                item.SubItems.Add(game.Folder);
                item.ToolTipText = ToolTipFor(game);
                item.Tag = game;
                if (!game.IsInstalled)
                {
                    item.ForeColor = SystemColors.GrayText;
                    foreach (ListViewItem.ListViewSubItem subItem in item.SubItems)
                    {
                        subItem.ForeColor = SystemColors.GrayText;
                    }
                }
                if (selected.Contains(game.Id))
                {
                    item.Selected = true;
                }
                listViewGames.Items.Add(item);
            }

            listViewGames.EndUpdate();
            FitColumns();
            UpdateButtons();
        }

        private void FitColumns()
        {
            if (_resizing)
            {
                return;
            }
            _resizing = true;
            try
            {
                listViewGames.BeginUpdate();
                ListViewColumnResizer.ResizeColumns(listViewGames);
            }
            finally
            {
                listViewGames.EndUpdate();
                _resizing = false;
            }
        }

        private void UpdateButtons()
        {
            AowGame selected = Selected;
            buttonSetLabel.Enabled = selected != null;
            buttonOpenFolder.Enabled = selected != null && System.IO.Directory.Exists(selected.Folder);
            buttonSetDefaultInstall.Enabled = selected != null && selected.IsInstalled && !selected.IsDefault;
            buttonRemoveInstall.Enabled = listViewGames.SelectedItems.Count > 0;
        }

        private void RaiseChanged()
        {
            if (Config_Changed != null)
            {
                Config_Changed(this, EventArgs.Empty);
            }
        }

        private void SetLabel()
        {
            AowGame game = Selected;
            if (game == null)
            {
                return;
            }

            Dictionary<string, string> taken = new Dictionary<string, string>();
            foreach (AowGame other in _games.Where(other => other.GameType == game.GameType && other.Id != game.Id && !string.IsNullOrEmpty(other.Label)))
            {
                taken[other.Label] = other.Folder;
            }
            string label = LabelDialog.Show(this, game, taken);
            if (label != null)
            {
                MoveLabel(_games, game, label);
                Populate();
                RaiseChanged();
            }
        }

        /// <summary>
        /// Gives the copy the label, taking it off any other copy of the same game that held it, so a
        /// label still points at exactly one copy. The copy that lost it shows what its folder holds.
        /// </summary>
        public static void MoveLabel(IEnumerable<AowGame> games, AowGame target, string label)
        {
            if (!string.IsNullOrEmpty(label))
            {
                foreach (AowGame other in games.Where(other => other != target && other.GameType == target.GameType && AowGame.SameLabel(other.Label, label)))
                {
                    other.Label = string.Empty;
                }
            }
            target.Label = label ?? string.Empty;
        }

        /// <summary>Ctrl+A highlights every copy and Delete removes the highlighted ones, as in Explorer.</summary>
        private void ListViewGames_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in listViewGames.Items)
                {
                    item.Selected = true;
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && buttonRemoveInstall.Enabled)
            {
                RemoveSelected();
                e.Handled = true;
            }
        }

        /// <summary>Double-clicking a copy opens its folder in Explorer; the actions are on the right-click menu.</summary>
        private void ListViewGames_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (listViewGames.HitTest(e.Location).Item != null)
            {
                OpenFolder();
            }
        }

        private ToolStripMenuItem _menuSetLabel;
        private ToolStripMenuItem _menuOpenFolder;
        private ToolStripMenuItem _menuSetDefault;
        private ToolStripMenuItem _menuRemove;

        private ContextMenuStrip BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            _menuSetLabel = AddMenuItem(menu, "buttonSetLabel", "Set label...", (sender, e) => SetLabel());
            _menuOpenFolder = AddMenuItem(menu, "buttonOpenFolder", "Open folder", (sender, e) => OpenFolder());
            _menuSetDefault = AddMenuItem(menu, "buttonSetDefaultInstall", "Set as default", (sender, e) => SetDefault());
            _menuRemove = AddMenuItem(menu, "buttonRemoveInstall", "Remove", (sender, e) => RemoveSelected());
            menu.Opening += (sender, e) =>
            {
                e.Cancel = listViewGames.SelectedItems.Count == 0;
                _menuSetLabel.Enabled = buttonSetLabel.Enabled;
                _menuOpenFolder.Enabled = buttonOpenFolder.Enabled;
                _menuSetDefault.Enabled = buttonSetDefaultInstall.Enabled;
                _menuRemove.Enabled = buttonRemoveInstall.Enabled;
            };
            return menu;
        }

        private static ToolStripMenuItem AddMenuItem(ContextMenuStrip menu, string key, string fallback, EventHandler onClick)
        {
            string text = Translator.Translate(key);
            ToolStripMenuItem item = new ToolStripMenuItem(string.IsNullOrEmpty(text) ? fallback : text);
            item.Click += onClick;
            menu.Items.Add(item);
            return item;
        }

        private void OpenFolder()
        {
            AowGame game = Selected;
            if (game == null || !System.IO.Directory.Exists(game.Folder))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game.Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Could not open {0}: {1}", game.Folder, ex.Message);
            }
        }

        private void SetDefault()
        {
            AowGame game = Selected;
            if (game == null || !game.IsInstalled)
            {
                return;
            }

            foreach (AowGame other in _games.Where(other => other.GameType == game.GameType))
            {
                other.IsDefault = false;
            }
            game.IsDefault = true;
            Populate();
            RaiseChanged();
        }

        private void RemoveSelected()
        {
            List<AowGame> games = SelectedGames;
            if (games.Count == 0)
            {
                return;
            }

            //Detection would find these copies again on the next start, so they are remembered as ones to leave out
            List<AowGame> detected = games.Where(game => !game.IsManual).ToList();
            if (detected.Count > 0)
            {
                string question = detected.Count == 1
                    ? Translator.Translate(IgnoreInstallKey, detected[0].Folder)
                    : string.Concat(Translator.Translate(IgnoreInstallsKey), Environment.NewLine, Environment.NewLine, string.Join(Environment.NewLine, detected.Select(game => game.Folder)));
                DialogResult answer = MessageBox.Show(this, question, Translator.Translate("Main"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    return;
                }
            }

            RemoveGames(_games, _ignored, games);
            EnsureDefaults();
            Populate();
            RaiseChanged();
        }

        /// <summary>
        /// Takes the copies off the list. A folder added by hand is simply forgotten; a copy the scan
        /// found is also remembered as one to leave out, so it stays away on the next start or rescan.
        /// </summary>
        public static void RemoveGames(List<AowGame> games, GamesConfigValues ignored, IEnumerable<AowGame> remove)
        {
            foreach (AowGame game in remove.ToList())
            {
                if (!game.IsManual)
                {
                    ignored.Ignore(game);
                }
                games.Remove(game);
            }
        }

        private void AddFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                List<AowGame> found = GameDetector.ScanFolder(dialog.SelectedPath, InstallSource.Manual);
                _ignored.Unignore(dialog.SelectedPath);
                if (found.Count == 0)
                {
                    MessageBox.Show(this, Translator.Translate(NoGameInFolderKey, dialog.SelectedPath), Translator.Translate("Main"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool added = false;
                foreach (AowGame game in found)
                {
                    AowGame existing = _games.FirstOrDefault(other => other.GameType == game.GameType && other.IsFolder(game.Folder));
                    if (existing == null)
                    {
                        _games.Add(game);
                        added = true;
                    }
                }

                if (added)
                {
                    EnsureDefaults();
                    Populate();
                    RaiseChanged();
                }
            }
        }

        /// <summary>Runs the full drive scan again in the background, keeping the labels and defaults set so far.</summary>
        private async void Rescan()
        {
            if (_scanning)
            {
                return;
            }
            _scanning = true;

            GamesConfigValues known = Config;
            string originalText = buttonRescan.Text;
            buttonRescan.Enabled = false;
            buttonRescan.Text = Translator.Translate(ScanningKey);

            try
            {
                List<AowGame> detected = await Task.Run(() => GameDetector.Detect(known.Installs, true));
                AowGameManager fresh = new AowGameManager(GameManager != null ? GameManager.CheckEmailFolder : null, detected, known);
                _games = fresh.Games.Select(Clone).ToList();
                Populate();
                //The player asked for the scan, so its result is kept at once rather than waiting for Save Settings
                if (Rescanned != null)
                {
                    Rescanned(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Rescan failed: {0}", ex);
            }
            finally
            {
                buttonRescan.Text = originalText;
                buttonRescan.Enabled = true;
                _scanning = false;
            }
        }

        private void EnsureDefaults()
        {
            foreach (AowGameType type in AowGame.AllTypes)
            {
                List<AowGame> installed = _games.Where(game => game.GameType == type && game.IsInstalled).ToList();
                if (installed.Count > 0 && !installed.Any(game => game.IsDefault))
                {
                    installed.OrderBy(game => game.Source).First().IsDefault = true;
                }
            }
        }
    }
}
