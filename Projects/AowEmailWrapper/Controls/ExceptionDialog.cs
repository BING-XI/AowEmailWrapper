using System;
using AowEmailWrapper.Helpers;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AowEmailWrapper.Localization;

namespace AowEmailWrapper.Controls
{
    /// <summary>
    /// Replacement for the retired Microsoft.ExceptionMessageBox: shows the exception chain
    /// with an expandable details pane and caller supplied buttons.
    /// </summary>
    public class ExceptionDialog : Form
    {
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Theme.Apply(this);
        }

        private const string DetailsShowKey = "buttonDetailsShow";
        private const string DetailsHideKey = "buttonDetailsHide";
        private const int Pad = 16;
        private const int ButtonHeight = 28;
        private const int DetailsHeight = 230;

        private int _collapsedHeight;
        private int _expandedHeight;

        private int _result = -1;
        private TextBox _details;
        private Button _detailsButton;

        /// <summary>
        /// Shows the dialog and returns the zero based index of the button that was clicked,
        /// or -1 if the dialog was closed some other way.
        /// </summary>
        public static int Show(IWin32Window owner, string caption, Exception ex, MessageBoxIcon icon, params string[] buttons)
        {
            using (ExceptionDialog dialog = new ExceptionDialog(caption, ex, icon, buttons))
            {
                dialog.ShowDialog(owner);
                return dialog._result;
            }
        }

        private ExceptionDialog(string caption, Exception ex, MessageBoxIcon icon, string[] buttons)
        {
            Text = caption ?? string.Empty;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            //Laid out in 96 dpi units and scaled to the screen; the message is measured in the theme's font
            int pad = DpiHelper.Scale(Pad);
            int buttonHeight = DpiHelper.Scale(ButtonHeight);
            int width = DpiHelper.Scale(520);
            Font measureFont = DpiHelper.MeasureFont(this);

            PictureBox iconBox = new PictureBox();
            iconBox.Image = GetIcon(icon);
            iconBox.SizeMode = PictureBoxSizeMode.AutoSize;
            iconBox.Location = new Point(pad, pad);
            Controls.Add(iconBox);

            //Size the message area to its text so long provider advice is never cut off
            string messageText = BuildMessage(ex);
            int messageLeft = DpiHelper.Scale(64);
            int messageWidth = width - messageLeft - pad;
            int textHeight = TextRenderer.MeasureText(messageText, measureFont, new Size(messageWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int labelHeight = Math.Min(DpiHelper.Scale(400), Math.Max(DpiHelper.Scale(48), textHeight + DpiHelper.Scale(8)));
            _collapsedHeight = pad + labelHeight + pad + buttonHeight + pad;
            _expandedHeight = _collapsedHeight + DpiHelper.Scale(DetailsHeight);
            ClientSize = new Size(width, _collapsedHeight);

            Label message = new Label();
            message.Text = messageText;
            message.Location = new Point(messageLeft, pad);
            message.Size = new Size(messageWidth, labelHeight);
            Controls.Add(message);

            _details = new TextBox();
            _details.Multiline = true;
            _details.ReadOnly = true;
            _details.ScrollBars = ScrollBars.Both;
            _details.WordWrap = false;
            _details.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            _details.Text = ex == null ? string.Empty : ex.ToString();
            _details.Location = new Point(pad, _collapsedHeight);
            _details.Size = new Size(width - 2 * pad, DpiHelper.Scale(DetailsHeight) - pad);
            _details.Visible = false;
            Controls.Add(_details);

            _detailsButton = new Button();
            _detailsButton.Text = DetailsText(false);
            //As wide as the longer of its two captions needs, which varies with the language
            Font detailsFont = Theme.Enabled ? Theme.ButtonFont(buttonHeight) : Font;
            int detailsWidth = Math.Max(TextRenderer.MeasureText(DetailsText(false), detailsFont).Width, TextRenderer.MeasureText(DetailsText(true), detailsFont).Width) + DpiHelper.Scale(24);
            _detailsButton.Size = new Size(Math.Max(DpiHelper.Scale(90), detailsWidth), buttonHeight);
            _detailsButton.Location = new Point(pad, _collapsedHeight - pad - buttonHeight);
            _detailsButton.Click += new EventHandler(DetailsButton_Click);
            Controls.Add(_detailsButton);

            int x = width - pad;
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                Button button = new Button();
                button.Text = buttons[i];
                button.Tag = i;
                Font buttonFont = Theme.Enabled ? Theme.ButtonFont(buttonHeight) : Font;
                button.Size = new Size(Math.Max(DpiHelper.Scale(90), TextRenderer.MeasureText(buttons[i], buttonFont).Width + DpiHelper.Scale(24)), buttonHeight);
                x -= button.Size.Width;
                button.Location = new Point(x, _collapsedHeight - pad - buttonHeight);
                x -= DpiHelper.Scale(8);
                button.Click += new EventHandler(Button_Click);
                Controls.Add(button);

                if (i == 0)
                {
                    AcceptButton = button;
                }
                if (i == buttons.Length - 1)
                {
                    CancelButton = button;
                }
            }
        }

        private void Button_Click(object sender, EventArgs e)
        {
            _result = (int)((Button)sender).Tag;
            Close();
        }

        private static string DetailsText(bool expanded)
        {
            string text = Translator.Translate(expanded ? DetailsHideKey : DetailsShowKey);
            return string.IsNullOrEmpty(text) ? (expanded ? "<< Details" : "Details >>") : text;
        }

        private void DetailsButton_Click(object sender, EventArgs e)
        {
            bool expand = !_details.Visible;
            _details.Visible = expand;
            _detailsButton.Text = DetailsText(expand);
            ClientSize = new Size(ClientSize.Width, expand ? _expandedHeight : _collapsedHeight);
        }

        private static string BuildMessage(Exception ex)
        {
            List<string> lines = new List<string>();
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrEmpty(current.Message) && !lines.Contains(current.Message))
                {
                    lines.Add(current.Message);
                }
            }
            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static Image GetIcon(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Question:
                    return SystemIcons.Question.ToBitmap();
                case MessageBoxIcon.Warning:
                    return SystemIcons.Warning.ToBitmap();
                case MessageBoxIcon.Information:
                    return SystemIcons.Information.ToBitmap();
                default:
                    return SystemIcons.Error.ToBitmap();
            }
        }
    }
}
