using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WorkHours
{
    public enum TrayLook { Stopped, Running, Overtime, Paused }

    /// <summary>The notification-area icon. Its look doubles as the status indicator.</summary>
    public class Tray : IDisposable
    {
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        string key;

        public event EventHandler OpenRequested, BalloonClicked;
        public readonly ToolStripMenuItem OpenItem, StartItem, PauseItem, StopItem, QuitItem;
        public ContextMenuStrip Menu { get { return menu; } }

        public Tray()
        {
            OpenItem = new ToolStripMenuItem("Open Clocked In");
            OpenItem.Font = new Font(OpenItem.Font, FontStyle.Bold);
            StartItem = new ToolStripMenuItem("Start");
            PauseItem = new ToolStripMenuItem("Pause");
            StopItem = new ToolStripMenuItem("Stop");
            QuitItem = new ToolStripMenuItem("Quit");
            menu.Items.AddRange(new ToolStripItem[] {
                OpenItem, new ToolStripSeparator(), StartItem, PauseItem, StopItem, new ToolStripSeparator(), QuitItem });
            icon.ContextMenuStrip = menu;
            icon.Text = "Clocked In";
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && OpenRequested != null) OpenRequested(this, EventArgs.Empty);
            };
            icon.BalloonTipClicked += (s, e) => { if (BalloonClicked != null) BalloonClicked(this, EventArgs.Empty); };
        }

        public void Show() { icon.Visible = true; }

        public void SetText(string t)
        {
            if (t.Length > 63) t = t.Substring(0, 63);
            if (icon.Text != t) icon.Text = t;
        }

        public void Notify(string title, string text)
        {
            icon.ShowBalloonTip(6000, title, text, ToolTipIcon.None);
        }

        public void SetLook(TrayLook look, bool taskbarDark)
        {
            int size = SystemInformation.SmallIconSize.Width;
            string k = look + "|" + taskbarDark + "|" + size;
            if (k == key) return;
            key = k;
            using (var bmp = Render(look, taskbarDark, size))
            {
                var old = icon.Icon;
                icon.Icon = ToIcon(bmp);
                if (old != null) old.Dispose();
            }
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>Draws at 4x and scales down with bicubic filtering for smooth, crisp edges.</summary>
        static Bitmap Render(TrayLook look, bool tbDark, int size)
        {
            const int SS = 4;
            int S = size * SS;
            using (var big = new Bitmap(S, S, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    DrawGlyph(g, look, tbDark, S);
                }
                var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
                        g.DrawImage(big, new Rectangle(0, 0, size, size), 0, 0, S, S, GraphicsUnit.Pixel, ia);
                    }
                }
                return small;
            }
        }

        /// <summary>
        /// The "CL" clock logo: the C is a clock face open on the right, the L is the hands at 3 o'clock.
        /// The state is shown by its color (and pause bars instead of the L when paused).
        /// </summary>
        static void DrawGlyph(Graphics g, TrayLook look, bool tbDark, float S)
        {
            Color color;
            switch (look)
            {
                case TrayLook.Running: color = tbDark ? Color.FromArgb(0x4A, 0xDE, 0x6E) : Color.FromArgb(0x17, 0x9C, 0x42); break;
                case TrayLook.Paused: color = tbDark ? Color.FromArgb(0xFF, 0xC0, 0x24) : Color.FromArgb(0xD0, 0x86, 0x00); break;
                case TrayLook.Overtime: color = tbDark ? Color.FromArgb(0xFF, 0x63, 0x57) : Color.FromArgb(0xD6, 0x35, 0x2B); break;
                default: color = tbDark ? Color.White : Color.FromArgb(0x20, 0x20, 0x20); break;
            }
            DrawLogo(g, S, color, look == TrayLook.Paused);
        }

        /// <summary>Shared logo geometry (also used for the app icon). S = canvas size in pixels.</summary>
        public static void DrawLogo(Graphics g, float S, Color color, bool paused)
        {
            float c = S / 2f;
            float w = S * 0.135f;                 // stroke: thick, like the sketch
            float r = S * 0.5f - w / 2 - S * 0.03f;
            using (var pen = new Pen(color, w))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                // C: clock face with the opening on the right (from 4:30 round to 1:30)
                g.DrawArc(pen, c - r, c - r, 2 * r, 2 * r, 42, 276);

                if (paused)
                {
                    float bh = S * 0.28f, gap = S * 0.095f;
                    float x = c + S * 0.005f;
                    g.DrawLine(pen, x - gap, c - bh / 2, x - gap, c + bh / 2);
                    g.DrawLine(pen, x + gap, c - bh / 2, x + gap, c + bh / 2);
                }
                else
                {
                    // L: the hands at 3 o'clock, one continuous stroke so the corner is clean
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(c - S * 0.03f, c - S * 0.235f),   // top of the L (12 o'clock hand)
                        new PointF(c - S * 0.03f, c + S * 0.015f),   // corner at the center
                        new PointF(c + S * 0.215f, c + S * 0.015f)   // reaching toward the C's opening
                    });
                }
            }
        }

        static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var p = new GraphicsPath())
            {
                float d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        /// <summary>
        /// Wraps a 32-bit ARGB bitmap in a real .ico (with alpha), instead of Bitmap.GetHicon(),
        /// which drops the soft alpha edges and makes the icon look jagged.
        /// </summary>
        static Icon ToIcon(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            int xorSize = w * h * 4, andSize = maskStride * h;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new byte[xorSize];
            try
            {
                for (int y = 0; y < h; y++)   // .ico bitmaps are stored bottom-up
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, (h - 1 - y) * w * 4, w * 4);
            }
            finally { bmp.UnlockBits(data); }

            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)1);              // ICONDIR
            bw.Write((byte)(w >= 256 ? 0 : w)); bw.Write((byte)(h >= 256 ? 0 : h));  // ICONDIRENTRY
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(40 + xorSize + andSize); bw.Write(6 + 16);
            bw.Write(40); bw.Write(w); bw.Write(h * 2);                              // BITMAPINFOHEADER
            bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(xorSize + andSize);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(pixels);
            bw.Write(new byte[andSize]);                                             // mask unused: alpha wins
            bw.Flush();
            ms.Position = 0;
            return new Icon(ms, w, h);
        }

        public void Dispose()
        {
            icon.Visible = false;
            var ic = icon.Icon;
            icon.Dispose();
            if (ic != null) ic.Dispose();
            menu.Dispose();
        }
    }
}
