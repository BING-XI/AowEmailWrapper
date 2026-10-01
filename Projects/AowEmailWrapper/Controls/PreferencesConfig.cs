using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Localization;

namespace AowEmailWrapper.Controls
{
    public partial class PreferencesConfig : UserControl
    {
        public EventHandler Config_Changed;
        /// <summary>Raised when the Look combo changes, so the window can restyle itself at once.</summary>
        public event EventHandler ThemeChanged;
        /// <summary>Raised when the player clicks "try again" in the turn server's status.</summary>
        public event EventHandler TurnServerRetry;
        private const string EmailInSelectedMessageKey = "msgEmailInSelected";
        private const string SaveSelectedMessageKey = "msgSaveSelected";
        private const string ThemeClassicKey = "themeClassic";
        private const string ThemeAgeOfWondersKey = "themeAgeOfWonders";
        private const string TurnServerOffKey = "msgTurnServerOff";
        private const string TurnServerRetryKey = "linkTurnServerRetry";
        private const string RetryLinkData = "retry";
        private PreferencesConfigValues _config;
        private bool _populating;

        public PreferencesConfig()
        {
            InitializeComponent();

            EventHandler raiseConfigChange = new EventHandler(Raise_Config_Changed);
            foreach (EmailSaveFolder value in Enum.GetValues(typeof(EmailSaveFolder)))
            {
                fbSaveFolder.AddItem(value.ToString(), Translator.TranslateEnum(value));
            }

            if (Translator.ComboBoxItems != null && Translator.ComboBoxItems.Count > 0)
            {
                Translator.ComboBoxItems.ForEach(item => fbLocalization.AddItem(item));
            }

            fbTheme.AddItem(Theme.ClassicName, Translator.Translate(ThemeClassicKey));
            fbTheme.AddItem(Theme.AgeOfWondersName, Translator.Translate(ThemeAgeOfWondersKey));
            fbTheme.SelectedValue = Theme.DefaultName;

            fbSaveFolder.SelectedIndex = 0;

            fbEmailSound.InnerCheckBox.CheckedChanged += raiseConfigChange;
            fbSentSound.InnerCheckBox.CheckedChanged += raiseConfigChange;
            fbAutostart.InnerCheckBox.CheckedChanged += raiseConfigChange;
            fbAutoInstallUpdates.InnerCheckBox.CheckedChanged += raiseConfigChange;
            fbSaveFolder.InnerComboBox.SelectedIndexChanged += raiseConfigChange;
            fbSaveFolder.InnerComboBox.SelectedIndexChanged += new EventHandler(SaveFolder_SelectedIndexChanged);
            fbCopyToEmailOut.InnerCheckBox.CheckedChanged += raiseConfigChange;
            fbLocalization.InnerComboBox.SelectedIndexChanged += raiseConfigChange;
            fbTheme.InnerComboBox.SelectedIndexChanged += raiseConfigChange;
            fbTheme.InnerComboBox.SelectedIndexChanged += (sender, e) => { if (!_populating) ThemeChanged?.Invoke(this, e); };
            fbGameWrapperDataPort.InnerTextBox.TextChanged += raiseConfigChange;
            fbTurnServerAddress.InnerTextBox.TextChanged += raiseConfigChange;
            fbHostTurnServer.InnerCheckBox.CheckedChanged += raiseConfigChange;
            linkTurnServerStatus.LinkClicked += new LinkLabelLinkClickedEventHandler(TurnServerLink_Clicked);
            SetTurnServerStatus(Translator.Translate(TurnServerOffKey), null, null);
        }

        /// <summary>
        /// Shows how hosting the turn server is going. In the text, {0} becomes the address as a link to
        /// open, {1} a "try again" link and {2} the detail, as plain text.
        /// </summary>
        public void SetTurnServerStatus(string template, string url, string detail)
        {
            string text = (template ?? string.Empty).Replace("{2}", detail ?? string.Empty);
            string retry = Translator.Translate(TurnServerRetryKey) ?? string.Empty;

            linkTurnServerStatus.Links.Clear();
            List<Tuple<int, string, object>> links = new List<Tuple<int, string, object>>();
            int urlAt = text.IndexOf("{0}", StringComparison.Ordinal);
            if (urlAt >= 0)
            {
                links.Add(Tuple.Create(urlAt, url ?? string.Empty, (object)url));
            }
            int retryAt = text.IndexOf("{1}", StringComparison.Ordinal);
            if (retryAt >= 0)
            {
                links.Add(Tuple.Create(retryAt, retry, (object)RetryLinkData));
            }

            //Replace from the end so the earlier positions stay right, then shift them by what came before
            StringBuilder built = new StringBuilder(text);
            foreach (Tuple<int, string, object> link in links.OrderByDescending(link => link.Item1))
            {
                built.Remove(link.Item1, 3).Insert(link.Item1, link.Item2);
            }
            linkTurnServerStatus.Text = built.ToString();

            int shift = 0;
            foreach (Tuple<int, string, object> link in links.OrderBy(link => link.Item1))
            {
                if (!string.IsNullOrEmpty(link.Item2) && !string.IsNullOrEmpty(link.Item3 as string))
                {
                    linkTurnServerStatus.Links.Add(link.Item1 + shift, link.Item2.Length, link.Item3);
                }
                shift += link.Item2.Length - 3;
            }
        }

