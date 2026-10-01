using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Classes;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Localization;

namespace AowEmailWrapper.Controls
{
    public delegate void ActivityListViewEventHandler(object sender, List<Activity> list);
    public delegate void ActivityMoveEventHandler(object sender, Activity activity, AowGame target);

    public partial class ActivityListView : UserControl
    {
        #region Private Members

        private ActivityList _activityLog;
        private ListViewColumnSorter _lvwColumnSorter;
        private ContextMenuStrip _contextMenu;
        private ToolStripMenuItem _resendMenuItem;
        private ToolStripMenuItem _moveToMenuItem;

        private const string Menu_Remove_Tag = "menuItemRemove";
        private const string Menu_MarkEnded_Tag = "menuItemMarkEnded";
        private const string Menu_MarkSent_Tag = "menuItemMarkSent";
        private const string Menu_Resend_Tag = "menuItemResend";
        private const string Menu_MoveTo_Tag = "menuItemMoveTo";
        private const string NewSenderKey = "activityNewSender";
        private const string NewSenderFallback = "new sender";
        private const string Menu_WhereIs_Tag = "menuItemWhereIs";
        private const string WhereIsFallback = "Who has the turn?";
        private const string HeldByKey = "activityHeldBy";
        private const string HeldByFallback = "held by {0}";
        private const string ProbablyWithKey = "activityProbablyWith";
        private const string ProbablyWithFallback = "probably with {0}";
        private ToolStripMenuItem _whereIsMenuItem;

        #endregion

        #region Public Properties

        new public ActivityListViewEventHandler OnDoubleClick;
        public ActivityListViewEventHandler OnMarkAsEnded;
        public ActivityListViewEventHandler OnResendClick;
        public ActivityListViewEventHandler OnDeleteClick;
        public EventHandler OnListChanged;
        public ActivityMoveEventHandler OnMoveTo;
        public ActivityListViewEventHandler OnWhereIs;

        /// <summary>Used to name the copy a game lives in and to offer the other copies under Move to.</summary>
        public AowGameManager GameManager { get; set; }

        public ActivityList ActivityLog
        {
            get 
            { 
                return _activityLog; 
            }
            set 
            { 
                _activityLog = value;
                Populate();
            }
        }

        /// <summary>The column widths the player has dragged, kept in the preferences between runs.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ColumnWidths
        {
            get
            {
                return ListViewColumnResizer.SavedWidths(listView);
            }
            set
            {
                ListViewColumnResizer.RestoreWidths(listView, value);
                listView.BeginUpdate();
                ListViewColumnResizer.ResizeColumns(listView);
                listView.EndUpdate();
            }
        }

        public ImageList SmallImageList
        {
            get { return listView.SmallImageList; }
            set { listView.SmallImageList = value; }
        }

        #endregion

        #region Constructors

        public ActivityListView()
        {
            InitializeComponent();
            _lvwColumnSorter = new ListViewColumnSorter();
            _lvwColumnSorter.Order = SortOrder.Descending;
            listView.ListViewItemSorter = _lvwColumnSorter;
            listView.ClientSizeChanged += new EventHandler(ActivityListView_Resize);
            ListViewColumnResizer.AllowUserResizing(listView);
            CreateContextMenu();
        }

        #endregion

        #region Public Methods

        public override void Refresh()
        {            
            Populate();
            base.Refresh();
        }

        #endregion

        #region Private Methods

