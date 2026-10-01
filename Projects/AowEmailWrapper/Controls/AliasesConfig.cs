using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;

namespace AowEmailWrapper.Controls
{
    /// <summary>
    /// The Aliases tab: the player's own list of the people they play with, each email address with the
    /// name the player knows its owner by. Changes take effect, and are saved, at once.
    /// </summary>
    public class AliasesConfig : UserControl
    {
        private const int ButtonPanelWidth = 113;
        private const int ButtonHeight = 42;

        private readonly Panel panelAliases;
        private readonly ListView listViewAliases;
        private readonly Panel panelButtons;
        private readonly Button buttonEditAlias;
        private readonly Button buttonRemoveAlias;
        private readonly Label lblAliasesHelp;

        private AliasList _aliases = new AliasList();
        private bool _resizing;

        /// <summary>Raised after every change; the main form saves the list and shows the names everywhere.</summary>
        public EventHandler AliasesChanged;

        /// <summary>The addresses offered while one is typed in: those turns have come from or gone to.</summary>
        public Func<IEnumerable<string>> KnownAddresses { get; set; }

        public AliasesConfig()
        {
            Name = "AliasesConfig";
            Padding = new Padding(5);

            lblAliasesHelp = new Label();
            lblAliasesHelp.Name = "lblAliasesHelp";
            lblAliasesHelp.Dock = DockStyle.Bottom;
            lblAliasesHelp.Height = 78;
            lblAliasesHelp.Padding = new Padding(0, 8, 0, 0);
            lblAliasesHelp.Text = "Give the players you play with the names you know them by. The Activity Log and the Wrapper's messages show the name instead of the email address, and a turn from an address on this list is not marked as coming from a new sender. The list stays on this PC.";

            panelAliases = new Panel();
            panelAliases.Name = "panelAliases";
            panelAliases.Dock = DockStyle.Fill;

            listViewAliases = new ListView();
            listViewAliases.Name = "listViewAliases";
            listViewAliases.Dock = DockStyle.Fill;
            listViewAliases.View = View.Details;
            listViewAliases.FullRowSelect = true;
            listViewAliases.MultiSelect = true;
            listViewAliases.HideSelection = false;
            listViewAliases.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            listViewAliases.Columns.Add(new ColumnHeader { Text = "Name", Tag = "ContentHeaderMax" });
            listViewAliases.Columns.Add(new ColumnHeader { Text = "Email address", Tag = "Fill" });
            ListViewColumnResizer.AllowUserResizing(listViewAliases);
            listViewAliases.SelectedIndexChanged += (sender, e) => UpdateButtons();
            //Sized on control resize only: reacting to the list's own client size changes loops when a scroll bar appears
            Resize += (sender, e) => FitColumns();
            listViewAliases.MouseDoubleClick += (sender, e) => EditSelected();
            listViewAliases.KeyDown += ListViewAliases_KeyDown;

            panelButtons = new Panel();
            panelButtons.Dock = DockStyle.Right;
            panelButtons.Width = ButtonPanelWidth;
            panelButtons.Padding = new Padding(5, 0, 0, 0);

            //Docked Top, so the last one added ends up at the top
            buttonRemoveAlias = AddButton("buttonRemoveAlias", "Remove", (sender, e) => RemoveSelected());
            buttonEditAlias = AddButton("buttonEditAlias", "Edit...", (sender, e) => EditSelected());
            AddButton("buttonAddAlias", "Add...", (sender, e) => Add());

            panelAliases.Controls.Add(listViewAliases);
            panelAliases.Controls.Add(panelButtons);

            Controls.Add(panelAliases);
            Controls.Add(lblAliasesHelp);

            UpdateButtons();
        }

        /// <summary>A copy of the list as it stands; setting it shows the list given.</summary>
        public AliasList Aliases
        {
            get { return _aliases.Clone(); }
            set
            {
                _aliases = value != null ? value.Clone() : new AliasList();
                Populate(null);
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

        /// <summary>The one highlighted alias, for editing.</summary>
        private PlayerAlias Selected
        {
            get { return listViewAliases.SelectedItems.Count == 1 ? listViewAliases.SelectedItems[0].Tag as PlayerAlias : null; }
        }

        /// <summary>Shows the list, by name and then address, with the alias for <paramref name="select"/> highlighted.</summary>
        private void Populate(string select)
        {
            listViewAliases.BeginUpdate();
            listViewAliases.Items.Clear();

            foreach (PlayerAlias alias in _aliases.Aliases
                .Where(alias => alias != null)
                .OrderBy(alias => alias.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(alias => alias.Address, StringComparer.OrdinalIgnoreCase))
            {
                ListViewItem item = new ListViewItem(alias.Name);
                item.SubItems.Add(alias.Address);
                item.Tag = alias;
                if (select != null && string.Equals(alias.Address, select, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.Focused = true;
                }
                listViewAliases.Items.Add(item);
            }

            listViewAliases.EndUpdate();
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
                listViewAliases.BeginUpdate();
                ListViewColumnResizer.ResizeColumns(listViewAliases);
            }
            finally
            {
                listViewAliases.EndUpdate();
                _resizing = false;
            }
        }

        private void UpdateButtons()
        {
            buttonEditAlias.Enabled = Selected != null;
            buttonRemoveAlias.Enabled = listViewAliases.SelectedItems.Count > 0;
        }

        private IEnumerable<string> Known()
        {
            return KnownAddresses != null ? KnownAddresses() : Enumerable.Empty<string>();
        }

        private void Add()
        {
            PlayerAlias added = AliasDialog.Show(this, null, _aliases, Known());
            if (added != null)
            {
                _aliases.Set(added.Name, added.Address, null);
                Changed(added.Address);
            }
        }

        private void EditSelected()
        {
            PlayerAlias alias = Selected;
            if (alias == null)
            {
                return;
            }

            PlayerAlias edited = AliasDialog.Show(this, alias, _aliases, Known());
            if (edited != null)
            {
                _aliases.Set(edited.Name, edited.Address, alias.Address);
                Changed(edited.Address);
            }
        }

        private void RemoveSelected()
        {
            List<PlayerAlias> selected = listViewAliases.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as PlayerAlias).Where(alias => alias != null).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            selected.ForEach(alias => _aliases.Remove(alias.Address));
            Changed(null);
        }

        private void Changed(string select)
        {
            Populate(select);
            if (AliasesChanged != null)
            {
                AliasesChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Ctrl+A highlights every alias, Delete removes the highlighted ones and Enter edits one, as in Explorer.</summary>
        private void ListViewAliases_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in listViewAliases.Items)
                {
                    item.Selected = true;
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && buttonRemoveAlias.Enabled)
            {
                RemoveSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && buttonEditAlias.Enabled)
            {
                EditSelected();
                e.Handled = true;
            }
        }
    }
}
