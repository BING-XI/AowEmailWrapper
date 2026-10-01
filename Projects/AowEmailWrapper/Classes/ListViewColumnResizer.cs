using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using AowEmailWrapper.Helpers;

namespace AowEmailWrapper.Classes
{
    public enum ColumnHeaderResizeStyle
    {
        None,
        ColumnContent,
        HeaderSize,
        ContentHeaderMax,
        Fixed,
        Fill
    }

    /// <summary>
    /// Sizes the columns of the Wrapper's lists from their tags: Fill takes the room the others leave (and never
    /// less than its own text needs), Fixed;N is N pixels wide, HeaderSize fits the heading, ContentHeaderMax the
    /// heading or the widest row, ColumnContent the widest row, and Fixed;0 is a hidden column.
    ///
    /// The list always spans its full width: the fill column gives or takes whatever room the others leave. When
    /// the room runs out, the widest columns sized to their text are squeezed first, down to a width that still
    /// reads; only then does the list scroll sideways.
    /// </summary>
    public static class ListViewColumnResizer
    {
        private const char SplitChar = ';';
        private const string HiddenTag = "Fixed;0";
        private const string UserWidthTemplate = "Fixed;{0}";
        /// <summary>A dragged column never gets narrower than this, so it cannot vanish and become unreachable.</summary>
        private const int MinimumUserWidth = 24;
        private const int MaximumSavedWidth = 4000;
        private const char SavedSeparator = '|';
        //In 96 dpi pixels: the least the fill column is given, how far a column sized to its text may be squeezed,
        //and the room a heading needs around its text
        private const int MinimumFillWidth = 60;
        private const int SqueezedWidth = 160;
        private const int HeaderPadding = 8;
        private static readonly Regex SavedTag = new Regex(@"^(Fixed|Fill);(\d{1,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>What the resizer keeps for each list.</summary>
        private sealed class ListState
        {
            /// <summary>The column tags as designed, to tell the widths the player has dragged from the automatic ones.</summary>
            public string[] Designed;

            /// <summary>
            /// The width the resizer last gave each column. The list reports these back as changes when its window is
            /// created, which must not be taken for the player dragging a column.
            /// </summary>
            public readonly Dictionary<ColumnHeader, int> Applied = new Dictionary<ColumnHeader, int>();

            /// <summary>The least width the fill column needs, from the last full resize.</summary>
            public int FillLeast;

            /// <summary>The column whose edge the player is dragging, or -1.</summary>
            public int Tracking = -1;
        }

        private static readonly ConditionalWeakTable<ListView, ListState> _states = new ConditionalWeakTable<ListView, ListState>();

        [ThreadStatic]
        private static int _applying;

        /// <summary>
        /// Lets the player drag column edges. A column the player has sized keeps that width through later
        /// automatic resizes (it becomes a Fixed column) and the fill column gives or takes the difference as the
        /// edge moves. The fill column itself and a hidden column cannot be dragged. Double-clicking a column's
        /// edge gives the column back to automatic sizing.
        /// </summary>
        public static void AllowUserResizing(ListView theListView)
        {
            ListState state = _states.GetValue(theListView, list => new ListState());
            state.Designed = theListView.Columns.Cast<ColumnHeader>().Select(TagOf).ToArray();
            foreach (ColumnHeader column in theListView.Columns)
            {
                state.Applied[column] = column.Width;
            }

            theListView.ColumnWidthChanging += (sender, e) =>
            {
                if (_applying == 0 && e.ColumnIndex < theListView.Columns.Count && e.NewWidth < MinimumUserWidth &&
                    !IsHidden(theListView.Columns[e.ColumnIndex]))
                {
                    e.Cancel = true;
                    e.NewWidth = MinimumUserWidth;
                }
            };
            theListView.ColumnWidthChanged += (sender, e) =>
            {
                if (_applying > 0 || e.ColumnIndex >= theListView.Columns.Count)
                {
                    return;
                }
                ColumnHeader column = theListView.Columns[e.ColumnIndex];
                if (IsHidden(column))
                {
                    return;
                }
                if (IsFill(column))
                {
                    //Whatever changed it, the fill column goes back to taking exactly the room that is left
                    FitFillColumn(theListView, column);
                    return;
                }
                int applied;
                if (state.Applied.TryGetValue(column, out applied) && applied == column.Width)
                {
                    //The list putting back a width it was given, as it does when its window is created
                    return;
                }

                column.Tag = string.Format(CultureInfo.InvariantCulture, UserWidthTemplate, Math.Max(MinimumUserWidth, column.Width));
                state.Applied[column] = column.Width;
                FitFillToTheRightOf(theListView, column.Index);
            };
            HeaderWatcher.Attach(theListView, state);
        }

        /// <summary>
        /// The widths the player has dragged, for the preferences: the column count, then index=tag for each column
        /// whose tag has changed from its design, such as "7|1=Fixed;180|4=Fixed;240". Null when nothing was dragged.
        /// </summary>
        public static string SavedWidths(ListView theListView)
        {
            ListState state;
            if (!_states.TryGetValue(theListView, out state) || state.Designed == null || state.Designed.Length != theListView.Columns.Count)
            {
                return null;
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < state.Designed.Length; i++)
            {
                string tag = TagOf(theListView.Columns[i]);
                if (!string.Equals(tag, state.Designed[i], StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(string.Concat(i.ToString(CultureInfo.InvariantCulture), "=", tag));
                }
            }
            if (parts.Count == 0)
            {
                return null;
            }
            parts.Insert(0, state.Designed.Length.ToString(CultureInfo.InvariantCulture));
            return string.Join(SavedSeparator.ToString(), parts);
        }

        /// <summary>
        /// Puts back widths from <see cref="SavedWidths"/>. Anything that does not fit this list (another number of
        /// columns, a hidden or fill column, a width of the wrong kind or out of range) is ignored and that column
        /// stays automatic. Earlier versions also saved a least width for the fill column; that is dropped, as the
        /// fill column now always takes the room that is left.
        /// </summary>
        public static void RestoreWidths(ListView theListView, string saved)
        {
            ListState state;
            if (string.IsNullOrEmpty(saved) || !_states.TryGetValue(theListView, out state) || state.Designed == null)
            {
                return;
            }
            string[] designed = state.Designed;
            string[] parts = saved.Split(SavedSeparator);
            int count;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                count != designed.Length || count != theListView.Columns.Count)
            {
                //Saved by a version with other columns
                return;
            }
            foreach (string part in parts.Skip(1))
            {
                int equals = part.IndexOf('=');
                int index;
                if (equals <= 0 ||
                    !int.TryParse(part.Substring(0, equals), NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
                    index >= count ||
                    IsHidden(theListView.Columns[index]) ||
                    IsFillTag(designed[index]))
                {
                    continue;
                }
                Match match = SavedTag.Match(part.Substring(equals + 1));
                int width = match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                if (!match.Success || !match.Groups[1].Value.Equals("Fixed", StringComparison.OrdinalIgnoreCase) ||
                    width < MinimumUserWidth || width > MaximumSavedWidth)
                {
                    continue;
                }
                theListView.Columns[index].Tag = string.Format(CultureInfo.InvariantCulture, UserWidthTemplate, width);
            }
        }

        /// <summary>Gives a column the player has sized back to automatic sizing, as double-clicking its edge does.</summary>
        public static void ResetColumn(ListView theListView, int index)
        {
            ListState state;
            if (index < 0 || index >= theListView.Columns.Count || !_states.TryGetValue(theListView, out state) ||
                state.Designed == null || index >= state.Designed.Length)
            {
                return;
            }
            ColumnHeader column = theListView.Columns[index];
            if (IsHidden(column) || IsFill(column))
            {
                return;
            }
            column.Tag = state.Designed[index];
            theListView.BeginUpdate();
            try
            {
                ResizeColumns(theListView);
            }
            finally
            {
                theListView.EndUpdate();
            }
        }

        private static string TagOf(ColumnHeader column)
        {
            return column.Tag != null ? column.Tag.ToString() : null;
        }

        private static bool IsHidden(ColumnHeader column)
        {
            return column.Tag != null && HiddenTag.Equals(column.Tag.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFillTag(string tag)
        {
            return tag != null && tag.StartsWith("Fill", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFill(ColumnHeader column)
        {
            return IsFillTag(TagOf(column));
        }

        public static void ResizeColumns(ListView theListView)
        {
            _applying++;
            try
            {
                ResizeColumnsCore(theListView);
            }
            finally
            {
                _applying--;
            }
        }

        private static void ResizeColumnsCore(ListView theListView)
        {
            if (theListView.Columns.Count == 0)
            {
                return;
            }

            ColumnHeader fillColumn = null;
            int fillLeast = DpiHelper.Scale(MinimumFillWidth);
            Dictionary<ColumnHeader, int> widths = new Dictionary<ColumnHeader, int>();
            List<ColumnHeader> squeezable = new List<ColumnHeader>();

            foreach (ColumnHeader column in theListView.Columns)
            {
                string style = TagOf(column) ?? string.Empty;
                string value = string.Empty;
                int split = style.IndexOf(SplitChar);
                if (split >= 0)
                {
                    value = style.Substring(split + 1);
                    style = style.Substring(0, split);
                }

                switch (ConfigHelper.ParseEnumString<ColumnHeaderResizeStyle>(style))
                {
                    case ColumnHeaderResizeStyle.ColumnContent:
                        int content = ContentWidth(theListView, column);
                        widths[column] = content > 0 ? content : HeaderWidth(theListView, column);
                        break;
                    case ColumnHeaderResizeStyle.HeaderSize:
                        widths[column] = HeaderWidth(theListView, column);
                        break;
                    case ColumnHeaderResizeStyle.ContentHeaderMax:
                        widths[column] = Math.Max(HeaderWidth(theListView, column), ContentWidth(theListView, column));
                        squeezable.Add(column);
                        break;
                    case ColumnHeaderResizeStyle.Fill:
                        fillColumn = column;
                        fillLeast = Math.Max(fillLeast, Math.Max(HeaderWidth(theListView, column), ContentWidth(theListView, column)));
                        break;
                    case ColumnHeaderResizeStyle.Fixed:
                        int width;
                        widths[column] = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out width) ? width : column.Width;
                        break;
                    default:
                        widths[column] = column.Width;
                        break;
                }
            }

            int over = widths.Values.Sum() + (fillColumn != null ? fillLeast : 0) - theListView.ClientSize.Width;
            int squeezed = DpiHelper.Scale(SqueezedWidth);
            if (over > 0 && squeezable.Sum(column => Math.Max(0, widths[column] - squeezed)) >= over)
            {
                //Short of room: the widest columns sized to their text give way first, down to a width that still reads,
                //with the whole text in the tooltip of the row. When even that would not make the columns fit, nothing
                //is cut and the list scrolls sideways instead
                foreach (ColumnHeader column in squeezable.OrderByDescending(column => widths[column]))
                {
                    int give = Math.Min(over, widths[column] - squeezed);
                    if (give > 0)
                    {
                        widths[column] -= give;
                        over -= give;
                    }
                }
            }

            ListState state = _states.GetValue(theListView, list => new ListState());
            foreach (KeyValuePair<ColumnHeader, int> pair in widths)
            {
                state.Applied[pair.Key] = pair.Value;
                if (pair.Key.Width != pair.Value)
                {
                    pair.Key.Width = pair.Value;
                }
            }

            state.FillLeast = fillLeast;
            if (fillColumn != null)
            {
                SetFillWidth(theListView, state, fillColumn);
            }
        }

        /// <summary>
        /// While an edge is dragged, a fill column to the right of it can change without moving that edge from under
        /// the pointer, so it gives or takes the room at once; one to the left is fitted when the drag is over.
        /// </summary>
        private static void FitFillToTheRightOf(ListView theListView, int index)
        {
            ColumnHeader fill = theListView.Columns.Cast<ColumnHeader>().FirstOrDefault(IsFill);
            if (fill != null && fill.Index > index)
            {
                FitFillColumn(theListView, fill);
            }
        }

        /// <summary>Lets the fill column take the room the other columns leave, as they stand now.</summary>
        private static void FitFillColumn(ListView theListView, ColumnHeader fillColumn)
        {
            ListState state = _states.GetValue(theListView, list => new ListState());
            _applying++;
            try
            {
                SetFillWidth(theListView, state, fillColumn);
            }
            finally
            {
                _applying--;
            }
        }

        private static void SetFillWidth(ListView theListView, ListState state, ColumnHeader fillColumn)
        {
            int least = state.FillLeast > 0 ? state.FillLeast : DpiHelper.Scale(MinimumFillWidth);
            int others = theListView.Columns.Cast<ColumnHeader>().Where(column => column != fillColumn).Sum(column => column.Width);
            //Never below a readable minimum: a negative width means "auto size" to the ListView and starts a resize loop
            int width = Math.Max(least, theListView.ClientSize.Width - others);
            state.Applied[fillColumn] = width;
            if (fillColumn.Width != width)
            {
                fillColumn.Width = width;
            }
        }

        /// <summary>
        /// The width the column's heading needs, measured in the font it is drawn in (the Age of Wonders look draws
        /// headings in its bold serif, wider than the list's own font), rather than auto-sized: the last column
        /// auto-sized to its heading fills the list.
        /// </summary>
        private static int HeaderWidth(ListView theListView, ColumnHeader column)
        {
            if (string.IsNullOrEmpty(column.Text))
            {
                return MinimumUserWidth;
            }
            return TextRenderer.MeasureText(column.Text, Theme.ListHeadingFont(theListView), System.Drawing.Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + DpiHelper.Scale(HeaderPadding);
        }

        /// <summary>
        /// The width the column needs for its rows, measured with each row's own font, or 0 when no row has text in
        /// it. The ListView's own content autosize measures with the control font, which is narrower than the bold
        /// rows the accounts and activity lists use, so it came out a few pixels short and the text was cut.
        /// </summary>
        private static int ContentWidth(ListView theListView, ColumnHeader column)
        {
            const int CellPadding = 12;
            try
            {
                int iconWidth = column.Index == 0 && theListView.SmallImageList != null ? theListView.SmallImageList.ImageSize.Width + 4 : 0;
                int widest = 0;
                foreach (ListViewItem item in theListView.Items)
                {
                    if (item == null || column.Index >= item.SubItems.Count) continue;
                    ListViewItem.ListViewSubItem cell = item.SubItems[column.Index];
                    if (cell == null || string.IsNullOrEmpty(cell.Text)) continue;
                    System.Drawing.Font font = (item.UseItemStyleForSubItems ? item.Font : cell.Font) ?? theListView.Font;
                    int width = TextRenderer.MeasureText(cell.Text, font, System.Drawing.Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    if (width > widest) widest = width;
                }
                return widest > 0 ? widest + iconWidth + CellPadding : 0;
            }
            catch (Exception)
            {
                //Rows can be mid-construction when a resize arrives; the heading's width will do until the next one
                return 0;
            }
        }

        /// <summary>
        /// Watches the list's header for what the ListView does not report while a column edge is dragged: the start
        /// (refused for the fill column and hidden columns, which are not the player's to size), each step (the fill
        /// column follows when it lies to the right), the end (everything is laid out again), and a double-click on an
        /// edge (the column goes back to automatic sizing, where the list itself would fix it at its text's width).
        /// </summary>
        private sealed class HeaderWatcher : NativeWindow
        {
            private const int WM_NOTIFY = 0x004E;
            private const int HDN_ITEMCHANGEDW = -321;
            private const int HDN_DIVIDERDBLCLICKW = -325;
            private const int HDN_BEGINTRACKW = -326;
            private const int HDN_ENDTRACKW = -327;

            [StructLayout(LayoutKind.Sequential)]
            private struct NMHDR
            {
                public IntPtr hwndFrom;
                public IntPtr idFrom;
                public int code;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NMHEADER
            {
                public NMHDR hdr;
                public int iItem;
                public int iButton;
                public IntPtr pitem;
            }

            private static readonly ConditionalWeakTable<ListView, HeaderWatcher> _watchers = new ConditionalWeakTable<ListView, HeaderWatcher>();
            private readonly ListView _list;
            private readonly ListState _state;

            public static void Attach(ListView list, ListState state)
            {
                _watchers.GetValue(list, key => new HeaderWatcher(key, state));
            }

            private HeaderWatcher(ListView list, ListState state)
            {
                _list = list;
                _state = state;
                list.HandleCreated += (sender, e) => AssignHandle(list.Handle);
                list.HandleDestroyed += (sender, e) => ReleaseHandle();
                if (list.IsHandleCreated)
                {
                    AssignHandle(list.Handle);
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg != WM_NOTIFY || m.LParam == IntPtr.Zero)
                {
                    base.WndProc(ref m);
                    return;
                }

                int code = Marshal.PtrToStructure<NMHDR>(m.LParam).code;
                switch (code)
                {
                    case HDN_BEGINTRACKW:
                        int item = Marshal.PtrToStructure<NMHEADER>(m.LParam).iItem;
                        if (item >= 0 && item < _list.Columns.Count && (IsFill(_list.Columns[item]) || IsHidden(_list.Columns[item])))
                        {
                            //Not the player's to size: the drag does not start
                            m.Result = (IntPtr)1;
                            return;
                        }
                        _state.Tracking = item;
                        base.WndProc(ref m);
                        return;
                    case HDN_ITEMCHANGEDW:
                        base.WndProc(ref m);
                        if (_state.Tracking >= 0 && _applying == 0)
                        {
                            FitFillToTheRightOf(_list, _state.Tracking);
                        }
                        return;
                    case HDN_ENDTRACKW:
                        base.WndProc(ref m);
                        _state.Tracking = -1;
                        if (_list.IsHandleCreated)
                        {
                            _list.BeginInvoke(new Action(() => ResizeColumns(_list)));
                        }
                        return;
                    case HDN_DIVIDERDBLCLICKW:
                        ResetColumn(_list, Marshal.PtrToStructure<NMHEADER>(m.LParam).iItem);
                        m.Result = IntPtr.Zero;
                        return;
                    default:
                        base.WndProc(ref m);
                        return;
                }
            }
        }
    }
}
