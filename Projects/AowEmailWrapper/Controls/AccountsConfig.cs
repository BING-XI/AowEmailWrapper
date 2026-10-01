using System;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Localization;
using AowEmailWrapper.Classes;
using AowEmailWrapper.Helpers;

namespace AowEmailWrapper.Controls
{
    public delegate void AccountActivatedEventHandler(object sender, AccountConfigValues theAccount, bool dirty);

    public partial class AccountsConfig : UserControl
    {
        #region Private Members

        private AccountConfigValuesList _accountsList;
        //private AccountConfigValuesList _accountsTemplates;
        public EventHandler Config_Changed;
        public AccountActivatedEventHandler Account_Activated;
        private const string AccountsTextKey = "tabAccounts";
        private const string AccountPromptTextKey = "msgAccountPrompt";
        private const string AccountDeletePromptTextKey = "msgAccountDeletePrompt";
        private const string AccountDuplicateTextKey = "msgAccountDuplicate";
        private const string AccountActiveTextKey = "activeAccount";
        private const string PrimaryAccountTextKey = "primaryAccount";
        private const string InactiveAccountTextKey = "inactiveAccount";
        private const string ButtonActivateKey = "buttonActivate";
        private const string ButtonDeactivateKey = "buttonDeactivate";
        private const string Menu_Activate_Tag = "menuItemActivate";
        private const string Menu_Deactivate_Key = "menuItemDeactivate";
        private const string AccountStatusTemplate = "{0} ({1})";
        private const string Menu_Add_Tag = "menuItemAdd";
        private const string Menu_Remove_Tag = "menuItemRemove";
        private const string Menu_Rename_Tag = "menuItemRename";
        private const string Menu_MoveUp_Tag = "menuItemMoveUp";
        private const string Menu_MoveDown_Tag = "menuItemMoveDown";
        private const string TestTitleKey = "msgTestTitle";
        private const string TestIncomingSuccessKey = "msgTestIncomingSuccess";
        private const string TestOutgoingSuccessKey = "msgTestOutgoingSuccess";
        private const string TestOutgoingSuccessNoAuthKey = "msgTestOutgoingSuccessNoAuth";
        private const string TestFailedKey = "msgTestFailed";
        private const string TestAuthFailedKey = "msgTestAuthFailed";
        private const string ButtonOKKey = "buttonOK";

        private Font ActiveFont = null;
        private Font NormalFont = null;
        
        private bool _configChanged = false;
        private bool _binding = false;
        private AccountConfigValues _editing;

        ContextMenuStrip _contextMenu;

        #endregion

        #region Public Properties

        /// <summary>The column widths the player has dragged, kept in the preferences between runs.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ColumnWidths
        {
            get
            {
                return ListViewColumnResizer.SavedWidths(listViewAccounts);
            }
            set
            {
                ListViewColumnResizer.RestoreWidths(listViewAccounts, value);
                listViewAccounts.BeginUpdate();
                ListViewColumnResizer.ResizeColumns(listViewAccounts);
                listViewAccounts.EndUpdate();
            }
        }

        public AccountConfigValuesList Config
        {
            get
            {
                if (_configChanged)
                {
                    Scrape();
                    _configChanged = false;
                }
                return _accountsList; 
            }
            set 
            { 
                _accountsList = value;
                Populate();
                _configChanged = false;
            }
        }
        /*
        public AccountConfigValuesList AccountsTemplates
        {
            get { return _accountsTemplates; }
            set { _accountsTemplates = value; }
        }
        */
        #endregion

        #region Constructors

        public AccountsConfig()
        {
            InitializeComponent();
            ImageListLoader.Load(imageListIcons, "AccountsConfig");
            //The account's settings keep to about the width they were designed for in a wide window; the list above takes the whole width
            tabControlAccountEditor.MaximumSize = Main.FormContentMaxSize;

            ActiveFont = new Font(this.Font, FontStyle.Bold);
            NormalFont = new Font(this.Font, FontStyle.Regular);

            panelSetStartUp.Visible = false;

            CreateContextMenu();
            listViewAccounts.SelectedIndexChanged += new EventHandler(listViewAccounts_SelectedIndexChanged);
            listViewAccounts.ClientSizeChanged += new EventHandler(listViewAccounts_Resize);
            ListViewColumnResizer.AllowUserResizing(listViewAccounts);

            EventHandler raiseConfigChange = new EventHandler(Raise_Config_Changed);
            pollingConfig.Config_Changed += raiseConfigChange;
            smtpConfig.Config_Changed += raiseConfigChange;

            pollingConfig.TestRequested += new EventHandler(pollingConfig_TestRequested);
            smtpConfig.TestRequested += new EventHandler(smtpConfig_TestRequested);

            listViewAccounts.KeyDown += new KeyEventHandler(listViewAccounts_KeyDown);
        }

