using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Localization;

namespace AowEmailWrapper.Controls
{
    /// <summary>Adds or edits one alias: the name a player goes by and one of their email addresses.</summary>
    public class AliasDialog : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Theme.Apply(this);
        }

        private const string AddTitleKey = "titleAddAlias";
        private const string EditTitleKey = "titleEditAlias";
        private const string NameKey = "lblAliasName";
        private const string AddressKey = "lblAliasAddress";
        private const string NoNameKey = "msgAliasNoName";
        private const string BadAddressKey = "msgAliasBadAddress";
        private const string TakenKey = "msgAliasTaken";
        private const string NoNameFallback = "Enter the name you know the player by.";
        private const string BadAddressFallback = "Enter one email address, such as player@example.com.";
        private const string TakenFallback = "{0} is already listed as {1}.";
        private const string OkKey = "buttonOK";
        private const string CancelKey = "buttonCancel";
        private const int Pad = 16;
        private const int RowHeight = 30;
        //The longest an email address can be
        private const int AddressMaxLength = 254;

        private readonly AliasList _list;
        private readonly string _replacing;
        private readonly TextBox _name;
        private readonly TextBox _address;
        private readonly Label _problem;
        private PlayerAlias _result;

        /// <summary>
        /// Returns the alias as entered, or null when cancelled. Editing passes the alias being changed;
        /// adding passes null. Names already in the list and the addresses turns have come from or gone
        /// to are offered as the player types.
        /// </summary>
        public static PlayerAlias Show(IWin32Window owner, PlayerAlias editing, AliasList list, IEnumerable<string> knownAddresses)
        {
            using (AliasDialog dialog = new AliasDialog(editing, list, knownAddresses))
            {
                dialog.ShowDialog(owner);
                return dialog._result;
            }
        }

        /// <summary>
        /// Why the entry cannot be saved, or null when it can: a name is needed, the address must be one
        /// address, and an address can belong to one name only.
        /// </summary>
        public static string Problem(string name, string address, AliasList list, string replacing)
        {
            //The English fallbacks keep a refusal from passing for "no problem" when no language is loaded
            if (string.IsNullOrWhiteSpace(name))
            {
                return Translated(Translator.Translate(NoNameKey), NoNameFallback);
            }

            string trimmed = (address ?? string.Empty).Trim();
            int at = trimmed.IndexOf('@');
            if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 || trimmed.Any(c => char.IsWhiteSpace(c) || c == ';' || c == ','))
            {
                return Translated(Translator.Translate(BadAddressKey), BadAddressFallback);
            }

            PlayerAlias holder = list != null ? list.Find(trimmed) : null;
            if (holder != null && !string.Equals(trimmed, (replacing ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Translated(Translator.Translate(TakenKey, trimmed, holder.Name), string.Format(TakenFallback, trimmed, holder.Name));
            }

            return null;
        }

        private static string Translated(string text, string fallback)
        {
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private AliasDialog(PlayerAlias editing, AliasList list, IEnumerable<string> knownAddresses)
        {
            _list = list ?? new AliasList();
            _replacing = editing != null ? editing.Address : null;

            Text = Translator.Translate(editing != null ? EditTitleKey : AddTitleKey);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            //Laid out in 96 dpi units and scaled to the screen; text widths are measured in the theme's font
            int pad = DpiHelper.Scale(Pad);
            int rowHeight = DpiHelper.Scale(RowHeight);
            int width = DpiHelper.Scale(400);
            Font measureFont = DpiHelper.MeasureFont(this);

            Label nameLabel = new Label { Text = Translator.Translate(NameKey), AutoSize = true };
            Label addressLabel = new Label { Text = Translator.Translate(AddressKey), AutoSize = true };
            int labelWidth = new[] { nameLabel, addressLabel }.Max(label => TextRenderer.MeasureText(label.Text, measureFont).Width) + DpiHelper.Scale(8);
            int boxLeft = pad + labelWidth;
            int y = pad;

            _name = new TextBox();
            _name.MaxLength = PlayerAlias.MaxNameLength;
            _name.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _name.AutoCompleteSource = AutoCompleteSource.CustomSource;
            _name.AutoCompleteCustomSource.AddRange(_list.Names.ToArray());
            PlaceRow(nameLabel, _name, pad, boxLeft, width, y);
            y += rowHeight;

            _address = new TextBox();
            _address.MaxLength = AddressMaxLength;
            _address.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _address.AutoCompleteSource = AutoCompleteSource.CustomSource;
            _address.AutoCompleteCustomSource.AddRange((knownAddresses ?? Enumerable.Empty<string>())
                .Where(known => !string.IsNullOrWhiteSpace(known))
                .Select(known => known.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
            PlaceRow(addressLabel, _address, pad, boxLeft, width, y);
            y += rowHeight;

            _problem = new Label();
            _problem.AutoSize = false;
            _problem.Location = new Point(pad, y);
            _problem.Size = new Size(width - pad * 2, DpiHelper.Scale(36));
            _problem.ForeColor = Color.DarkRed;
            Controls.Add(_problem);
            y += _problem.Height + pad / 2;

            Button ok = new Button();
            ok.Text = Translator.Translate(OkKey);
            ok.Size = DpiHelper.Scale(84, 26);
            ok.Location = new Point(width - pad - ok.Width * 2 - DpiHelper.Scale(8), y);
            ok.Click += (sender, e) =>
            {
                if (Accept())
                {
                    DialogResult = DialogResult.OK;
                }
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = Translator.Translate(CancelKey);
            cancel.Size = ok.Size;
            cancel.Location = new Point(width - pad - cancel.Width, y);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(width, y + ok.Height + pad);

            if (editing != null)
            {
                _name.Text = editing.Name;
                _address.Text = editing.Address;
            }
        }

        private void PlaceRow(Label label, TextBox box, int pad, int boxLeft, int width, int y)
        {
            label.Location = new Point(pad, y + DpiHelper.Scale(3));
            box.Location = new Point(boxLeft, y);
            box.Width = width - boxLeft - pad;
            Controls.Add(label);
            Controls.Add(box);
        }

        private bool Accept()
        {
            string problem = Problem(_name.Text, _address.Text, _list, _replacing);
            if (problem != null)
            {
                _problem.Text = problem;
                return false;
            }

            _result = new PlayerAlias(_name.Text.Trim(), _address.Text.Trim());
            return true;
        }
    }
}
