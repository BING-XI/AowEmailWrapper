using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Controls;
using AowEmailWrapper.Helpers;
using Xunit;

namespace AowEmailWrapper.Tests
{
    /// <summary>The Age of Wonders look and the switch back to the classic one. Runs on a form that is never shown.</summary>
    public class ThemeTests
    {
        private static Form BuildForm(out Button button, out TextBox text, out Label label, out ListView list, out ThemedTabControl tabs)
        {
            Form form = new Form();
            tabs = new ThemedTabControl();
            TabPage page = new TabPage("Page");
            GroupBox group = new GroupBox { Text = "Group" };
            button = new Button { Text = "Save" };
            text = new TextBox();
            label = new Label { Text = "Server:" };
            list = new ListView { View = View.Details };
            list.Columns.Add("Name", 120);
            group.Controls.Add(button);
            group.Controls.Add(text);
            group.Controls.Add(label);
            page.Controls.Add(group);
            page.Controls.Add(list);
            tabs.TabPages.Add(page);
            form.Controls.Add(tabs);
            return form;
        }

        [Fact]
        public void TheDefaultLookIsClassic()
        {
            Assert.False(Theme.IsAgeOfWonders(null));
            Assert.False(Theme.IsAgeOfWonders(""));
            Assert.True(Theme.IsAgeOfWonders("ageofwonders"));
            Assert.False(Theme.IsAgeOfWonders(Theme.ClassicName));
            Assert.Equal(Theme.ClassicName, new PreferencesConfigValues().Theme);
        }

        [Fact]
        public void TexturesAreEmbeddedTiles()
        {
            Assert.Equal(new Size(256, 256), Theme.ParchmentTexture.Size);
            Assert.Equal(new Size(256, 256), Theme.LeatherTexture.Size);
        }

        [Fact]
        public void ApplyingAndRestoringLeavesControlsAsTheyWere()
        {
            using (Form form = BuildForm(out Button button, out TextBox text, out Label label, out ListView list, out ThemedTabControl tabs))
            {
                Font bodyBefore = label.Font;
                Color buttonBackBefore = button.BackColor;
                Size formBefore = form.Size;

                Theme.Select(Theme.AgeOfWondersName, form);
                Assert.True(Theme.Enabled);
                Assert.True(tabs.Themed);
                Assert.Equal(FlatStyle.Flat, button.FlatStyle);
                Assert.Equal(Theme.TextOnLeather, button.ForeColor);
                Assert.Equal(Theme.Leather, button.BackColor);
                Assert.Equal(Theme.Ink, text.ForeColor);
                Assert.Equal("Palatino Linotype", label.Font.Name);
                Assert.Equal(bodyBefore, list.Font);
                Assert.True(list.OwnerDraw);
                Assert.Equal(formBefore, form.Size);

                Theme.Select(Theme.ClassicName, form);
                Assert.False(Theme.Enabled);
                Assert.False(tabs.Themed);
                Assert.Equal(FlatStyle.Standard, button.FlatStyle);
                Assert.True(button.UseVisualStyleBackColor);
                Assert.Equal(buttonBackBefore, button.BackColor);
                Assert.Equal(bodyBefore, label.Font);
                Assert.Equal(SystemColors.Window, text.BackColor);
                Assert.False(list.OwnerDraw);
                Assert.Null(form.BackgroundImage);
                Assert.Equal(formBefore, form.Size);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static bool IsNativelyDoubleBuffered(ListView list)
        {
            const int LvmGetExtendedListViewStyle = 0x1037, LvsExDoubleBuffer = 0x10000;
            return (SendMessage(list.Handle, LvmGetExtendedListViewStyle, IntPtr.Zero, IntPtr.Zero).ToInt64() & LvsExDoubleBuffer) != 0;
        }

        [Fact]
        public void ListsPaintOffScreenInEitherLook()
        {
            // Without it, dragging a column edge repaints every owner-drawn row on each mouse move and the text flashes.
            using (Form form = BuildForm(out Button button, out TextBox text, out Label label, out ListView list, out ThemedTabControl tabs))
            {
                form.CreateControl();
                Assert.False(IsNativelyDoubleBuffered(list));

                Theme.Select(Theme.AgeOfWondersName, form);
                Assert.True(IsNativelyDoubleBuffered(list));

                Theme.Select(Theme.ClassicName, form);
                Assert.True(IsNativelyDoubleBuffered(list));
            }
        }

        [Fact]
        public void SwitchingBackAndForthIsIdempotent()
        {
            using (Form form = BuildForm(out Button button, out _, out Label label, out _, out _))
            {
                Color labelBackBefore = label.BackColor;
                for (int i = 0; i < 3; i++)
                {
                    Theme.Select(Theme.AgeOfWondersName, form);
                    Theme.Select(Theme.ClassicName, form);
                }
                Assert.Equal(labelBackBefore, label.BackColor);
                Assert.Equal(SystemColors.ControlText, label.ForeColor);
                Assert.Equal(FlatStyle.Standard, button.FlatStyle);

                Theme.Select(Theme.AgeOfWondersName, form);
                Theme.Select(Theme.AgeOfWondersName, form);
                Assert.Equal(Theme.TextOnLeather, button.ForeColor);
                Assert.Equal(Color.Transparent, label.BackColor);
                Theme.Select(Theme.ClassicName, form);
            }
        }
    }
}
