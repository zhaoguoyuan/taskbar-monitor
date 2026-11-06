using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TaskbarMonitor.Counters;

namespace TaskbarMonitor
{
    /// <summary>
    /// Borderless, non-activating, click-through popup listing the top processes for a counter.
    /// </summary>
    internal sealed class TopProcessPopup : Form
    {
        private string title = "";
        private string message = null;
        private List<ProcessSampler.Entry> entries = new List<ProcessSampler.Entry>();
        private ProcessSampler.Metric metric;

        private readonly Font font;
        private readonly Font fontBold;
        private Color backColor = Color.FromArgb(32, 32, 32);
        private Color foreColor = Color.FromArgb(240, 240, 240);
        private Color dimColor = Color.FromArgb(160, 160, 160);
        private Color borderColor = Color.FromArgb(70, 70, 70);
        private Color stripeColor = Color.FromArgb(40, 40, 40);
        private Color lowColor = Color.FromArgb(108, 203, 95);
        private Color midColor = Color.FromArgb(252, 185, 0);
        private Color highColor = Color.FromArgb(255, 99, 99);
        private bool dark = true;

        private Rectangle anchor;
        private const int MaxNameWidth = 220;

        public TopProcessPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            font = new Font("Segoe UI", 9f, FontStyle.Regular);
            fontBold = new Font("Segoe UI", 9f, FontStyle.Bold);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOPMOST = 0x00000008;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int CS_DROPSHADOW = 0x00020000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_MOUSEACTIVATE = 0x0021;
            const int HTTRANSPARENT = -1;
            const int MA_NOACTIVATE = 3;
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        public void SetDark(bool dark)
        {
            this.dark = dark;
            if (dark)
            {
                backColor = Color.FromArgb(32, 32, 32);
                foreColor = Color.FromArgb(240, 240, 240);
                dimColor = Color.FromArgb(140, 140, 140);
                borderColor = Color.FromArgb(70, 70, 70);
                stripeColor = Color.FromArgb(42, 42, 42);
                lowColor = Color.FromArgb(108, 203, 95);
                midColor = Color.FromArgb(252, 185, 0);
                highColor = Color.FromArgb(255, 99, 99);
            }
            else
            {
                backColor = Color.FromArgb(249, 249, 249);
                foreColor = Color.FromArgb(20, 20, 20);
                dimColor = Color.FromArgb(120, 120, 120);
                borderColor = Color.FromArgb(200, 200, 200);
                stripeColor = Color.FromArgb(238, 238, 238);
                lowColor = Color.FromArgb(16, 124, 16);
                midColor = Color.FromArgb(180, 100, 0);
                highColor = Color.FromArgb(196, 43, 28);
            }
            Invalidate();
        }

        // 0 = idle, 1 = low (green), 2 = medium (orange), 3 = high (red)
        private static int Level(ProcessSampler.Metric metric, double v)
        {
            const double MB = 1024d * 1024;
            double mid, high;
            switch (metric)
            {
                case ProcessSampler.Metric.CPU: mid = 5; high = 25; break;          // % of total CPU
                case ProcessSampler.Metric.GPU: mid = 10; high = 50; break;         // %
                case ProcessSampler.Metric.DISK: mid = 1 * MB; high = 10 * MB; break; // per second
                default: mid = 500 * MB; high = 2048 * MB; break;                   // MEM / GPU MEM
            }
            if (v <= 0 || (metric == ProcessSampler.Metric.CPU && v < 0.05)) return 0;
            if (v >= high) return 3;
            if (v >= mid) return 2;
            return 1;
        }

