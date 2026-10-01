using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AowEmailWrapper.Classes;
using AowEmailWrapper.Controls;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// The Age of Wonders look: parchment pages, dark leather chrome, gold trim and a serif for headings.
    /// Apply() walks a form and restyles every control it knows; the original values are remembered so the
    /// classic Windows look can be restored without a restart.
    /// </summary>
    public static class Theme
    {
        public const string ClassicName = "Classic";
        public const string AgeOfWondersName = "AgeOfWonders";
        public const string DefaultName = ClassicName;

        public static readonly Color Leather = Color.FromArgb(54, 33, 20);
        public static readonly Color LeatherLight = Color.FromArgb(92, 58, 34);
        public static readonly Color LeatherDark = Color.FromArgb(34, 20, 12);
        public static readonly Color Parchment = Color.FromArgb(236, 222, 186);
        public static readonly Color ParchmentLight = Color.FromArgb(249, 240, 216);
        public static readonly Color ParchmentDark = Color.FromArgb(214, 196, 154);
        public static readonly Color Ink = Color.FromArgb(46, 28, 14);
        public static readonly Color InkFaded = Color.FromArgb(110, 84, 58);
        public static readonly Color Gold = Color.FromArgb(206, 168, 82);
        public static readonly Color GoldLight = Color.FromArgb(236, 206, 128);
        public static readonly Color GoldDark = Color.FromArgb(150, 116, 46);
        public static readonly Color Crimson = Color.FromArgb(128, 34, 26);

        private const string HeadingFontFamily = "Palatino Linotype";
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;

        private const int WmSetRedraw = 0x000B;
        private const int WmNcActivate = 0x0086;
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

        private static bool _enabled = string.Equals(DefaultName, AgeOfWondersName, StringComparison.Ordinal);
        private static Image _parchment;
        private static Image _leather;
        private static Font _bodyFont;
        private static Font _headingFont;
        private static Font _smallHeadingFont;
        private static readonly ConditionalWeakTable<Control, Snapshot> _originals = new ConditionalWeakTable<Control, Snapshot>();
        private static readonly ConditionalWeakTable<ListView, ListViewHooks> _listViewHooks = new ConditionalWeakTable<ListView, ListViewHooks>();
        private static readonly ConditionalWeakTable<Button, ButtonHooks> _buttonHooks = new ConditionalWeakTable<Button, ButtonHooks>();
        private static readonly ConditionalWeakTable<GroupBox, GroupBoxHooks> _groupBoxHooks = new ConditionalWeakTable<GroupBox, GroupBoxHooks>();

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>True when the Age of Wonders look is on. New forms read this when they apply themselves.</summary>
        public static bool Enabled
        {
            get { return _enabled; }
        }

        public static string CurrentName
        {
            get { return _enabled ? AgeOfWondersName : ClassicName; }
        }

        public static bool IsAgeOfWonders(string themeName)
        {
            return string.IsNullOrEmpty(themeName)
                ? string.Equals(DefaultName, AgeOfWondersName, StringComparison.Ordinal)
                : string.Equals(themeName, AgeOfWondersName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Selects the look by its configured name and restyles the forms given.</summary>
        public static void Select(string themeName, params Form[] forms)
        {
            _enabled = IsAgeOfWonders(themeName);
            ToolStripManager.Renderer = _enabled ? new AowToolStripRenderer() : null;
            foreach (Form form in forms)
            {
                if (form != null && !form.IsDisposed)
                {
                    Apply(form);
                }
            }
        }

        public static Image ParchmentTexture
        {
            get { return _parchment ?? (_parchment = LoadTexture("parchment.png", Parchment)); }
        }

        public static Image LeatherTexture
        {
            get { return _leather ?? (_leather = LoadTexture("leather.png", Leather)); }
        }

        /// <summary>Serif used for ordinary text in the Age of Wonders look.</summary>
        public static Font BodyFont
        {
            get { return _bodyFont ?? (_bodyFont = new Font(HeadingFontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point)); }
        }

        /// <summary>Bold serif used for tabs, buttons and group titles.</summary>
        public static Font HeadingFont
        {
            get { return _headingFont ?? (_headingFont = new Font(HeadingFontFamily, 9.75f, FontStyle.Bold, GraphicsUnit.Point)); }
        }

        /// <summary>
        /// The bold serif for a button of the given height in pixels: the heading size when there is room for it,
        /// a smaller cut for the short buttons of the dialogs, so text is never pushed against the border.
        /// </summary>
        public static Font ButtonFont(int buttonHeight)
        {
            if (buttonHeight >= HeadingFont.Height + 8) return HeadingFont;
            return _smallHeadingFont ?? (_smallHeadingFont = new Font(HeadingFontFamily, 8.25f, FontStyle.Bold, GraphicsUnit.Point));
        }

        /// <summary>Restyles a form (or any control tree) for the current look.</summary>
        /// <summary>The font a list's column headings are drawn in: the heading serif when the look draws them, else the list's own.</summary>
        public static Font ListHeadingFont(ListView listView)
        {
            ListViewHooks hooks;
            return _listViewHooks.TryGetValue(listView, out hooks) && hooks.Attached ? HeadingFont : listView.Font;
        }

        public static void Apply(Control root)
        {
            if (root == null || root.IsDisposed)
            {
                return;
            }

            Form form = root as Form;
            if (form != null)
            {
                ApplyWindowChrome(form);
            }

            // One repaint at the end instead of one per control: without this the window visibly
            // re-dresses itself control by control.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            bool freeze = root.IsHandleCreated;
            if (freeze) SendMessage(root.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            root.SuspendLayout();
            try
            {
                // Remember every control's original look before touching any of them: a child that has no
                // colour or font of its own reports its parent's, so snapshots taken mid-restyle would record
                // the theme instead of the original.
                Remember(root);
                Walk(root);
            }
            finally
            {
                root.ResumeLayout(true);
                if (freeze)
                {
                    SendMessage(root.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                    root.Refresh();
                }
                System.Diagnostics.Trace.TraceInformation("Theme {0} applied to {1} in {2} ms", CurrentName, root.Name, clock.ElapsedMilliseconds);
            }
        }

        private static void Remember(Control control)
        {
            _originals.GetValue(control, Snapshot.Take);
            if (control is ListView)
            {
                // Pin the list's font before any parent changes its own: a list rescales its column widths
                // whenever its (inherited) font changes, and the rounding leaves the columns a little narrower
                // after every switch.
                control.Font = control.Font;
            }
            foreach (Control child in control.Controls)
            {
                Remember(child);
            }
        }

        private static void Walk(Control control)
        {
            Style(control);
            foreach (Control child in control.Controls)
            {
                Walk(child);
            }
        }

        private static void Style(Control control)
        {
            Snapshot original = _originals.GetValue(control, Snapshot.Take);
            if (!_enabled)
            {
                original.Restore(control);
                PaintOffScreen(control as ListView);
                return;
            }

            // Every control gets an explicit font, so nothing depends on what its parent happens to be. Containers
            // (forms, user controls) keep theirs: changing a container's font makes it auto-scale its layout.
            if (!(control is ContainerControl))
            {
                control.Font = ThemeFont(original.Font, control);
            }

            if (control is Form)
            {
                control.BackColor = Parchment;
                control.BackgroundImage = ParchmentTexture;
                control.BackgroundImageLayout = ImageLayout.Tile;
                control.ForeColor = Ink;
            }
            else if (control is ThemedTabControl themedTabs)
            {
                themedTabs.Themed = true;
            }
            else if (control is TabPage page)
            {
                page.UseVisualStyleBackColor = false;
                Opaque(page, ParchmentTexture, Parchment);
                page.ForeColor = Ink;
            }
            else if (control is Button button)
            {
                StyleButton(button);
            }
            else if (control is TextBox textBox)
            {
                textBox.BackColor = ParchmentLight;
                textBox.ForeColor = Ink;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = ParchmentLight;
                combo.ForeColor = Ink;
            }
            else if (control is ListView listView)
            {
                StyleListView(listView);
            }
            else if (control is LinkLabel link)
            {
                link.BackColor = Color.Transparent;
                link.LinkColor = Crimson;
                link.ActiveLinkColor = GoldDark;
                link.VisitedLinkColor = Crimson;
                link.ForeColor = Ink;
            }
            else if (control is GroupBox group)
            {
                Opaque(group, ParchmentTexture, Parchment);
                group.ForeColor = Crimson;
                GroupBoxHooks hooks;
                if (!_groupBoxHooks.TryGetValue(group, out hooks))
                {
                    hooks = new GroupBoxHooks(group);
                    _groupBoxHooks.Add(group, hooks);
                }
                hooks.Attach();
            }
            else if (control is Label label)
            {
                bool isBanner = original.BackColor == SystemColors.Highlight;
                label.BackColor = isBanner ? Leather : Color.Transparent;
                label.ForeColor = isBanner ? GoldLight : (original.ForeColor == SystemColors.GrayText ? InkFaded : Ink);
                if (!label.AutoSize && label.Dock == DockStyle.Top && label.Text.Length > 60 && label.Width > 0)
                {
                    // Paragraph labels were sized for the system font; give them the height the serif needs.
                    int width = label.Width - label.Padding.Horizontal;
                    int needed = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    label.Height = Math.Max(label.Height, needed + label.Padding.Vertical + 4);
                }
            }
            else if (control is CheckBox || control is RadioButton)
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = Ink;
            }
            else if (control is ProgressBar)
            {
                control.BackColor = ParchmentLight;
                control.ForeColor = GoldDark;
            }
            else if (control is Panel panel)
            {
                bool isBottomBar = panel.Parent is Form && panel.Dock == DockStyle.Bottom;
                bool isTip = original.BackColor == SystemColors.Info;
                if (isBottomBar)
                {
                    Opaque(panel, LeatherTexture, Leather);
                }
                else if (isTip)
                {
                    Opaque(panel, null, ParchmentDark);
                }
                else
                {
                    Opaque(panel, ParchmentTexture, Parchment);
                }
                panel.ForeColor = Ink;
            }
            else if (control is UserControl || control is TableLayoutPanel || control is FlowLayoutPanel || control is SplitContainer)
            {
                Opaque(control, ParchmentTexture, Parchment);
                control.ForeColor = Ink;
            }
        }

        /// <summary>
        /// Containers paint their own texture rather than showing through to their parent. A transparent
        /// container makes every one of its children repaint the whole chain of parents above it, which is
        /// what made a page take so long to draw; an opaque one is painted once, double-buffered.
        /// </summary>
        /// <summary>
        /// Lists draw into a buffer in either look. Without it, dragging a column edge or resizing the window erases
        /// and redraws every row on each mouse move, and the text flashes.
        /// </summary>
        private static void PaintOffScreen(ListView listView)
        {
            if (listView != null) SetDoubleBuffered(listView, true);
        }

        private static void Opaque(Control control, Image texture, Color colour)
        {
            control.BackColor = colour;
            control.BackgroundImage = texture;
            control.BackgroundImageLayout = ImageLayout.Tile;
            SetDoubleBuffered(control, true);
        }

        private static readonly System.Reflection.PropertyInfo DoubleBufferedProperty =
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void SetDoubleBuffered(Control control, bool value)
        {
            if (DoubleBufferedProperty != null) DoubleBufferedProperty.SetValue(control, value, null);
        }

        private static bool GetDoubleBuffered(Control control)
        {
            return DoubleBufferedProperty != null && (bool)DoubleBufferedProperty.GetValue(control, null);
        }

        /// <summary>
        /// The serif that replaces a control's original font: bold for buttons and group titles, the original
        /// size for large headings (the wizard title, the About banner), the body size otherwise. Lists keep
        /// their font because their rows carry fonts of their own.
        /// </summary>
        private static Font ThemeFont(Font original, Control control)
        {
            if (control is ListView) return original;
            if (control is ThemedTabControl) return HeadingFont;
            if (control is Button) return ButtonFont(control.Height);
            if (control is GroupBox) return HeadingFont;
            if (original.Size >= 12f) return new Font(HeadingFontFamily, original.Size, FontStyle.Bold, GraphicsUnit.Point);
            if (original.Style != FontStyle.Regular) return new Font(BodyFont, original.Style);
            return BodyFont;
        }

        private static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = Leather;
            button.ForeColor = GoldLight;
            button.FlatAppearance.BorderColor = Gold;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = LeatherLight;
            button.FlatAppearance.MouseDownBackColor = LeatherDark;
            ButtonHooks hooks;
            if (!_buttonHooks.TryGetValue(button, out hooks))
            {
                hooks = new ButtonHooks(button);
                _buttonHooks.Add(button, hooks);
            }
            hooks.Attach();
        }

        private static void StyleListView(ListView listView)
        {
            // The border is left alone: changing it alters the client width, and the account list
            // redistributes its columns on every client size change, losing a little to rounding each time.
            listView.BackColor = ParchmentLight;
            listView.ForeColor = Ink;
            PaintOffScreen(listView);
            ListViewHooks hooks;
            if (!_listViewHooks.TryGetValue(listView, out hooks))
            {
                hooks = new ListViewHooks(listView);
                _listViewHooks.Add(listView, hooks);
            }
            hooks.Attach();
        }

        private static void ApplyWindowChrome(Form form)
        {
            if (form.IsHandleCreated)
            {
                SetDarkTitleBar(form.Handle, _enabled);
                SetComposited(form.Handle, _enabled);
            }
            else
            {
                form.HandleCreated += (sender, e) => { SetDarkTitleBar(form.Handle, _enabled); SetComposited(form.Handle, _enabled); };
            }
        }

        /// <summary>
        /// With the themed look every control is painted into one off-screen surface and shown together
        /// (WS_EX_COMPOSITED), so switching tabs no longer shows the page building up control by control.
        /// Classic keeps the ordinary window style.
        /// </summary>
        private static void SetComposited(IntPtr handle, bool on)
        {
            long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            long wanted = on ? (style | WsExComposited) : (style & ~WsExComposited);
            if (wanted != style)
            {
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(wanted));
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
        }

        private const int GwlExStyle = -20;
        private const long WsExComposited = 0x02000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        private static void SetDarkTitleBar(IntPtr handle, bool dark)
        {
            try
            {
                int useDark = dark ? 1 : 0;
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDark, sizeof(int));
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    // Windows 11 lets the caption take the leather colour; Windows 10 has no such attributes.
                    int caption = dark ? ToColorRef(Leather) : unchecked((int)0xFFFFFFFF);
                    int text = dark ? ToColorRef(GoldLight) : unchecked((int)0xFFFFFFFF);
                    DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
                    DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
                }

                // The caption keeps its old pixels until the window is activated or deactivated, so it would
                // show a stale highlight after a switch. Tell it the frame changed and flip its activation
                // state and back, which repaints the caption in place.
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                Form active = Form.ActiveForm;
                bool isActive = active != null && active.IsHandleCreated && active.Handle == handle;
                SendMessage(handle, WmNcActivate, new IntPtr(isActive ? 0 : 1), IntPtr.Zero);
                SendMessage(handle, WmNcActivate, new IntPtr(isActive ? 1 : 0), IntPtr.Zero);
            }
            catch (Exception)
            {
                // Older Windows without DWM support: the title bar simply keeps the system look.
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        /// <summary>Fills a rectangle with the leather texture.</summary>
        public static void FillLeather(Graphics g, Rectangle bounds)
        {
            using (TextureBrush brush = new TextureBrush(LeatherTexture, WrapMode.Tile))
            {
                g.FillRectangle(brush, bounds);
            }
        }

        /// <summary>Fills a rectangle with the parchment texture.</summary>
        public static void FillParchment(Graphics g, Rectangle bounds)
        {
            using (TextureBrush brush = new TextureBrush(ParchmentTexture, WrapMode.Tile))
            {
                g.FillRectangle(brush, bounds);
            }
        }

        private static Image LoadTexture(string fileName, Color fallback)
        {
            string resourceName = "AowEmailWrapper.Resources.Theme." + fileName;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (Image image = Image.FromStream(stream))
                    {
                        return new Bitmap(image);
                    }
                }
            }

            Bitmap plain = new Bitmap(8, 8);
            using (Graphics g = Graphics.FromImage(plain))
            {
                g.Clear(fallback);
            }
            return plain;
        }

        /// <summary>What a control looked like before the theme touched it.</summary>
        private sealed class Snapshot
        {
            private Color _backColor;
            private Color _foreColor;
            private Font _font;
            private Image _backgroundImage;
            private ImageLayout _backgroundImageLayout;
            private FlatStyle _flatStyle;
            private bool _useVisualStyleBackColor;
            private BorderStyle _borderStyle;
            private bool _hasBorderStyle;
            private Color _linkColor, _activeLinkColor, _visitedLinkColor;

            public Color BackColor { get { return _backColor; } }
            public Color ForeColor { get { return _foreColor; } }
            public Font Font { get { return _font; } }

            public static Snapshot Take(Control control)
            {
                Snapshot s = new Snapshot();
                s._backColor = control.BackColor;
                s._foreColor = control.ForeColor;
                s._font = control.Font;
                s._backgroundImage = control.BackgroundImage;
                s._backgroundImageLayout = control.BackgroundImageLayout;
                s._doubleBuffered = GetDoubleBuffered(control);
                if (control is Button b) { s._flatStyle = b.FlatStyle; s._useVisualStyleBackColor = b.UseVisualStyleBackColor; }
                if (control is ComboBox c) { s._flatStyle = c.FlatStyle; }
                if (control is TabPage p) { s._useVisualStyleBackColor = p.UseVisualStyleBackColor; s._borderStyle = p.BorderStyle; s._hasBorderStyle = true; }
                if (control is TextBox t) { s._borderStyle = t.BorderStyle; s._hasBorderStyle = true; }
                if (control is ListView l) { s._borderStyle = l.BorderStyle; s._hasBorderStyle = true; }
                if (control is LinkLabel k) { s._linkColor = k.LinkColor; s._activeLinkColor = k.ActiveLinkColor; s._visitedLinkColor = k.VisitedLinkColor; }
                if (control is Label lb) { s._height = lb.Height; }
                return s;
            }

            private int _height;
            private bool _doubleBuffered;

            public void Restore(Control control)
            {
                control.BackColor = _backColor;
                control.ForeColor = _foreColor;
                control.Font = _font;
                control.BackgroundImage = _backgroundImage;
                control.BackgroundImageLayout = _backgroundImageLayout;
                if (GetDoubleBuffered(control) != _doubleBuffered) SetDoubleBuffered(control, _doubleBuffered);
                if (control is ThemedTabControl tabs) { tabs.Themed = false; }
                if (control is Button b)
                {
                    b.FlatStyle = _flatStyle; b.UseVisualStyleBackColor = _useVisualStyleBackColor;
                    ButtonHooks hooks;
                    if (_buttonHooks.TryGetValue(b, out hooks)) { hooks.Detach(); }
                }
                if (control is ComboBox c) { c.FlatStyle = _flatStyle; }
                if (control is TabPage p) { p.UseVisualStyleBackColor = _useVisualStyleBackColor; p.BorderStyle = _borderStyle; }
                if (control is TextBox t && _hasBorderStyle) { t.BorderStyle = _borderStyle; }
                if (control is ListView l)
                {
                    if (_hasBorderStyle) { l.BorderStyle = _borderStyle; }
                    ListViewHooks hooks;
                    if (_listViewHooks.TryGetValue(l, out hooks)) { hooks.Detach(); }
                }
                if (control is LinkLabel k) { k.LinkColor = _linkColor; k.ActiveLinkColor = _activeLinkColor; k.VisitedLinkColor = _visitedLinkColor; }
                if (control is Label lb && !lb.AutoSize && lb.Dock == DockStyle.Top && lb.Height != _height) { lb.Height = _height; }
                if (control is GroupBox gb)
                {
                    GroupBoxHooks hooks;
                    if (_groupBoxHooks.TryGetValue(gb, out hooks)) { hooks.Detach(); }
                }
            }
        }

        /// <summary>
        /// A flat button paints its disabled text in dark grey, which is unreadable on leather. This repaints a
        /// disabled button in dimmed gold on darker leather so every button reads the same way.
        /// </summary>
        private sealed class ButtonHooks
        {
            private readonly Button _button;
            private bool _attached;

            public ButtonHooks(Button button)
            {
                _button = button;
            }

            public void Attach()
            {
                if (_attached) return;
                _attached = true;
                _button.Paint += Paint;
                _button.EnabledChanged += EnabledChanged;
            }

            public void Detach()
            {
                if (!_attached) return;
                _attached = false;
                _button.Paint -= Paint;
                _button.EnabledChanged -= EnabledChanged;
            }

            private void EnabledChanged(object sender, EventArgs e)
            {
                _button.Invalidate();
            }

            private void Paint(object sender, PaintEventArgs e)
            {
                if (_button.Enabled) return;
                Rectangle bounds = _button.ClientRectangle;
                using (SolidBrush fill = new SolidBrush(LeatherDark))
                {
                    e.Graphics.FillRectangle(fill, bounds);
                }
                using (Pen border = new Pen(GoldDark))
                {
                    e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                }
                if (_button.Image != null)
                {
                    ControlPaint.DrawImageDisabled(e.Graphics, _button.Image,
                        bounds.X + (bounds.Width - _button.Image.Width) / 2, bounds.Y + (bounds.Height - _button.Image.Height) / 2, LeatherDark);
                }
                TextRenderer.DrawText(e.Graphics, _button.Text, _button.Font, bounds, GoldDark,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>
        /// The system group box draws a pale etched line that disappears on parchment. This paints a gold frame
        /// and the title in the heading serif over it, after the control has painted itself.
        /// </summary>
        private sealed class GroupBoxHooks
        {
            private readonly GroupBox _group;
            private bool _attached;

            public GroupBoxHooks(GroupBox group)
            {
                _group = group;
            }

            public void Attach()
            {
                if (_attached) return;
                _attached = true;
                _group.Paint += Paint;
            }

            public void Detach()
            {
                if (!_attached) return;
                _attached = false;
                _group.Paint -= Paint;
                _group.Invalidate();
            }

            private void Paint(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Font font = _group.Font;
                Size title = string.IsNullOrEmpty(_group.Text) ? Size.Empty : TextRenderer.MeasureText(g, _group.Text, font, Size.Empty, TextFormatFlags.NoPrefix);
                int top = title.IsEmpty ? 0 : title.Height / 2;
                Rectangle frame = new Rectangle(0, top, _group.Width - 1, _group.Height - top - 1);

                // Paint out the system frame and title, then draw ours.
                using (TextureBrush parchment = new TextureBrush(ParchmentTexture, WrapMode.Tile))
                {
                    g.FillRectangle(parchment, new Rectangle(0, 0, _group.Width, Math.Max(top + 2, title.Height)));
                    g.FillRectangle(parchment, new Rectangle(0, 0, 2, _group.Height));
                    g.FillRectangle(parchment, new Rectangle(_group.Width - 2, 0, 2, _group.Height));
                    g.FillRectangle(parchment, new Rectangle(0, _group.Height - 2, _group.Width, 2));
                }
                using (Pen gold = new Pen(GoldDark))
                {
                    if (title.IsEmpty)
                    {
                        g.DrawRectangle(gold, frame);
                    }
                    else
                    {
                        int textLeft = 8;
                        int textRight = Math.Min(_group.Width - 4, textLeft + title.Width + 4);
                        g.DrawLine(gold, frame.Left, frame.Top, textLeft - 3, frame.Top);
                        g.DrawLine(gold, textRight, frame.Top, frame.Right, frame.Top);
                        g.DrawLine(gold, frame.Left, frame.Top, frame.Left, frame.Bottom);
                        g.DrawLine(gold, frame.Right, frame.Top, frame.Right, frame.Bottom);
                        g.DrawLine(gold, frame.Left, frame.Bottom, frame.Right, frame.Bottom);
                        TextRenderer.DrawText(g, _group.Text, font, new Point(textLeft, 0), _group.ForeColor, TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        /// <summary>Owner-draws list view column headers in leather and gold, and selected rows in gold.</summary>
        private sealed class ListViewHooks
        {
            private readonly ListView _listView;
            private bool _attached;

            public ListViewHooks(ListView listView)
            {
                _listView = listView;
            }

            public bool Attached
            {
                get { return _attached; }
            }

            public void Attach()
            {
                if (_attached) return;
                _attached = true;
                _listView.OwnerDraw = true;
                _listView.DrawColumnHeader += DrawColumnHeader;
                _listView.DrawItem += DrawDefault;
                _listView.DrawSubItem += DrawSubItemDefault;
                //The headings are now drawn in the heading serif, wider than the list's own font they were sized for
                ListViewColumnResizer.ResizeColumns(_listView);
            }

            public void Detach()
            {
                if (!_attached) return;
                _attached = false;
                _listView.DrawColumnHeader -= DrawColumnHeader;
                _listView.DrawItem -= DrawDefault;
                _listView.DrawSubItem -= DrawSubItemDefault;
                _listView.OwnerDraw = false;
                ListViewColumnResizer.ResizeColumns(_listView);
            }

            private static void DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                FillLeather(e.Graphics, e.Bounds);
                using (Pen pen = new Pen(GoldDark))
                {
                    e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                    e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
                Rectangle textBounds = e.Bounds;
                textBounds.Inflate(-4, 0);
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
                else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(e.Graphics, e.Header.Text, HeadingFont, textBounds, GoldLight, flags);
            }

            private static void DrawDefault(object sender, DrawListViewItemEventArgs e)
            {
                // Unselected rows keep their own colours and fonts; a selected row is painted in gold below.
                if (!e.Item.Selected || e.Item.ListView.View != View.Details)
                {
                    e.DrawDefault = true;
                    return;
                }
                using (SolidBrush fill = new SolidBrush(GoldLight))
                {
                    e.Graphics.FillRectangle(fill, e.Bounds);
                }
            }

            private static void DrawSubItemDefault(object sender, DrawListViewSubItemEventArgs e)
            {
                if (!e.Item.Selected)
                {
                    e.DrawDefault = true;
                    return;
                }
                Rectangle bounds = e.Bounds;
                ListView list = e.Item.ListView;
                if (e.ColumnIndex == 0)
                {
                    if (list.CheckBoxes)
                    {
                        Rectangle box = new Rectangle(bounds.X + 4, bounds.Y + (bounds.Height - 13) / 2, 13, 13);
                        ControlPaint.DrawCheckBox(e.Graphics, box, e.Item.Checked ? ButtonState.Checked | ButtonState.Flat : ButtonState.Flat);
                        bounds.X += 20; bounds.Width -= 20;
                    }
                    Image image = ItemImage(list, e.Item);
                    if (image != null)
                    {
                        // Same geometry as the default drawing: icon 4 px in, text 3 px after it.
                        e.Graphics.DrawImage(image, bounds.X + 4, bounds.Y + (bounds.Height - image.Height) / 2, image.Width, image.Height);
                        bounds.X += image.Width + 7; bounds.Width -= image.Width + 7;
                    }
                    else
                    {
                        bounds.X += 4; bounds.Width -= 4;
                    }
                }
                else
                {
                    bounds.X += 6; bounds.Width -= 6;
                }
                Font font = e.Item.UseItemStyleForSubItems ? e.Item.Font : e.SubItem.Font;
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                HorizontalAlignment align = list.Columns[e.ColumnIndex].TextAlign;
                if (align == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
                else if (align == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, bounds, Ink, flags);
            }

            private static Image ItemImage(ListView list, ListViewItem item)
            {
                ImageList images = list.SmallImageList;
                if (images == null) return null;
                if (!string.IsNullOrEmpty(item.ImageKey) && images.Images.ContainsKey(item.ImageKey)) return images.Images[item.ImageKey];
                if (item.ImageIndex >= 0 && item.ImageIndex < images.Images.Count) return images.Images[item.ImageIndex];
                return null;
            }
        }

        /// <summary>Menus in leather with gold text.</summary>
        private sealed class AowToolStripRenderer : ToolStripProfessionalRenderer
        {
            public AowToolStripRenderer() : base(new AowColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Ink : GoldLight) : GoldDark;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                FillLeather(e.Graphics, e.AffectedBounds);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                FillLeather(e.Graphics, e.AffectedBounds);
            }
        }

        private sealed class AowColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Gold; } }
            public override Color MenuItemSelectedGradientBegin { get { return GoldLight; } }
            public override Color MenuItemSelectedGradientEnd { get { return Gold; } }
            public override Color MenuItemBorder { get { return GoldDark; } }
            public override Color MenuItemPressedGradientBegin { get { return LeatherLight; } }
            public override Color MenuItemPressedGradientEnd { get { return LeatherLight; } }
            public override Color MenuBorder { get { return Gold; } }
            public override Color ToolStripDropDownBackground { get { return Leather; } }
            public override Color ImageMarginGradientBegin { get { return Leather; } }
            public override Color ImageMarginGradientMiddle { get { return Leather; } }
            public override Color ImageMarginGradientEnd { get { return Leather; } }
            public override Color SeparatorDark { get { return GoldDark; } }
            public override Color SeparatorLight { get { return LeatherLight; } }
            public override Color CheckBackground { get { return GoldLight; } }
            public override Color CheckSelectedBackground { get { return GoldLight; } }
            public override Color CheckPressedBackground { get { return Gold; } }
            public override Color ButtonSelectedBorder { get { return Gold; } }
        }
    }
}
