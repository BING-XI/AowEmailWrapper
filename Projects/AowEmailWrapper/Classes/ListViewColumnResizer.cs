using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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

    public static class ListViewColumnResizer
    {
        private const char SplitChar = ';';
        private const int MinimumFillWidth = 60;
        private const string HiddenTag = "Fixed;0";
        private const string UserWidthTemplate = "Fixed;{0}";
        private const string FillFloorTemplate = "Fill;{0}";
        /// <summary>A dragged column never gets narrower than this, so it cannot vanish and become unreachable.</summary>
        private const int MinimumUserWidth = 24;
        private const int MaximumSavedWidth = 4000;
        private const char SavedSeparator = '|';
        private static readonly Regex SavedTag = new Regex(@"^(Fixed|Fill);(\d{1,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Each list's column tags as designed, to tell the widths the player has dragged from the automatic ones.</summary>
        private static readonly ConditionalWeakTable<ListView, string[]> _designedTags = new ConditionalWeakTable<ListView, string[]>();

        [ThreadStatic]
        private static int _applying;

        /// <summary>
        /// Lets the player drag column edges. A column the player has sized keeps that width through later
        /// automatic resizes (it becomes a Fixed column), the fill column takes up whatever is left, and a
        /// hidden column stays hidden.
        /// </summary>
        public static void AllowUserResizing(ListView theListView)
        {
            _designedTags.AddOrUpdate(theListView, theListView.Columns.Cast<ColumnHeader>().Select(TagOf).ToArray());
            theListView.ColumnWidthChanging += (sender, e) =>
            {
                if (_applying > 0)
                {
                    return;
                }
                if (IsHidden(theListView.Columns[e.ColumnIndex]))
                {
                    e.Cancel = true;
                    e.NewWidth = 0;
                }
                else if (e.NewWidth < MinimumUserWidth)
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
                //The header may still be tracking the drag, so nothing else is resized here: the width is only
                //remembered, the list scrolls sideways when the columns no longer fit, and the fill column keeps
                //at least its present width at the next automatic resize instead of being squeezed
                column.Tag = string.Format(UserWidthTemplate, Math.Max(MinimumUserWidth, column.Width));
                foreach (ColumnHeader other in theListView.Columns)
                {
                    if (other != column && other.Tag != null && other.Tag.ToString().StartsWith("Fill", StringComparison.OrdinalIgnoreCase))
                    {
                        other.Tag = string.Format(FillFloorTemplate, other.Width);
                    }
                }
            };
        }

        /// <summary>
        /// The widths the player has dragged, for the preferences: the column count, then index=tag for each column
        /// whose tag has changed from its design, such as "7|1=Fixed;180|0=Fill;240". Null when nothing was dragged.
        /// </summary>
        public static string SavedWidths(ListView theListView)
        {
            string[] designed;
            if (!_designedTags.TryGetValue(theListView, out designed) || designed.Length != theListView.Columns.Count)
            {
                return null;
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < designed.Length; i++)
            {
                string tag = TagOf(theListView.Columns[i]);
                if (!string.Equals(tag, designed[i], StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(string.Concat(i.ToString(CultureInfo.InvariantCulture), "=", tag));
                }
            }
            if (parts.Count == 0)
            {
                return null;
            }
            parts.Insert(0, designed.Length.ToString(CultureInfo.InvariantCulture));
            return string.Join(SavedSeparator.ToString(), parts);
        }

        /// <summary>
        /// Puts back widths from <see cref="SavedWidths"/>. Anything that does not fit this list (another number of
        /// columns, a hidden column, a width of the wrong kind or out of range) is ignored and that column stays automatic.
        /// </summary>
        public static void RestoreWidths(ListView theListView, string saved)
        {
            string[] designed;
            if (string.IsNullOrEmpty(saved) || !_designedTags.TryGetValue(theListView, out designed))
            {
                return;
            }
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
                    IsHidden(theListView.Columns[index]))
                {
                    continue;
                }
                Match match = SavedTag.Match(part.Substring(equals + 1));
                if (!match.Success)
                {
                    continue;
                }
                bool isFill = designed[index] != null && designed[index].StartsWith("Fill", StringComparison.OrdinalIgnoreCase);
                bool savedFill = match.Groups[1].Value.Equals("Fill", StringComparison.OrdinalIgnoreCase);
                int width = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                //The fill column only ever carries a floor, the others only a fixed width
                if (isFill != savedFill || width < MinimumUserWidth || width > MaximumSavedWidth)
                {
                    continue;
                }
                theListView.Columns[index].Tag = string.Format(CultureInfo.InvariantCulture, savedFill ? FillFloorTemplate : UserWidthTemplate, width);
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
            if (theListView.Columns.Count > 0 &&
                theListView.Items.Count > 0)
            {
                ColumnHeader fillColumn = null;
                int fillFloor = MinimumFillWidth;
                int totalColumnWidth = 0;

                foreach (ColumnHeader column in theListView.Columns)
                {
                    if (column.Tag != null)
                    {
                        string style = column.Tag.ToString();
                        string value = string.Empty;

                        if (style.Contains(SplitChar))
                        {
                            string[] split = style.Split(SplitChar);
                            style = split[0];
                            value = split[1];
                        }

                        ColumnHeaderResizeStyle theStyle = ConfigHelper.ParseEnumString<ColumnHeaderResizeStyle>(style);

                        switch (theStyle)
                        {
                            case ColumnHeaderResizeStyle.ColumnContent:
                                column.Width = ContentWidth(theListView, column);
                                totalColumnWidth += column.Width;
                                break;
                            case ColumnHeaderResizeStyle.HeaderSize:
                                AutoResizeColumn(column, ColumnHeaderAutoResizeStyle.HeaderSize);
                                totalColumnWidth += column.Width;
                                break;
                            case ColumnHeaderResizeStyle.ContentHeaderMax:
                                AutoResizeColumn(column, ColumnHeaderAutoResizeStyle.HeaderSize);
                                int headerSize = column.Width;
                                int columnContentSize = ContentWidth(theListView, column);

                                column.Width = (headerSize > columnContentSize) ? headerSize : columnContentSize;
                                totalColumnWidth += column.Width;
                                break;
                            case ColumnHeaderResizeStyle.Fill:
                                fillColumn = column;
                                int floor;
                                if (int.TryParse(value, out floor))
                                {
                                    fillFloor = Math.Max(MinimumFillWidth, floor);
                                }
                                break;
                            case ColumnHeaderResizeStyle.Fixed:
                                int width = 0;
                                if (int.TryParse(value, out width))
                                {
                                    column.Width = width;
                                }
                                totalColumnWidth += column.Width;
                                break;
                            default:
                                totalColumnWidth += column.Width;
                                break;
                        }
                    }
                }

                if (fillColumn != null)
                {
                    //Never below a readable minimum: a negative width means "auto size" to the ListView and starts a resize loop
                    //Below its floor the list scrolls sideways instead of squeezing the fill column
                    fillColumn.Width = Math.Max(fillFloor, theListView.ClientSize.Width - totalColumnWidth);
                }
            }
        }
        
        /// <summary>
        /// The width the column needs for its rows, measured with each row's own font. The ListView's own
        /// content autosize measures with the control font, which is narrower than the bold rows the
        /// accounts and activity lists use, so it came out a few pixels short and the text was cut.
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
                return widest + iconWidth + CellPadding;
            }
            catch (Exception)
            {
                // Rows can be mid-construction when a resize arrives; fall back to the ListView's own measure.
                AutoResizeColumn(column, ColumnHeaderAutoResizeStyle.ColumnContent);
                return column.Width;
            }
        }

        private static void AutoResizeColumn(ColumnHeader theColumn, ColumnHeaderAutoResizeStyle style)
        {
            try
            {
                //This method seems to sometimes throw a random null ref exception
                theColumn.AutoResize(style);
            }
            catch { }
        }
    }
}