        private Color LevelColor(int level)
        {
            switch (level)
            {
                case 3: return highColor;
                case 2: return midColor;
                case 1: return lowColor;
                default: return dimColor;
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Set content. entries == null shows the "measuring" message.</summary>
        public void SetContent(string counterLabel, ProcessSampler.Metric metric, List<ProcessSampler.Entry> entries)
        {
            this.title = "Top processes - " + counterLabel;
            this.metric = metric;
            if (entries == null)
            {
                this.entries = new List<ProcessSampler.Entry>();
                this.message = "Measuring...";
            }
            else
            {
                this.entries = entries;
                this.message = entries.Count == 0 ? "No activity" : null;
            }
            UpdateLayoutAndPosition();
            Invalidate();
        }

        public void ShowAt(Rectangle anchorScreen)
        {
            anchor = anchorScreen;
            UpdateLayoutAndPosition();
            if (!Visible)
                Show();
            SetWindowPos(Handle, HWND_TOPMOST, Left, Top, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private float DpiScale => DeviceDpi / 96f;

        /// <summary>Number of rows the popup always reserves.</summary>
        public int RowCount { get; set; } = 10;

        private int stableWidth = 0;

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible)
                stableWidth = 0;
            base.OnVisibleChanged(e);
        }

        private static string EntryName(ProcessSampler.Entry e)
        {
            return e.Count > 1 ? $"{e.Name} ({e.Count})" : e.Name;
        }

        private void UpdateLayoutAndPosition()
        {
            int pad = (int)(8 * DpiScale);
            int gap = (int)(16 * DpiScale);
            int maxName = (int)(MaxNameWidth * DpiScale);

            var titleSize = TextRenderer.MeasureText(title, fontBold);
            int lineH = RowHeight;
            int nameW = 0, valueW = 0;
            foreach (var e in entries)
            {
                nameW = Math.Max(nameW, Math.Min(maxName, TextRenderer.MeasureText(EntryName(e), font).Width));
                valueW = Math.Max(valueW, TextRenderer.MeasureText(ProcessSampler.Format(metric, e.Value), fontBold).Width);
            }
            int bodyW = entries.Count > 0 ? RankWidth + nameW + gap + valueW : 0;
            if (message != null)
                bodyW = Math.Max(bodyW, TextRenderer.MeasureText(message, font).Width);

            // fixed height (RowCount rows) and grow-only width while shown, so the popup does not jitter
            int rows = Math.Max(RowCount, message != null ? 1 : entries.Count);
            int w = Math.Max((int)(180 * DpiScale), Math.Max(titleSize.Width, bodyW) + pad * 2);
            w = Math.Max(w, stableWidth);
            stableWidth = w;
            int h = pad + titleSize.Height + (int)(4 * DpiScale) + rows * lineH + pad;
            Size = new Size(w, h);

            if (anchor.IsEmpty) return;

            var screen = Screen.FromRectangle(anchor);
            var wa = screen.WorkingArea;
            var b = screen.Bounds;
            int margin = (int)(6 * DpiScale);
            int x, y;
            if (anchor.Right <= wa.Left + 1)            // taskbar on the left
            {
                x = anchor.Right + margin;
                y = anchor.Top;
            }
            else if (anchor.Left >= wa.Right - 1)       // taskbar on the right
            {
                x = anchor.Left - w - margin;
                y = anchor.Top;
            }
            else
            {
                x = anchor.Left + anchor.Width / 2 - w / 2;
                bool bottom = anchor.Top + anchor.Height / 2 > b.Top + b.Height / 2;
                y = bottom ? anchor.Top - h - margin : anchor.Bottom + margin;
            }
            x = Math.Max(b.Left, Math.Min(x, b.Right - w));
            y = Math.Max(b.Top, Math.Min(y, b.Bottom - h));
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(backColor);
            using (var pen = new Pen(borderColor))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            int pad = (int)(8 * DpiScale);
            int maxName = (int)(MaxNameWidth * DpiScale);
            int y = pad;
            var titleSize = TextRenderer.MeasureText(title, fontBold);
            TextRenderer.DrawText(g, title, fontBold, new Point(pad, y), foreColor, TextFormatFlags.NoPrefix);
            y += titleSize.Height + (int)(4 * DpiScale);

            int lineH = RowHeight;
            if (message != null)
            {
                TextRenderer.DrawText(g, message, font, new Rectangle(pad, y, Width - pad * 2, lineH), dimColor,
                    TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                return;
            }

            double top = entries.Count > 0 ? entries.Max(x => x.Value) : 0;
            int innerW = Width - 2;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                int level = Level(metric, entry.Value);
                Color levelColor = LevelColor(level);

                // zebra stripe
                if (i % 2 == 1)
                    using (var b = new SolidBrush(stripeColor))
                        g.FillRectangle(b, 1, y, innerW, lineH);

                // usage bar relative to the top entry, tinted with the level color
                if (top > 0 && entry.Value > 0)
                {
                    int barW = (int)Math.Round(innerW * Math.Min(1.0, entry.Value / top));
                    var barColor = Blend(i % 2 == 1 ? stripeColor : backColor, levelColor, dark ? 0.22f : 0.18f);
                    using (var b = new SolidBrush(barColor))
                        g.FillRectangle(b, 1, y, Math.Max(1, barW), lineH);
                }

                var rank = (i + 1).ToString();
                TextRenderer.DrawText(g, rank, font, new Rectangle(pad, y, RankWidth - (int)(4 * DpiScale), lineH), dimColor,
                    flags | TextFormatFlags.Right);

                var value = ProcessSampler.Format(metric, entry.Value);
                var valueW = TextRenderer.MeasureText(value, fontBold).Width;
                int nameX = pad + RankWidth;
                var nameRect = new Rectangle(nameX, y, Math.Min(maxName, Width - nameX - pad - valueW - (int)(8 * DpiScale)), lineH);
                TextRenderer.DrawText(g, EntryName(entry), font, nameRect, level == 0 ? dimColor : foreColor,
                    flags | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
                TextRenderer.DrawText(g, value, fontBold, new Rectangle(Width - pad - valueW, y, valueW, lineH), levelColor,
                    flags | TextFormatFlags.Right);
                y += lineH;
            }
        }

        private int RowHeight => TextRenderer.MeasureText("Ag", font).Height + (int)(4 * DpiScale);

        private int RankWidth => TextRenderer.MeasureText("10", font).Width + (int)(6 * DpiScale);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                font.Dispose();
                fontBold.Dispose();
            }
            base.Dispose(disposing);
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