        private void Populate()
        {
            listView.BeginUpdate();

            listView.Items.Clear();

            if (_activityLog != null && _activityLog.Activities != null && _activityLog.Activities.Count > 0)
            {
                foreach (Activity activity in _activityLog.Activities)
                {
                    ListViewItem item = new ListViewItem();
                    int age = GetAgeInDays(activity.DateTicks);                    
                    SetItemColour(item, activity, age);
                    
                    item.Text = activity.FileName;
                    item.ToolTipText = ToolTipFor(activity);
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, activity.MapTitle));
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, activity.TurnNumber));
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, (age > 0) ? age.ToString() : string.Empty));
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, StatusLabel(activity)));
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, CopyLabel(activity)));
                    item.SubItems.Add(new ListViewItem.ListViewSubItem(item, activity.DateTicks));

                    item.Tag = activity;

                    switch (activity.GameType)
                    {
                        case AowGameType.Aow1:
                            //A mod with its own dragon (Ziggurat, AoWx) by key, the stock game by index
                            string imageKey = ImageKeyFor(activity);
                            if (imageKey != AowGameType.Aow1.ToString())
                            {
                                item.ImageKey = imageKey;
                            }
                            else
                            {
                                item.ImageIndex = 3;
                            }
                            break;
                        case AowGameType.Aow2:
                            item.ImageIndex = 4;
                            break;
                        case AowGameType.AowSm:
                            item.ImageIndex = 5;
                            break;
                        case AowGameType.AowMpe:
                            item.ImageIndex = 6;
                            break;
                        case AowGameType.Unknown:
                            item.ImageIndex = 7;
                            break;
                    }

                    listView.Items.Add(item);
                }

                _lvwColumnSorter.SortColumn = 6;
                listView.Sort();

                ListViewColumnResizer.ResizeColumns(listView);
            }
            else
            {
                ListViewColumnResizer.ResizeColumns(listView);
            }

            listView.EndUpdate();
        }

        /// <summary>
        /// The status text, with a "new sender" tag on a received turn from an address not seen before,
        /// or the player whose wrapper says it holds a sent turn.
        /// </summary>
        private static string StatusLabel(Activity activity)
        {
            string label = activity.Status.Equals(ActivityState.None) ? string.Empty : Translator.TranslateEnum(activity.Status);
            if (activity.NewSender && activity.Status == ActivityState.Received)
            {
                string tag = Translator.Translate(NewSenderKey);
                label = string.Format("{0} ({1})", label, string.IsNullOrEmpty(tag) ? NewSenderFallback : tag);
            }
            else if (activity.Status == ActivityState.Sent && !string.IsNullOrEmpty(activity.Holder))
            {
                string holder = AliasHelper.Display(activity.Holder);
                string held = Translator.Translate(HeldByKey, holder);
                label = string.Format("{0} ({1})", label, string.IsNullOrEmpty(held) ? string.Format(HeldByFallback, holder) : held);
            }
            else if (activity.Status == ActivityState.Sent && !string.IsNullOrEmpty(activity.LikelyHolder))
            {
                string likely = AliasHelper.Display(activity.LikelyHolder);
                string probably = Translator.Translate(ProbablyWithKey, likely);
                label = string.Format("{0} ({1})", label, string.IsNullOrEmpty(probably) ? string.Format(ProbablyWithFallback, likely) : probably);
            }
            return label;
        }

        private static string ToolTipFor(Activity activity)
        {
            StringBuilder tip = new StringBuilder(activity.FileName);
            if (!string.IsNullOrEmpty(activity.Sender))
            {
                tip.Append(Environment.NewLine).Append("From: ").Append(AliasHelper.DisplayListWithAddresses(activity.Sender));
            }
            if (!string.IsNullOrEmpty(activity.Recipients))
            {
                tip.Append(Environment.NewLine).Append("To: ").Append(AliasHelper.DisplayListWithAddresses(activity.Recipients));
            }
            if (!string.IsNullOrEmpty(activity.Players))
            {
                tip.Append(Environment.NewLine).Append("Players: ").Append(AliasHelper.DisplayListWithAddresses(activity.Players));
            }
            if (!string.IsNullOrEmpty(activity.Whereabouts))
            {
                foreach (string line in activity.Whereabouts.Split(new[] { TurnQuery.WhereaboutsSeparator }, StringSplitOptions.RemoveEmptyEntries))
                {
                    tip.Append(Environment.NewLine).Append(AliasHelper.InText(line));
                }
            }
            return tip.ToString();
        }

        /// <summary>The label of the copy a game lives in, or the label it arrived with when the copy is unknown.</summary>
        private string CopyLabel(Activity activity)
        {
            if (GameManager != null && !string.IsNullOrEmpty(activity.InstallFolder))
            {
                AowGame game = GameManager.GetGameByFolder(activity.GameType, activity.InstallFolder);
                if (game != null)
                {
                    return game.Label;
                }
            }
            return activity.ModLabel ?? string.Empty;
        }

        private string ImageKeyFor(Activity activity)
        {
            return GameManager != null ? GameManager.ImageKeyFor(activity) : AowGame.ImageKeyFor(activity.GameType, activity.ModLabel);
        }

        private void RaiseListChanged()
        {
            if (OnListChanged != null)
            {
                OnListChanged(this, new EventArgs());
            }
        }

        private List<Activity> GetSelectedActivities()
        {
            List<Activity> returnVal = new List<Activity>();

            if (listView.SelectedItems.Count > 0)
            {
                foreach (ListViewItem selected in listView.SelectedItems)
                {
                    returnVal.Add((Activity)selected.Tag);
                }
            }

            return returnVal;
        }

        private void RemoveSelected(List<Activity> theActivities)
        {
            if (theActivities != null && theActivities.Count > 0)
            {
                foreach (Activity activity in theActivities)
                {
                    _activityLog.Activities.Remove(activity);
                }                
                Refresh();
                RaiseListChanged();
            }
        }

        private void MarkState(ActivityState state, List<Activity> theActivities)
        {
            if (theActivities != null && theActivities.Count > 0)
            {
                foreach (Activity activity in theActivities)
                {
                    activity.Status = state;
                }
                Refresh();
                RaiseListChanged();
            }
        }

        private void listView_DoubleClick(object sender, System.EventArgs e)
        {
            if (OnDoubleClick != null)
            {
                List<Activity> theList = GetSelectedActivities();

                if (theList != null && theList.Count == 1)
                {
                    OnDoubleClick(this, theList);
                }
            }
        }

        private void SetItemColour(ListViewItem listItem, Activity activity, int age)
        {
            switch (activity.Status)
            { 
                case ActivityState.Received:
                    listItem.BackColor = SystemColors.Info;
                    if (activity.NewSender)
                    {
                        listItem.ForeColor = Color.DarkRed;
                        listItem.Font = new Font(listItem.Font ?? SystemFonts.DefaultFont, FontStyle.Bold);
                    }
                    break;
                case ActivityState.Sent:
                    if (age >= 14 && age < 28)
                    {
                        listItem.BackColor = Color.PeachPuff;
                    }
                    else if (age >= 28)
                    {
                        listItem.BackColor = Color.MistyRose;
                    }
                    break;
                case ActivityState.Pending:
                    listItem.ForeColor = Color.Blue;
                    break;
                case ActivityState.Ended:
                    listItem.ForeColor = Color.Gray;
                    break;
            }
        }

        private int GetAgeInDays(string theTicks)
        {
            int returnVal = 0;
            long ticks;
            if (long.TryParse(theTicks, out ticks))
            {
                DateTime timeStamp = new DateTime(ticks);

                TimeSpan age = DateTime.Now.Subtract(timeStamp);
                returnVal = age.Days;
            }
            return returnVal;
        }

        private void ActivityListView_Resize(object sender, EventArgs e)
        {
            listView.BeginUpdate();
            ListViewColumnResizer.ResizeColumns(listView);
            listView.EndUpdate();
        }

        #endregion

        #region Context Menu

        private void CreateContextMenu()
        {
            _contextMenu = new ContextMenuStrip();


            EventHandler menuItemClickEvent = new EventHandler(ContextMenu_Click);
            _contextMenu = new ContextMenuStrip();

            ToolStripMenuItem remove = new ToolStripMenuItem();
            ToolStripMenuItem markEnded = new ToolStripMenuItem();
            ToolStripMenuItem markSent = new ToolStripMenuItem();
            _resendMenuItem = new ToolStripMenuItem();
            _moveToMenuItem = new ToolStripMenuItem();
            _whereIsMenuItem = new ToolStripMenuItem();

            _contextMenu.Items.AddRange(new ToolStripMenuItem[] { _resendMenuItem, _moveToMenuItem, _whereIsMenuItem, markEnded, markSent, remove });

            string whereIs = Translator.Translate(Menu_WhereIs_Tag);
            _whereIsMenuItem.Text = string.IsNullOrEmpty(whereIs) ? WhereIsFallback : whereIs;
            _whereIsMenuItem.Tag = Menu_WhereIs_Tag;
            _whereIsMenuItem.Click += menuItemClickEvent;

            _contextMenu.Opening += new System.ComponentModel.CancelEventHandler(ContextMenu_Popup);

            _resendMenuItem.Text = Translator.Translate(Menu_Resend_Tag);
            _resendMenuItem.Tag = Menu_Resend_Tag;
            _resendMenuItem.Click += menuItemClickEvent;

            _moveToMenuItem.Text = Translator.Translate(Menu_MoveTo_Tag);
            _moveToMenuItem.Tag = Menu_MoveTo_Tag;

            markEnded.Text = Translator.Translate(Menu_MarkEnded_Tag);
            markEnded.Tag = Menu_MarkEnded_Tag;
            markEnded.Click += menuItemClickEvent;

            markSent.Text = Translator.Translate(Menu_MarkSent_Tag);
            markSent.Tag = Menu_MarkSent_Tag;
            markSent.Click += menuItemClickEvent;

            remove.Text = Translator.Translate(Menu_Remove_Tag);
            remove.Tag = Menu_Remove_Tag;
            remove.Click += menuItemClickEvent;

            listView.ContextMenuStrip = _contextMenu;
        }

        private void ContextMenu_Click(object sender, EventArgs e)
        {
            List<Activity> selected = GetSelectedActivities();

            if (selected != null && selected.Count > 0)
            {
                string senderTag = ((ToolStripMenuItem)sender).Tag.ToString();

                switch (senderTag)
                {
                    case Menu_Remove_Tag:
                        RemoveSelected(selected);
                        if (OnDeleteClick != null)
                        {
                            OnDeleteClick(this, selected);
                        }
                        break;
                    case Menu_MarkEnded_Tag:
                        MarkState(ActivityState.Ended, selected);
                        if (OnMarkAsEnded != null)
                        {
                            OnMarkAsEnded(this, selected);
                        }
                        break;
                    case Menu_MarkSent_Tag:
                        MarkState(ActivityState.Sent, selected);
                        break;
                    case Menu_Resend_Tag:
                        if (OnResendClick != null)
                        {
                            OnResendClick(this, selected);
                        }
                        break;
                    case Menu_WhereIs_Tag:
                        if (OnWhereIs != null)
                        {
                            OnWhereIs(this, selected);
                        }
                        break;
                }
            }
        }

        private void ContextMenu_Popup(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool enabled = listView.SelectedItems.Count > 0;
            foreach (ToolStripMenuItem menu in _contextMenu.Items)
            {
                menu.Enabled = enabled;
            }

            bool resend = false;
            foreach (Activity activity in GetSelectedActivities())
            {
                resend = !activity.Status.Equals(ActivityState.Ended) && AowEmailWrapper.Helpers.ResendHelper.CanResend(activity.FileName);
                if (!resend)
                {
                    break;
                }
            }

            _resendMenuItem.Enabled = resend;

            //Only a turn that has left this player can be somewhere else
            _whereIsMenuItem.Enabled = enabled && GetSelectedActivities().All(activity => activity.Status.Equals(ActivityState.Sent) && TurnQuery.PlayersToAsk(activity, null).Count > 0);

            PopulateMoveTo();
        }

        /// <summary>Lists the other copies of the selected game's type; hidden when there is only one copy.</summary>
        private void PopulateMoveTo()
        {
            _moveToMenuItem.DropDownItems.Clear();
            _moveToMenuItem.Visible = false;

            List<Activity> selected = GetSelectedActivities();
            if (GameManager == null || selected.Count != 1 || selected[0].GameType.Equals(AowGameType.Unknown))
            {
                return;
            }

            Activity activity = selected[0];
            AowGame current = GameManager.GetGameForActivity(activity);

            foreach (AowGame game in GameManager.GetInstalls(activity.GameType))
            {
                if (current != null && game.Id == current.Id)
                {
                    continue;
                }

                AowGame target = game;
                ToolStripMenuItem item = new ToolStripMenuItem(game.DisplayName);
                item.ToolTipText = game.Folder;
                item.Click += (sender, e) =>
                {
                    if (OnMoveTo != null)
                    {
                        OnMoveTo(this, activity, target);
                    }
                };
                _moveToMenuItem.DropDownItems.Add(item);
            }

            _moveToMenuItem.Visible = _moveToMenuItem.DropDownItems.Count > 0;
            _moveToMenuItem.Enabled = true;
        }

        #endregion
    }
}
