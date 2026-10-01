using System;
using System.Drawing;
using System.Windows.Forms;

namespace AowEmailWrapper.Controls
{
    /// <summary>
    /// Menu item with the game icon on the left and an optional status icon on the right.
    /// </summary>
    public class IconMenuItem : ToolStripMenuItem
    {
        private const int EndImagePadding = 6;

        private Image _endImage;
        private bool _showEndImage;

        public bool ShowEndImage
        {
            get { return _showEndImage; }
            set { _showEndImage = value; Invalidate(); }
        }

        public Image StartImage
        {
            get { return Image; }
            set { Image = value; }
        }

        public Image EndImage
        {
            get { return _endImage; }
            set { _endImage = value; Invalidate(); }
        }

        public IconMenuItem(string text, Image startImage, Image endImage)
            : base(text, startImage)
        {
            _endImage = endImage;
            ImageScaling = ToolStripItemImageScaling.None;
        }

        public IconMenuItem(string text, Image startImage, Image endImage, Font font)
            : this(text, startImage, endImage)
        {
            Font = font;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_showEndImage && _endImage != null)
            {
                int x = Width - _endImage.Width - EndImagePadding;
                int y = (Height - _endImage.Height) / 2;
                e.Graphics.DrawImage(_endImage, x, y, _endImage.Width, _endImage.Height);
            }
        }
    }
}