        #endregion

        #region Public Methods

        public override void Refresh()
        {
            base.Refresh();
            Populate();
        }

        #endregion

        #region Event Handlers

        private void buttonAdd_Click(object sender, EventArgs e)
        {
            Add();
        }

        private void buttonRemove_Click(object sender, EventArgs e)
        {
            Remove();
        }

        private void buttonRename_Click(object sender, EventArgs e)
        {
            Rename();
        }        

        private void buttonActivate_Click(object sender, EventArgs e)
        {
            ToggleActive();
        }

        /// <summary>Switches the selected account between active (watched, replies through it) and inactive.</summary>
        private void ToggleActive()
        {
            AccountConfigValues account = _accountsList == null ? null : _accountsList.GetAccountByName(GetSelectedItem());
            if (account == null)
            {
                return;
            }

            if (account.PollingConfig == null)
            {
                account.PollingConfig = new PollingConfigValues();
            }

            bool nowActive = !account.IsActive;
            account.PollingConfig.UsePolling = nowActive;

            if (account == _editing)
            {
                _binding = true;
                try { pollingConfig.ChecksForEmail = nowActive; }
                finally { _binding = false; }
            }

            Populate();
            Raise_Config_Changed();
            Raise_Account_Activated(account, true);
        }

        /// <summary>Keeps the list statuses, the Activate button and the menu in step with the accounts.</summary>
        private void RefreshStatuses()
        {
            if (_accountsList == null || _accountsList.Accounts == null)
            {
                return;
            }

            AccountConfigValues primary = _accountsList.PrimaryAccount;
            foreach (ListViewItem item in listViewAccounts.Items)
            {
                AccountConfigValues account = _accountsList.GetAccountByName(item.Tag as string);
                if (account != null)
                {
                    ApplyStatus(item, account, primary);
                }
            }

            AccountConfigValues selected = _accountsList.GetAccountByName(GetSelectedItem());
            bool selectedActive = selected != null && selected.IsActive;
            buttonActivate.Text = Translator.Translate(selectedActive ? ButtonDeactivateKey : ButtonActivateKey);

            foreach (ToolStripItem menu in _contextMenu.Items)
            {
                if (Menu_Activate_Tag.Equals(menu.Tag))
                {
                    menu.Text = Translator.Translate(selectedActive ? Menu_Deactivate_Key : Menu_Activate_Tag);
                }
            }
        }

        private void ApplyStatus(ListViewItem item, AccountConfigValues account, AccountConfigValues primary)
        {
            string status = Translator.Translate(InactiveAccountTextKey);
            if (account.IsActive)
            {
                status = account == primary ? Translator.Translate(PrimaryAccountTextKey) : Translator.Translate(AccountActiveTextKey);
            }

            item.SubItems[2].Text = status;
            item.Font = account.IsActive ? ActiveFont : NormalFont;
            item.ForeColor = account.IsActive ? SystemColors.WindowText : Color.Gray;
        }

        private void buttonSetStartUp_Click(object sender, EventArgs e)
        {
            //Kept for the designer; the first active account is the primary one
        }

        private void listViewAccounts_SelectedIndexChanged(object sender, EventArgs e)
        {
            CheckEnabled();
            EditSelectedAccount();
        }

        /// <summary>The lower tabs edit whichever account is selected in the list.</summary>
        private void EditSelectedAccount()
        {
            string name = GetSelectedItem();
            if (string.IsNullOrEmpty(name) || _accountsList == null)
            {
                return;
            }

            AccountConfigValues account = _accountsList.GetAccountByName(name);
            if (account == null || account == _editing)
            {
                return;
            }

            Scrape();
            BindEditor(account);
        }

