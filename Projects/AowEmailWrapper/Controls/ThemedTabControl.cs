using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AowEmailWrapper.Helpers;

namespace AowEmailWrapper.Controls
{
    /// <summary>
    /// A TabControl that can paint the Age of Wonders look over the native one: a leather strip, gold-lettered
    /// tabs and a gold page border. With Themed off it is an untouched standard TabControl. Neither state
    /// recreates the native window, so switching between them costs one repaint.
    /// </summary>
    public class ThemedTabControl : TabControl
    {
        private const int WmPaint = 0x000F;
        private const int TabPadding = 6;
        private bool _themed;

        public bool Themed
        {
            get { return _themed; }
            set
            {
                if (_themed == value) return;
                _themed = value;
                // Nothing here recreates the native window: the tabs are sized by the control font and the
                // themed look is painted over the native one in WndProc, so a switch is just a repaint.
                if (value) Font = Theme.HeadingFont;
                Invalidate(true);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (_themed && IsHandleCreated && TabCount > 0)
            {
                if (m.Msg == WmEraseBkgnd)
                {
                    // Nothing to erase: the paint below covers the whole strip.
                    m.Result = new IntPtr(1);
                    return;
                }
                if (m.Msg == WmPaint)
                {
                    // Paint the strip, tabs and frame ourselves into a buffer and hand it to the screen in one
                    // go. Letting the native control paint first (its hover highlight included) and covering
                    // it afterwards is what made the tabs flicker under the mouse.
                    PaintStruct ps;
                    IntPtr hdc = BeginPaint(Handle, out ps);
                    try
                    {
                        using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(hdc, ClientRectangle))
                        {
                            PaintThemedFrame(buffer.Graphics);
                            buffer.Render(hdc);
                        }
                    }
                    finally
                    {
                        EndPaint(Handle, ref ps);
                    }
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private const int WmEraseBkgnd = 0x0014;

        [StructLayout(LayoutKind.Sequential)]
        private struct PaintStruct
        {
            public IntPtr hdc;
            public bool fErase;
            public int left, top, right, bottom;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PaintStruct lpPaint);

        private void PaintThemedFrame(Graphics g)
        {
            Rectangle page = DisplayRectangle;
            Rectangle frame = page;
            frame.Inflate(3, 3);

            // Leather everywhere the page is not, then the tabs on top of it.
            Region leather = new Region(ClientRectangle);
            leather.Exclude(page);
            for (int i = 0; i < TabCount; i++)
            {
                leather.Exclude(GetTabRect(i));
            }
            g.SetClip(leather, System.Drawing.Drawing2D.CombineMode.Replace);
            Theme.FillLeather(g, ClientRectangle);
            g.ResetClip();
            leather.Dispose();

            using (Pen gold = new Pen(Theme.Gold))
            {
                g.DrawRectangle(gold, frame.X, frame.Y, frame.Width - 1, frame.Height - 1);
            }

            for (int i = 0; i < TabCount; i++)
            {
                DrawThemedTab(g, i);
            }
            if (SelectedIndex >= 0)
            {
                // Join the selected tab to the page by painting out the frame line under it.
                Rectangle selected = GetTabRect(SelectedIndex);
                Rectangle join = new Rectangle(selected.X + 1, frame.Y, selected.Width - 2, page.Y - frame.Y);
                Theme.FillParchment(g, join);
            }
        }

        private void DrawThemedTab(Graphics g, int index)
        {
            Rectangle tab = GetTabRect(index);
            bool selected = index == SelectedIndex;

            if (selected)
            {
                Theme.FillParchment(g, tab);
            }
            else
            {
                using (SolidBrush fill = new SolidBrush(Theme.LeatherLight))
                {
                    g.FillRectangle(fill, tab);
                }
            }
            using (Pen border = new Pen(selected ? Theme.Gold : Theme.GoldDark))
            {
                g.DrawLine(border, tab.Left, tab.Top, tab.Right - 1, tab.Top);
                g.DrawLine(border, tab.Left, tab.Top, tab.Left, tab.Bottom - 1);
                g.DrawLine(border, tab.Right - 1, tab.Top, tab.Right - 1, tab.Bottom - 1);
            }

            DrawTabContent(g, index, tab, selected ? Theme.Ink : Theme.TextOnLeather, Theme.HeadingFont);
        }

        private void DrawTabContent(Graphics g, int index, Rectangle tab, Color textColor, Font font)
        {
            Rectangle text = tab;
            TabPage page = TabPages[index];
            if (ImageList != null && page.ImageIndex >= 0 && page.ImageIndex < ImageList.Images.Count)
            {
                Image image = ImageList.Images[page.ImageIndex];
                g.DrawImage(image, tab.Left + TabPadding, tab.Top + (tab.Height - image.Height) / 2, image.Width, image.Height);
                text.X += image.Width + TabPadding;
                text.Width -= image.Width + TabPadding;
            }
            // The native control sized the tab for this font without GDI's glyph padding, so draw without it too.
            TextRenderer.DrawText(g, page.Text, font, text, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }
    }
}
