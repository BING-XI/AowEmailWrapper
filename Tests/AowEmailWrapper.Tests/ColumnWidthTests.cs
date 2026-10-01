using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;
using AowEmailWrapper.Classes;
using AowEmailWrapper.ConfigFramework;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>Column widths the player drags are kept in the preferences and put back at the next start.</summary>
    public class ColumnWidthTests
    {
        /// <summary>Laid out like the activity log: a fill column, a fixed one, automatic ones and a hidden sort column.</summary>
        private static ListView BuildList(Form form)
        {
            ListView list = new ListView { View = View.Details, Width = 600 };
            list.Columns.Add(new ColumnHeader { Text = "File", Tag = "Fill" });
            list.Columns.Add(new ColumnHeader { Text = "Map", Tag = "Fixed;130" });
            list.Columns.Add(new ColumnHeader { Text = "Turn", Tag = "HeaderSize" });
            list.Columns.Add(new ColumnHeader { Text = "Ticks", Tag = "Fixed;0", Width = 0 });
            list.Items.Add(new ListViewItem(new[] { "turn.asg", "Map", "3", "0" }));
            form.Controls.Add(list);
            //A live list, as on screen: only then does a new width go through the notifications a drag sends
            IntPtr handle = list.Handle;
            ListViewColumnResizer.AllowUserResizing(list);
            return list;
        }

        /// <summary>Laid out like the lists now are: columns sized to their text or fixed, then the fill column last.</summary>
        private static ListView BuildFillLastList(Form form, int width, params string[] status)
        {
            ListView list = new ListView { View = View.Details, Width = width, Height = 200 };
            list.Columns.Add(new ColumnHeader { Text = "File", Tag = "ContentHeaderMax" });
            list.Columns.Add(new ColumnHeader { Text = "Map", Tag = "Fixed;130" });
            list.Columns.Add(new ColumnHeader { Text = "Status", Tag = "ContentHeaderMax" });
            list.Columns.Add(new ColumnHeader { Text = "Copy", Tag = "Fill" });
            list.Columns.Add(new ColumnHeader { Text = "Ticks", Tag = "Fixed;0", Width = 0 });
            foreach (string text in status)
            {
                list.Items.Add(new ListViewItem(new[] { "turn.asg", "Map", text, "Vanilla", "0" }));
            }
            form.Controls.Add(list);
            IntPtr handle = list.Handle;
            ListViewColumnResizer.AllowUserResizing(list);
            ListViewColumnResizer.ResizeColumns(list);
            return list;
        }

        private static int Total(ListView list)
        {
            return list.Columns.Cast<ColumnHeader>().Sum(column => column.Width);
        }

        [Fact]
        public void TheFillColumnGivesAndTakesTheRoomAColumnIsDraggedTo()
        {
            using (Form form = new Form())
            {
                ListView list = BuildFillLastList(form, 900, "Sent");
                Assert.Equal(list.ClientSize.Width, Total(list));
                int fill = list.Columns[3].Width;

                //Widened: the fill column gives the room at once, so the list does not scroll sideways
                list.Columns[1].Width = 180;
                Assert.Equal("Fixed;180", list.Columns[1].Tag);
                Assert.Equal(fill - 50, list.Columns[3].Width);
                Assert.Equal(list.ClientSize.Width, Total(list));

                //Narrowed: it takes the room back, so no gap opens at the right
                list.Columns[1].Width = 100;
                Assert.Equal(fill + 30, list.Columns[3].Width);
                Assert.Equal(list.ClientSize.Width, Total(list));
                Assert.Equal("Fill", list.Columns[3].Tag);
            }
        }

        [Fact]
        public void TheFillColumnCannotBeDraggedAndKeepsFilling()
        {
            using (Form form = new Form())
            {
                ListView list = BuildFillLastList(form, 900, "Sent");
                int fill = list.Columns[3].Width;

                list.Columns[3].Width = fill - 100;

                Assert.Equal(fill, list.Columns[3].Width);
                Assert.Equal("Fill", list.Columns[3].Tag);
                Assert.Null(ListViewColumnResizer.SavedWidths(list));
            }
        }

        [Fact]
        public void AnEmptyListStillSpansItsWidth()
        {
            using (Form form = new Form())
            {
                ListView list = BuildFillLastList(form, 700);
                Assert.Equal(list.ClientSize.Width, Total(list));
                Assert.True(list.Columns[0].Width > 0 && list.Columns[2].Width > 0);
            }
        }

        [Fact]
        public void ShortOfRoomTheWidestTextColumnGivesWayButOnlyThen()
        {
            string longStatus = "Sent (probably with somebody.with.a.very.long.address@example.com)";
            using (Form form = new Form())
            {
                ListView wide = BuildFillLastList(form, 1600, longStatus);
                int natural = wide.Columns[2].Width;
                Assert.Equal(wide.ClientSize.Width, Total(wide));

                ListView narrow = BuildFillLastList(form, 500, longStatus);
                Assert.True(narrow.Columns[2].Width < natural, "the long status column gives way in a narrow list");
                Assert.Equal(narrow.ClientSize.Width, Total(narrow));
            }
        }

        [Fact]
        public void ADraggedColumnGoesBackToAutomaticWhenItsEdgeIsDoubleClicked()
        {
            using (Form form = new Form())
            {
                ListView list = BuildFillLastList(form, 900, "Sent");
                int automatic = list.Columns[2].Width;
                list.Columns[2].Width = automatic + 120;
                Assert.Equal("Fixed;" + (automatic + 120), list.Columns[2].Tag);

                ListViewColumnResizer.ResetColumn(list, 2);

                Assert.Equal("ContentHeaderMax", list.Columns[2].Tag);
                Assert.Equal(automatic, list.Columns[2].Width);
                Assert.Equal(list.ClientSize.Width, Total(list));
                Assert.Null(ListViewColumnResizer.SavedWidths(list));
            }
        }

        [Fact]
        public void TheLeastWidthEarlierVersionsSavedForTheFillColumnIsDropped()
        {
            using (Form form = new Form())
            {
                ListView list = BuildFillLastList(form, 900, "Sent");
                ListViewColumnResizer.RestoreWidths(list, "5|3=Fill;600|1=Fixed;150");
                Assert.Equal("Fill", list.Columns[3].Tag);
                Assert.Equal("Fixed;150", list.Columns[1].Tag);
            }
        }

        [Fact]
        public void NothingIsSavedUntilAColumnIsDragged()
        {
            using (Form form = new Form())
            {
                ListView list = BuildList(form);
                form.CreateControl();
                ListViewColumnResizer.ResizeColumns(list);
                Assert.Null(ListViewColumnResizer.SavedWidths(list));
            }
        }

        [Fact]
        public void ADraggedWidthSurvivesARestart()
        {
            string saved;
            using (Form form = new Form())
            {
                ListView list = BuildList(form);
                form.CreateControl();
                ListViewColumnResizer.ResizeColumns(list);

                // Setting the width on a live list goes through the same notification as a drag
                list.Columns[2].Width = 90;
                Assert.Equal("Fixed;90", list.Columns[2].Tag);

                saved = ListViewColumnResizer.SavedWidths(list);
                Assert.StartsWith("4|", saved);
                Assert.Contains("2=Fixed;90", saved);
            }

            using (Form form = new Form())
            {
                ListView list = BuildList(form);
                form.CreateControl();
                ListViewColumnResizer.RestoreWidths(list, saved);
                ListViewColumnResizer.ResizeColumns(list);
                Assert.Equal(90, list.Columns[2].Width);
                Assert.Equal(0, list.Columns[3].Width);
                Assert.Equal(saved, ListViewColumnResizer.SavedWidths(list));
            }
        }

        [Theory]
        [InlineData("5|2=Fixed;90")]            // saved by a version with another number of columns
        [InlineData("4|3=Fixed;200")]           // the hidden sort column stays hidden
        [InlineData("4|2=Fill;90")]             // only the fill column carries a floor
        [InlineData("4|0=Fixed;90")]            // and the fill column never becomes fixed
        [InlineData("4|2=Fixed;5")]             // too narrow to find again
        [InlineData("4|2=Fixed;99999")]         // absurdly wide
        [InlineData("4|9=Fixed;90")]            // no such column
        [InlineData("4|2=HeaderSize")]          // not a width
        [InlineData("four|2=Fixed;90")]
        [InlineData("4|2")]
        [InlineData("4|-1=Fixed;90")]
        public void WidthsThatDoNotFitTheListAreIgnored(string saved)
        {
            using (Form form = new Form())
            {
                ListView list = BuildList(form);
                string[] before = list.Columns.Cast<ColumnHeader>().Select(c => (string)c.Tag).ToArray();
                ListViewColumnResizer.RestoreWidths(list, saved);
                Assert.Equal(before, list.Columns.Cast<ColumnHeader>().Select(c => (string)c.Tag).ToArray());
            }
        }

        [Fact]
        public void TheWidthsAreKeptInThePreferencesFile()
        {
            PreferencesConfigValues preferences = new PreferencesConfigValues(true)
            {
                ActivityColumnWidths = "7|0=Fill;240|1=Fixed;180",
                AccountsColumnWidths = "4|0=Fixed;150"
            };
            XmlSerializer serializer = new XmlSerializer(typeof(PreferencesConfigValues));
            StringWriter writer = new StringWriter();
            serializer.Serialize(writer, preferences);
            PreferencesConfigValues back = (PreferencesConfigValues)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal(preferences.ActivityColumnWidths, back.ActivityColumnWidths);
            Assert.Equal(preferences.AccountsColumnWidths, back.AccountsColumnWidths);

            // A preferences file from before this setting existed leaves every column automatic
            PreferencesConfigValues old = (PreferencesConfigValues)serializer.Deserialize(new StringReader("<preferences_config playsoundonemail=\"true\" />"));
            Assert.Null(old.ActivityColumnWidths);
            Assert.Null(old.AccountsColumnWidths);
        }
    }
}
