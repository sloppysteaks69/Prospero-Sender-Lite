// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the live speed graph. Each link is a band, stacked, so the
// top edge is the whole send's speed; the dashed line is the 590 MB/s goal.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PSLite
{
    class Series
    {
        public string Name;
        public Color Color;
        public float[] Values = new float[SpeedGraph.Points];   // MB/s, oldest first
        public float Now, Peak;
    }

    class SpeedGraph : Control
    {
        public const int Points = 240;        // 60 s at 4 samples a second
        public const float Goal = 590f;
        public List<Series> Lines = new List<Series>();
        public float TotalNow, TotalPeak, Average;
        public string Caption = "";

        public SpeedGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        static float Nice(float top)
        {
            float[] steps = { 50, 100, 200, 400, 700, 1000, 1400, 2000, 3000, 5000 };
            foreach (float s in steps)
                if (top <= s) return s;
            return (float)Math.Ceiling(top / 1000) * 1000;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.S(12);
            Rectangle plot = new Rectangle(Theme.S(48), Theme.S(66), Width - Theme.S(48) - pad, Height - Theme.S(66) - Theme.S(22));
            DrawReadout(g, pad);
            if (plot.Width < 40 || plot.Height < 30) return;

            float[] total = new float[Points];
            float top = 0;
            foreach (Series s in Lines)
                for (int i = 0; i < Points; i++) total[i] += s.Values[i];
            for (int i = 0; i < Points; i++) top = Math.Max(top, total[i]);
            float max = Nice(Math.Max(top * 1.12f, Goal * 1.1f));

            using (Pen grid = new Pen(Theme.Border))
            using (SolidBrush dim = new SolidBrush(Theme.Dim))
            {
                StringFormat right = new StringFormat();
                right.Alignment = StringAlignment.Far;
                right.LineAlignment = StringAlignment.Center;
                for (int i = 0; i <= 4; i++)
                {
                    float y = plot.Bottom - plot.Height * i / 4f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawString(((int)(max * i / 4)).ToString(), Theme.Small, dim, new RectangleF(0, y - 8, plot.Left - 6, 16), right);
                }
                g.DrawString("60 s ago", Theme.Small, dim, plot.Left, plot.Bottom + 3);
                SizeF now = g.MeasureString("now", Theme.Small);
                g.DrawString("now", Theme.Small, dim, plot.Right - now.Width, plot.Bottom + 3);
                right.Dispose();
            }

            // the bands, bottom link first
            float[] below = new float[Points];
            float step = plot.Width / (float)(Points - 1);
            foreach (Series s in Lines)
            {
                PointF[] shape = new PointF[Points * 2];
                PointF[] edge = new PointF[Points];
                bool any = false;
                for (int i = 0; i < Points; i++)
                {
                    float x = plot.Left + i * step;
                    float low = below[i], high = below[i] + s.Values[i];
                    if (s.Values[i] > 0.05f) any = true;
                    edge[i] = new PointF(x, plot.Bottom - plot.Height * Math.Min(high, max) / max);
                    shape[i] = edge[i];
                    shape[Points * 2 - 1 - i] = new PointF(x, plot.Bottom - plot.Height * Math.Min(low, max) / max);
                    below[i] = high;
                }
                if (!any) continue;
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(150, s.Color))) g.FillPolygon(fill, shape);
                using (Pen line = new Pen(s.Color, 1.4f)) g.DrawLines(line, edge);
            }
            if (top > 0.05f)
            {
                PointF[] sum = new PointF[Points];
                for (int i = 0; i < Points; i++)
                    sum[i] = new PointF(plot.Left + i * step, plot.Bottom - plot.Height * Math.Min(total[i], max) / max);
                using (Pen white = new Pen(Color.FromArgb(235, 255, 255, 255), 1.8f)) g.DrawLines(white, sum);
            }

            float goalY = plot.Bottom - plot.Height * Goal / max;
            using (Pen dash = new Pen(Theme.Warn, 1.2f))
            using (SolidBrush warn = new SolidBrush(Theme.Warn))
            {
                dash.DashStyle = DashStyle.Dash;
                g.DrawLine(dash, plot.Left, goalY, plot.Right, goalY);
                string label = "goal 590 MB/s";
                SizeF size = g.MeasureString(label, Theme.Small);
                g.DrawString(label, Theme.Small, warn, plot.Right - size.Width - 2, goalY - size.Height - 1);
            }
            using (Pen frame = new Pen(Theme.Border)) g.DrawRectangle(frame, plot);
        }

        void DrawReadout(Graphics g, int pad)
        {
            using (SolidBrush text = new SolidBrush(Theme.Text))
            using (SolidBrush dim = new SolidBrush(Theme.Dim))
            {
                string big = TotalNow.ToString("0");
                SizeF size = g.MeasureString(big, Theme.Big);
                g.DrawString(big, Theme.Big, text, pad - 4, Theme.S(6));
                float x = pad + size.Width - Theme.S(4);
                g.DrawString("MB/s", Theme.UiBold, dim, x, Theme.S(14));
                g.DrawString(string.Format("peak {0:0}   average {1:0}", TotalPeak, Average), Theme.Small, dim, x, Theme.S(33));
                if (Caption.Length > 0) g.DrawString(Caption, Theme.Small, dim, pad, Theme.S(48));

                // the legend, right-aligned, one link a row
                float y = Theme.S(8);
                int shownRows = 0;
                foreach (Series s in Lines)
                {
                    if (shownRows++ >= 3 && Lines.Count > 3) break;
                    string line = s.Name + "  " + s.Now.ToString("0") + " MB/s";
                    SizeF w = g.MeasureString(line, Theme.Small);
                    float left = Width - pad - w.Width;
                    using (SolidBrush c = new SolidBrush(s.Color)) g.FillEllipse(c, left - Theme.S(14), y + Theme.S(4), Theme.S(8), Theme.S(8));
                    g.DrawString(line, Theme.Small, text, left, y);
                    y += w.Height + 1;
                }
                if (Lines.Count > 3) g.DrawString("+" + (Lines.Count - 3) + " more in the table", Theme.Small, dim, Width - pad - Theme.S(120), y);
            }
        }
    }
}
