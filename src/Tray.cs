using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZapretLauncher
{
    // Значок в трее: цветной — обход работает, серый — выключен. Тёмное контекстное меню.
    class Tray : IDisposable
    {
        readonly NotifyIcon icon;
        readonly ToolStripMenuItem toggle;
        readonly Icon iconOn = Draw(true), iconOff = Draw(false);

        static readonly Color Text = Color.FromArgb(232, 235, 242);

        public Tray(Action open, Action toggleAct, Action exit)
        {
            var font = new Font("Segoe UI", 9.5f);
            var menu = new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new Dark()) { RoundedEdges = false },
                ShowImageMargin = false, Font = font, ForeColor = Text, Padding = new Padding(2, 4, 2, 4)
            };
            toggle = new ToolStripMenuItem("Включить", null, (s, e) => toggleAct());
            menu.Items.Add(new ToolStripMenuItem("Открыть", null, (s, e) => open()) { Font = new Font(font, FontStyle.Bold) });
            menu.Items.Add(toggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => exit()));
            foreach (ToolStripItem i in menu.Items) { i.ForeColor = Text; i.Padding = new Padding(8, 3, 16, 3); }

            icon = new NotifyIcon { Icon = iconOff, Text = "Zapret Launcher", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) open(); };
        }

        public void Update(bool on, bool canToggle, string tip)
        {
            icon.Icon = on ? iconOn : iconOff;
            toggle.Text = on ? "Выключить" : "Включить";
            toggle.Enabled = canToggle;
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        public void Balloon(string title, string text)
        {
            icon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        // Тот же логотип, что в окне: скруглённый квадрат, щит, молния
        static Icon Draw(bool on)
        {
            const int S = 32;
            var bmp = new Bitmap(S, S);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(1, 1, S - 2, S - 2);
                using (var path = Rounded(r, 9))
                using (Brush bg = on ? (Brush)new LinearGradientBrush(r, Color.FromArgb(46, 230, 168), Color.FromArgb(59, 130, 246), 45f)
                                     : new SolidBrush(Color.FromArgb(72, 79, 96)))
                    g.FillPath(bg, path);

                Func<float, float, PointF> p = (x, y) => new PointF(5 + x * 22f / 24, 5 + y * 22f / 24);
                var shield = new[] { p(12, 1.5f), p(21, 5), p(21, 11.5f), p(20.5f, 14.5f), p(19, 17.3f), p(16, 20.3f), p(12, 22.5f),
                                     p(8, 20.3f), p(5, 17.3f), p(3.5f, 14.5f), p(3, 11.5f), p(3, 5) };
                var bolt = new[] { p(13.2f, 5.5f), p(8, 13), p(11.6f, 13), p(10.6f, 18.5f), p(16, 10.8f), p(12.4f, 10.8f) };
                using (var b = new SolidBrush(Color.FromArgb(225, 6, 17, 13))) g.FillPolygon(b, shield);
                using (var b = new SolidBrush(on ? Color.FromArgb(46, 230, 168) : Color.FromArgb(150, 158, 175))) g.FillPolygon(b, bolt);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        class Dark : ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(21, 25, 35), Hover = Color.FromArgb(36, 43, 58), Line = Color.FromArgb(42, 50, 69);
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Bg; } }
        }
    }
}