        private void BindEditor(AccountConfigValues account)
        {
            _editing = account;
            _binding = true;
            try
            {
                if (account != null)
                {
                    pollingConfig.OAuthProvider = account.OAuthProvider;
                    smtpConfig.OAuthProvider = account.OAuthProvider;
                    pollingConfig.Config = account.PollingConfig ?? new PollingConfigValues();
                    smtpConfig.Config = account.SmtpConfig ?? new SmtpConfigValues();
                }
            }
            finally
            {
                _binding = false;
                _configChanged = false;
            }
        }

        private void MoveAccount(int delta)
        {
            AccountConfigValues account = _accountsList == null ? null : _accountsList.GetAccountByName(GetSelectedItem());
            if (account == null)
            {
                return;
            }

            int index = _accountsList.Accounts.IndexOf(account);
            int target = index + delta;
            if (target < 0 || target >= _accountsList.Accounts.Count)
            {
                return;
            }

            _accountsList.Accounts.RemoveAt(index);
            _accountsList.Accounts.Insert(target, account);
            _editing = account;
            Populate();
            Raise_Config_Changed();
        }

        private void listViewAccounts_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F2:
                    Rename();
                    break;
                case Keys.Delete:
                    Remove();
                    break;
            }
        }

        private void pollingConfig_TestRequested(object sender, EventArgs e)
        {
            PollingConfigValues polling = pollingConfig.Config;
            string oauthProvider = ActiveOAuthProvider;
            RunConnectionTest(
                () => ConnectionTester.TestIncoming(polling, oauthProvider),
                result => Translator.Translate(TestIncomingSuccessKey, result.Host, result.Username));
        }

        private void smtpConfig_TestRequested(object sender, EventArgs e)
        {
            SmtpConfigValues smtp = smtpConfig.Config;
            PollingConfigValues polling = pollingConfig.Config;
            string oauthProvider = ActiveOAuthProvider;
            RunConnectionTest(
                () => ConnectionTester.TestOutgoing(smtp, polling, oauthProvider),
                result => string.IsNullOrEmpty(result.Username)
                    ? Translator.Translate(TestOutgoingSuccessNoAuthKey, result.Host)
                    : Translator.Translate(TestOutgoingSuccessKey, result.Host, result.Username));
        }

        private string ActiveOAuthProvider
        {
            get { return (_accountsList != null && _accountsList.ActiveAccount != null) ? _accountsList.ActiveAccount.OAuthProvider : null; }
        }

        /// <summary>
        /// Runs a connection test off the UI thread and reports the outcome, with provider specific
        /// advice (app passwords and so on) when the sign-in itself was refused.
        /// </summary>
        private void RunConnectionTest(Func<ConnectionTestResult> test, Func<ConnectionTestResult, string> successMessage)
        {
            this.UseWaitCursor = true;
            this.Enabled = false;

            System.Threading.Tasks.Task.Run(test).ContinueWith(task =>
            {
                this.Enabled = true;
                this.UseWaitCursor = false;

                ConnectionTestResult result = task.IsFaulted
                    ? new ConnectionTestResult() { Error = task.Exception.GetBaseException() }
                    : task.Result;

                ShowConnectionTestResult(result, successMessage);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowConnectionTestResult(ConnectionTestResult result, Func<ConnectionTestResult, string> successMessage)
        {
            string title = Translator.Translate(TestTitleKey);

            if (result.Success)
            {
                MessageBox.Show(this, successMessage(result), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder text = new StringBuilder(Translator.Translate(TestFailedKey));

            if (result.AuthenticationFailed)
            {
                text.Append(Environment.NewLine).Append(Translator.Translate(TestAuthFailedKey));

                ProviderHint hint = ProviderHints.ForHost(result.Host);
                if (hint != null)
                {
                    text.Append(Environment.NewLine).Append(Environment.NewLine).Append(Translator.Translate(hint.MessageKey));
                }
            }

            ExceptionDialog.Show(this, title, new ApplicationException(text.ToString(), result.Error), MessageBoxIcon.Error, Translator.Translate(ButtonOKKey));
        }

        private void listViewAccounts_Resize(object sender, EventArgs e)
        {
            listViewAccounts.BeginUpdate();
            ListViewColumnResizer.ResizeColumns(listViewAccounts);
            listViewAccounts.EndUpdate();
        }

        #endregion

        #region Private Methods

        private void CheckEnabled()
        {
            bool enabled = listViewAccounts.SelectedItems.Count > 0;

            foreach (ToolStripItem menu in _contextMenu.Items)
            {
                if (!Menu_Add_Tag.Equals(menu.Tag))
                {
                    menu.Enabled = enabled;
                }
            }

            buttonRemove.Enabled = enabled;
            buttonRename.Enabled = enabled;
            buttonActivate.Enabled = enabled;
            RefreshStatuses();
        }

        private void Raise_Config_Changed()
        {
            Raise_Config_Changed(null, null);
        }

        private void Raise_Config_Changed(object sender, EventArgs e)
        {
            if (_binding)
            {
                //Filling the editor is not the user changing anything
                return;
            }
            _configChanged = true;

            //Ticking Check for email is what makes an account active; show it in the list at once
            if (_editing != null && _editing.PollingConfig != null && _editing.PollingConfig.UsePolling != pollingConfig.ChecksForEmail)
            {
                _editing.PollingConfig.UsePolling = pollingConfig.ChecksForEmail;
                RefreshStatuses();
            }
            if (Config_Changed != null)
            {
                Config_Changed(this, e);
            }
        }

        private void Raise_Account_Activated(AccountConfigValues theAccount)
        {
            Raise_Account_Activated(theAccount, false);
        }

        private void Raise_Account_Activated(AccountConfigValues theAccount, bool dirty)
        {
            if (Account_Activated != null)
            {
                Account_Activated(this, theAccount, dirty);
            }
        }

        public void Add()
        {
            AccountConfigValues theNewAccount = null;

            using (AccountsCreationForm createForm = new AccountsCreationForm())
            {
                createForm.Name = createForm.GetType().Name;

                if (createForm.ShowDialog(this).Equals(DialogResult.OK))
                {
                    theNewAccount = createForm.ChosenTemplate;
                }
            }

            if (theNewAccount != null)
            {
                theNewAccount.TemplateDomains = null; //Don't need to save this

                if (_accountsList.CheckAccountExistsByName(theNewAccount.Name))
                {
                    //That name does exist, make a new name

                    int num = 0;
                    bool success;
                    string proposedName;
                    do
                    {
                        num++;
                        proposedName = string.Format(AccountStatusTemplate, theNewAccount.Name, num);
                        success = !_accountsList.CheckAccountExistsByName(proposedName);

                    } while (!success);

                    theNewAccount.Name = proposedName;
                }

                _accountsList.Accounts.Add(theNewAccount);
                _editing = theNewAccount;
                Raise_Account_Activated(theNewAccount, true);
            }
        }

        private void Remove()
        {
            if (_accountsList != null &&
                _accountsList.Accounts != null &&
                _accountsList.Accounts.Count > 1)
            {
                AccountConfigValues theAccount = _accountsList.GetAccountByName(GetSelectedItem());

                if (theAccount != null &&
                    MessageBox.Show(Translator.Translate(AccountDeletePromptTextKey, theAccount.Name), Translator.Translate(AccountsTextKey), MessageBoxButtons.YesNo, MessageBoxIcon.Question).Equals(DialogResult.Yes))
                {
                    _accountsList.Accounts.Remove(theAccount);
                    if (_editing == theAccount)
                    {
                        _editing = null;
                    }

                    Populate();
                    Raise_Config_Changed();
                    Raise_Account_Activated(_accountsList.PrimaryAccount, true);
                }
            }
        }

        private void Rename()
        {
            if (_accountsList != null &&
                _accountsList.Accounts != null)
            {
                string beforeName = GetSelectedItem();
                string theName = beforeName;

                AccountConfigValues theAccount = _accountsList.GetAccountByName(theName);

                if (theAccount != null)
                {
                    Image accountImage = imageListIcons.Images[0];
                    string emailProviderType = !string.IsNullOrEmpty(theAccount.EmailProvider) ? theAccount.EmailProvider.ToLower() : string.Empty;

                    if (imageListIcons.Images.IndexOfKey(emailProviderType) >= 0)
                    {
                        accountImage = imageListIcons.Images[emailProviderType];
                    }

                    DialogResult dialogResult = InputBox.Show(theAccount.Name, Translator.Translate(AccountPromptTextKey), ref theName, accountImage);

                    if (!dialogResult.Equals(DialogResult.Cancel) &&
                        !string.IsNullOrEmpty(theName) &&
                        !beforeName.Equals(theName))
                    {
                        if (!_accountsList.CheckAccountExistsByName(theName))
                        {
                            if (theAccount.Equals(_accountsList.ActiveAccount))
                            {
                                _accountsList.ActiveAccountName = theName;
                            }
                            theAccount.Name = theName;

                            Populate();
                            Raise_Config_Changed();
                        }
                        else
                        {
                            MessageBox.Show(Translator.Translate(AccountDuplicateTextKey), Translator.Translate(AccountsTextKey), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
        }



        private string GetSelectedItem()
        {
            string theTag = null;

            if (listViewAccounts.SelectedItems.Count.Equals(1))
            {
                theTag = listViewAccounts.SelectedItems[0].Tag.ToString();
            }

            return theTag;
        }

        private void Populate()
        {
            if (_accountsList != null &&
                _accountsList.Accounts != null)
            {
                AccountConfigValues primary = _accountsList.PrimaryAccount;
                AccountConfigValues toEdit = (_editing != null && _accountsList.Accounts.Contains(_editing)) ? _editing : primary;
                _editing = toEdit;

                listViewAccounts.BeginUpdate();
                listViewAccounts.SelectedItems.Clear();
                listViewAccounts.Items.Clear();

                foreach (AccountConfigValues account in _accountsList.Accounts)
                {
                    ListViewItem item = CreateListItem(account, primary);
                    listViewAccounts.Items.Add(item);

                    if (account == toEdit)
                    {
                        item.Selected = true;
                        item.EnsureVisible();
                    }
                }

                ListViewColumnResizer.ResizeColumns(listViewAccounts);
                listViewAccounts.EndUpdate();

                BindEditor(toEdit);
            }
        }

        private ListViewItem CreateListItem(AccountConfigValues account, AccountConfigValues primary)
        {
            ListViewItem item = new ListViewItem();

            item.Text = account.Name;
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, account.SmtpConfig != null ? account.SmtpConfig.EmailAddress : string.Empty));
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, string.Empty));
            item.Tag = account.Name;

            ApplyStatus(item, account, primary);

            int imageIndex = 0;

            if (account.SmtpConfig != null &&
                !string.IsNullOrEmpty(account.SmtpConfig.EmailAddress))
            {
                string emailProviderType = !string.IsNullOrEmpty(account.EmailProvider) ? account.EmailProvider.ToLower() : string.Empty;
                if (!string.IsNullOrEmpty(emailProviderType))
                {
                    imageIndex = imageListIcons.Images.IndexOfKey(emailProviderType);
                }
            }

            item.ImageIndex = imageIndex >= 0 ? imageIndex : 0;

            return item;
        }

        private void Scrape()
        {
            if (_editing != null && _configChanged)
            {
                _editing.PollingConfig = pollingConfig.Config;
                _editing.SmtpConfig = smtpConfig.Config;
            }
        }

        #endregion

        #region Context Menu

        private void CreateContextMenu()
        {
            EventHandler menuItemClickEvent = new EventHandler(ContextMenu_Click);
            _contextMenu = new ContextMenuStrip();

            foreach (string tag in new[] { Menu_Add_Tag, Menu_Activate_Tag, Menu_Remove_Tag, Menu_Rename_Tag, Menu_MoveUp_Tag, Menu_MoveDown_Tag })
            {
                ToolStripMenuItem item = new ToolStripMenuItem();
                item.Text = Translator.Translate(tag);
                item.Tag = tag;
                item.Click += menuItemClickEvent;
                _contextMenu.Items.Add(item);
            }

            listViewAccounts.ContextMenuStrip = _contextMenu;
        }

        /*
        private void ContextMenu_Popup(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool enabled = listViewAccounts.SelectedItems.Count > 0;
            foreach (ToolStripMenuItem menu in _contextMenu.Items)
            {
                if (!menu.Tag.ToString().Equals(Menu_Add_Tag))
                {
                    menu.Enabled = enabled;
                }
            }
        }
        */

        private void ContextMenu_Click(object sender, EventArgs e)
        {
            switch (((ToolStripItem)sender).Tag.ToString())
            {
                case Menu_Add_Tag:
                    Add();
                    break;
                case Menu_Activate_Tag:
                    ToggleActive();
                    break;
                case Menu_Remove_Tag:
                    Remove();
                    break;
                case Menu_Rename_Tag:
                    Rename();
                    break;
                case Menu_MoveUp_Tag:
                    MoveAccount(-1);
                    break;
                case Menu_MoveDown_Tag:
                    MoveAccount(1);
                    break;
            }
        }

        #endregion

    }
}