        /// <summary>Fills in the address when hosting has published one and the player has not chosen another.</summary>
        public bool OfferTurnServerAddress(string address)
        {
            if (!string.IsNullOrWhiteSpace(fbTurnServerAddress.TextValue))
            {
                return false;
            }
            fbTurnServerAddress.TextValue = address;
            return true;
        }

        private void TurnServerLink_Clicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string url = e.Link.LinkData as string;
            if (url == RetryLinkData)
            {
                TurnServerRetry?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Could not open {0}: {1}", url, ex.Message);
            }
        }

        public string Prefix
        {
            get { return "Preferences"; }
        }

        public PreferencesConfigValues Config
        {
            get
            {
                Scrape();
                return _config;
            }
            set
            {
                _config = value;
                Populate();
            }
        }

        private void Scrape()
        {
            _config = new PreferencesConfigValues();

            _config.PlaySoundOnEmail = fbEmailSound.Checked;
            _config.PlaySoundOnSend = fbSentSound.Checked;
            _config.Autostart = fbAutostart.Checked;
            _config.AutoInstallUpdates = fbAutoInstallUpdates.Checked;
            _config.SaveFolder = ConfigHelper.ParseEnumString<EmailSaveFolder>(fbSaveFolder.SelectedValue);
            _config.CopyToEmailOut = fbCopyToEmailOut.Checked;
            _config.Theme = !string.IsNullOrEmpty(fbTheme.SelectedValue) ? fbTheme.SelectedValue : Theme.DefaultName;
            _config.LanguageCode = !string.IsNullOrEmpty(fbLocalization.SelectedValue) ? fbLocalization.SelectedValue : Translator.CurrentLanguageCode;
            _config.HostTurnServer = fbHostTurnServer.Checked;
            _config.TurnServerAddress = TurnServerClient.Normalise(fbTurnServerAddress.TextValue);
            if (_config.TurnServerAddress == null && !string.IsNullOrWhiteSpace(fbTurnServerAddress.TextValue))
            {
                //Not an address a Wrapper would use: keep what was typed rather than lose it, but use none
                _config.TurnServerAddress = fbTurnServerAddress.TextValue.Trim();
            }

            int testValue;
            if (int.TryParse(fbGameWrapperDataPort.TextValue, out testValue))
            {
                _config.GameWrapperDataPort = IsUnassignedPortRange(testValue) ? testValue : PreferencesConfigValues.GameWrapperDataPortDefault;
                fbGameWrapperDataPort.TextValue = _config.GameWrapperDataPort.ToString();
            }
            else
            {
                _config.GameWrapperDataPort = PreferencesConfigValues.GameWrapperDataPortDefault;
            }
        }

        private void Populate()
        {
            _populating = true;
            try
            {
                fbEmailSound.Checked = _config.PlaySoundOnEmail;
                fbSentSound.Checked = _config.PlaySoundOnSend;
                fbAutostart.Checked = _config.Autostart;
                fbAutoInstallUpdates.Checked = _config.AutoInstallUpdates;
                fbSaveFolder.SelectedValue = _config.SaveFolder.ToString();
                fbCopyToEmailOut.Checked = _config.CopyToEmailOut;
                fbLocalization.SelectedValue = _config.LanguageCode;
                fbTheme.SelectedValue = Theme.IsAgeOfWonders(_config.Theme) ? Theme.AgeOfWondersName : Theme.ClassicName;
                fbGameWrapperDataPort.TextValue = _config.GameWrapperDataPort.ToString();
                fbHostTurnServer.Checked = _config.HostTurnServer;
                fbTurnServerAddress.TextValue = _config.TurnServerAddress ?? string.Empty;
                UpdateSaveFolderTip();
            }
            finally
            {
                _populating = false;
            }
        }

        private void Raise_Config_Changed(object sender, EventArgs e)
        {
            if (Config_Changed != null)
            {
                Config_Changed(this, e);
            }
        }

        private void SaveFolder_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateSaveFolderTip();
        }

        private void UpdateSaveFolderTip()
        {
            switch (ConfigHelper.ParseEnumString<EmailSaveFolder>(fbSaveFolder.SelectedValue))
            {
                case EmailSaveFolder.EmailIn:
                    labelMessage.Text = Translator.Translate(EmailInSelectedMessageKey);
                    break;
                case EmailSaveFolder.Save:
                    labelMessage.Text = Translator.Translate(SaveSelectedMessageKey);
                    break;
            }
        }

        private bool IsUnassignedPortRange(int input)
        {
            return (input >= 49151 && input < 65535);
        }
    }
}
