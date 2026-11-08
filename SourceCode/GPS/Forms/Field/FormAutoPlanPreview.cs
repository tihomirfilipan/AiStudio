using AgOpenGPS.AutoPlan;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Field
{
    /// <summary>
    /// Preview of an automatic field plan before it is saved: boundary, passes with numbers,
    /// coloured turns, grey headland, vehicle position and the plan summary.
    /// </summary>
    public class FormAutoPlanPreview : Form
    {
        private readonly FieldPlanResult plan;
        private readonly List<V2> fence;
        private readonly V2 vehiclePos;
        private readonly double vehicleHeading;
        private readonly PictureBox picture;

        private static readonly Color[] TurnColors =
        {
            Color.FromArgb(255, 112, 67), Color.FromArgb(236, 64, 122), Color.FromArgb(38, 198, 218),
            Color.FromArgb(171, 71, 188), Color.FromArgb(239, 83, 80), Color.FromArgb(66, 165, 245)
        };

        private static string T(string en, string hr) => FormGPS.T(en, hr);

        public FormAutoPlanPreview(FieldPlanResult plan, List<V2> fence, string summary, V2 vehiclePos, double vehicleHeading)
        {
            this.plan = plan;
            this.fence = fence;
            this.vehiclePos = vehiclePos;
            this.vehicleHeading = vehicleHeading;

            Text = T("Auto Field Plan - Preview", "Automatski plan parcele - predpregled");
            Font = new Font("Tahoma", 13F);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1180, 760);
            MinimumSize = new Size(900, 600);
            BackColor = Color.WhiteSmoke;

            var right = new Panel { Dock = DockStyle.Right, Width = 330, Padding = new Padding(12) };
            var lbl = new Label { Dock = DockStyle.Fill, Text = summary, AutoSize = false };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 80, FlowDirection = FlowDirection.RightToLeft };
            var btnSave = new Button
            {
                Text = T("Save plan", "Spremi plan"),
                Size = new Size(160, 64),
                BackColor = Color.FromArgb(31, 122, 58),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            var btnCancel = new Button { Text = T("Cancel", "Odustani"), Size = new Size(140, 64), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(btnSave);
            buttons.Controls.Add(btnCancel);
            right.Controls.Add(lbl);
            right.Controls.Add(buttons);
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            picture = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 38, 43) };
            Controls.Add(picture);
            Controls.Add(right);

            picture.Resize += (s, e) => Redraw();
            Load += (s, e) => Redraw();
        }

        private void Redraw()
        {
            int w = Math.Max(100, picture.ClientSize.Width), h = Math.Max(100, picture.ClientSize.Height);
            var old = picture.Image;
            picture.Image = Render(w, h);
            old?.Dispose();
        }

        private Bitmap Render(int w, int h)
        {
            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(32, 38, 43));
                if (fence.Count < 3) return bmp;

                // fit the boundary into the picture, north up
                double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
                foreach (V2 p in fence)
                {
                    minE = Math.Min(minE, p.E); maxE = Math.Max(maxE, p.E);
                    minN = Math.Min(minN, p.N); maxN = Math.Max(maxN, p.N);
                }
                const int pad = 30;
                double sc = Math.Min((w - 2 * pad) / Math.Max(1, maxE - minE), (h - 2 * pad) / Math.Max(1, maxN - minN));
                double ox = pad + ((w - 2 * pad) - (maxE - minE) * sc) / 2;
                double oy = pad + ((h - 2 * pad) - (maxN - minN) * sc) / 2;
                PointF P(double e, double n) => new PointF((float)(ox + (e - minE) * sc), (float)(oy + (maxN - n) * sc));

                var poly = new PointF[fence.Count];
                for (int i = 0; i < fence.Count; i++) poly[i] = P(fence[i].E, fence[i].N);
                using (var fill = new SolidBrush(Color.FromArgb(36, 69, 44))) g.FillPolygon(fill, poly);
                using (var pen = new Pen(Color.WhiteSmoke, 2.5f)) g.DrawPolygon(pen, poly);

                // path, run by run
                var pts = plan.Points;
                int turn = 0, i0 = 0;
                while (i0 < pts.Count)
                {
                    bool work = pts[i0].IsWork;
                    bool head = pts[i0].IsHeadland;
                    int i1 = i0;
                    while (i1 < pts.Count && pts[i1].IsWork == work && pts[i1].IsHeadland == head) i1++;
                    int from = Math.Max(0, i0 - 1);
                    if (i1 - from >= 2)
                    {
                        var line = new PointF[i1 - from];
                        for (int k = from; k < i1; k++) line[k - from] = P(pts[k].E, pts[k].N);
                        Color c = !work ? TurnColors[turn++ % TurnColors.Length] : (head ? Color.FromArgb(158, 163, 170) : Color.FromArgb(255, 224, 102));
                        using (var pen = new Pen(c, !work ? 2.6f : (head ? 2.2f : 1.4f)) { LineJoin = LineJoin.Round })
                            g.DrawLines(pen, line);
                    }
                    i0 = i1;
                }

                // pass numbers in the middle of each run, skipped where they would overlap
                var labels = FieldPlanner.GetRunLabels(pts);
                var shown = new List<PointF>();
                using (var font = new Font("Tahoma", 10F, FontStyle.Bold))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    foreach (RunLabel l in labels)
                    {
                        PointF c = P(l.E, l.N);
                        bool free = true;
                        foreach (PointF o in shown)
                            if ((o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y) < 26 * 26) { free = false; break; }
                        if (!free) continue;
                        shown.Add(c);
                        const float r = 12f;
                        using (var b = new SolidBrush(l.IsHeadland ? Color.FromArgb(95, 99, 104) : Color.FromArgb(13, 71, 161)))
                            g.FillEllipse(b, c.X - r, c.Y - r, 2 * r, 2 * r);
                        using (var pen = new Pen(l.IsHeadland ? Color.FromArgb(208, 212, 216) : Color.FromArgb(255, 213, 79), 1.6f))
                            g.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);
                        g.DrawString(l.Text, font, Brushes.White, c, fmt);
                    }
                }

                // start of the plan and the vehicle
                if (pts.Count > 0)
                {
                    PointF s0 = P(pts[0].E, pts[0].N);
                    g.FillEllipse(Brushes.LimeGreen, s0.X - 6, s0.Y - 6, 12, 12);
                }
                PointF v = P(vehiclePos.E, vehiclePos.N);
                float sin = (float)Math.Sin(vehicleHeading), cos = (float)Math.Cos(vehicleHeading);
                // fx to the right, fy forward; screen y grows downwards, heading clockwise from north
                PointF Rot(float fx, float fy) => new PointF(v.X + fx * cos + fy * sin, v.Y + fx * sin - fy * cos);
                var tri = new[] { Rot(0, 14), Rot(-9, -9), Rot(9, -9) };
                using (var b = new SolidBrush(Color.FromArgb(255, 82, 82))) g.FillPolygon(b, tri);
                g.DrawPolygon(Pens.White, tri);
            }
            return bmp;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) picture?.Image?.Dispose();
            base.Dispose(disposing);
        }
    }
}
