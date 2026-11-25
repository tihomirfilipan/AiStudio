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
        private readonly bool showTrack;

        private static readonly Color[] TurnColors =
        {
            Color.FromArgb(255, 112, 67), Color.FromArgb(236, 64, 122), Color.FromArgb(38, 198, 218),
            Color.FromArgb(171, 71, 188), Color.FromArgb(239, 83, 80), Color.FromArgb(66, 165, 245)
        };

        private static string T(string en, string hr) => FormGPS.T(en, hr);

        public FormAutoPlanPreview(FieldPlanResult plan, List<V2> fence, string summary, V2 vehiclePos, double vehicleHeading, bool showTrack = false)
        {
            this.showTrack = showTrack;
            this.plan = plan;
            this.fence = fence;
            this.vehiclePos = vehiclePos;
            this.vehicleHeading = vehicleHeading;

            Text = T("Auto Field Plan - Preview", "Automatski plan parcele - predpregled");
            Font = new Font("Tahoma", 13F);
            StartPosition = FormStartPosition.CenterParent;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(1180, wa.Width - 20), Math.Min(760, wa.Height - 20));
            MinimumSize = new Size(Math.Min(900, wa.Width - 20), Math.Min(560, wa.Height - 20));
            BackColor = Color.WhiteSmoke;

            var right = new Panel { Dock = DockStyle.Right, Width = 360, Padding = new Padding(12) };
            var lbl = new Label { Dock = DockStyle.Fill, Text = summary, AutoSize = false, Font = new Font("Tahoma", 11F) };
            // both buttons side by side, always visible at the bottom
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var btnSave = new Button
            {
                Text = T("Save plan", "Spremi plan"),
                Size = new Size(170, 64),
                Margin = new Padding(3),
                BackColor = Color.FromArgb(31, 122, 58),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            var btnCancel = new Button
            {
                Text = T("Cancel", "Odustani"),
                Size = new Size(140, 64),
                Margin = new Padding(3),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(97, 97, 97),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
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

        private Bitmap Render(int w, int h) => RenderPlan(plan, fence, vehiclePos, vehicleHeading, showTrack, w, h);

        /// <summary>Picture of a plan: boundary, passes with numbers, turns, headland, vehicle.</summary>
        private static readonly Color[] SchemeColors =
        {
            Color.FromArgb(255, 70, 70), Color.FromArgb(80, 230, 90), Color.FromArgb(70, 160, 255), Color.FromArgb(255, 200, 40),
            Color.FromArgb(171, 71, 188), Color.FromArgb(38, 198, 218), Color.FromArgb(255, 112, 67), Color.FromArgb(236, 64, 122)
        };

        // --- zoom and pan of the preview (mouse wheel, drag, double click resets) ---
        public static double ViewZoom = 1.0;
        public static double ViewCenterE, ViewCenterN;
        public static bool ViewHasCenter;

        public static void ResetView() { ViewZoom = 1.0; ViewHasCenter = false; }

        /// <summary>Base scale (pixels per metre at zoom 1) and the centre of the field for a picture of w x h.</summary>
        public static void ViewBase(List<V2> fence, int w, int h, out double sc, out double cE, out double cN)
        {
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (V2 q in fence)
            {
                minE = Math.Min(minE, q.E); maxE = Math.Max(maxE, q.E);
                minN = Math.Min(minN, q.N); maxN = Math.Max(maxN, q.N);
            }
            const int pad = 30;
            sc = Math.Min((w - 2 * pad) / Math.Max(1, maxE - minE), (h - 2 * pad) / Math.Max(1, maxN - minN));
            cE = (minE + maxE) / 2; cN = (minN + maxN) / 2;
        }

        private static PointF ViewPoint(double e, double n, double defE, double defN, double sc, int w, int h)
        {
            double cE = ViewHasCenter ? ViewCenterE : defE, cN = ViewHasCenter ? ViewCenterN : defN;
            double k = sc * ViewZoom;
            return new PointF((float)(w / 2.0 + (e - cE) * k), (float)(h / 2.0 - (n - cN) * k));
        }

        /// <summary>
        /// Step 1 of the plan: only the passes as laid out (no order, no turns). In a wedge field the
        /// first half is 1L, 2L ... (solid) and the second 1D, 2D ... (dashed) counted from the outer
        /// boundary, the pair of the same number in the same colour.
        /// </summary>
        public static Bitmap RenderScheme(FieldPlanResult plan, List<V2> fence, int w, int h)
        {
            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(32, 38, 43));
                if (fence.Count < 3) return bmp;
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
                PointF P(double e, double n) => ViewPoint(e, n, (minE + maxE) / 2, (minN + maxN) / 2, sc, w, h);

                var poly = new PointF[fence.Count];
                for (int i = 0; i < fence.Count; i++) poly[i] = P(fence[i].E, fence[i].N);
                using (var fill = new SolidBrush(Color.FromArgb(36, 69, 44))) g.FillPolygon(fill, poly);
                using (var pen = new Pen(Color.WhiteSmoke, 2.5f)) g.DrawPolygon(pen, poly);

                using (var font = new Font("Tahoma", 9F, FontStyle.Bold))
                    foreach (PassScheme ps in plan.Scheme)
                    {
                        if (ps.Pts.Count < 2) continue;
                        Color c = SchemeColors[(Math.Max(1, ps.Rank) - 1) % SchemeColors.Length];
                        var line = new PointF[ps.Pts.Count];
                        for (int k = 0; k < line.Length; k++) line[k] = P(ps.Pts[k].E, ps.Pts[k].N);
                        using (var pen = new Pen(c, 2f) { LineJoin = LineJoin.Round })
                        {
                            if (ps.Side == 'D') pen.DashStyle = DashStyle.Dash;
                            g.DrawLines(pen, line);
                        }
                        PointF m = line[line.Length / 2];
                        SizeF sz = g.MeasureString(ps.Name, font);
                        g.FillRectangle(Brushes.Black, m.X - 1, m.Y - sz.Height - 1, sz.Width + 2, sz.Height);
                        g.DrawString(ps.Name, font, new SolidBrush(c), m.X, m.Y - sz.Height - 1);
                    }
            }
            return bmp;
        }

        /// <summary>
        /// Step 2 of the plan: the headland (uvratina) on the scheme of the passes: the band between the
        /// boundary and the inner edge of the headland, the passes, and the headland tracks uvratina1/2.
        /// </summary>
        /// <summary>
        /// Number of headland passes of each headland track (uvratina1, uvratina2): the distance of the
        /// track from the boundary divided by the work width.
        /// </summary>
        public static List<int> HeadlandPasses(FieldPlanResult plan, List<V2> fence, double workWidth)
        {
            var res = new List<int>();
            for (int t = 0; t < plan.HeadlandTracks.Count && t < 2; t++)
            {
                // the planner knows the headland width in work widths
                if (plan.HeadlandPasses > 0) { res.Add(plan.HeadlandPasses); continue; }
                List<V2> tr = plan.HeadlandTracks[t];
                var d = new List<double>();
                for (int k = 0; k < tr.Count; k += Math.Max(1, tr.Count / 20))
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < fence.Count; i++)
                    {
                        V2 a = fence[i], b = fence[(i + 1) % fence.Count];
                        double ex = b.E - a.E, en = b.N - a.N, l2 = ex * ex + en * en;
                        double u = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((tr[k].E - a.E) * ex + (tr[k].N - a.N) * en) / l2));
                        double dx = tr[k].E - (a.E + u * ex), dn = tr[k].N - (a.N + u * en);
                        best = Math.Min(best, Math.Sqrt(dx * dx + dn * dn));
                    }
                    d.Add(best);
                }
                d.Sort();
                // the track lies at the headland depth from the end; points near the sides of a narrow field are
                // closer to the side, so the upper part of the distances is taken (not the median)
                res.Add(d.Count == 0 || workWidth <= 0 ? 0 : (int)Math.Round(d[Math.Min(d.Count - 1, (int)(d.Count * 0.9))] / workWidth));
            }
            return res;
        }

        public static Bitmap RenderHeadland(FieldPlanResult plan, List<V2> fence, int w, int h, double workWidth = 0)
        {
            List<int> passes = HeadlandPasses(plan, fence, workWidth);

            var bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(32, 38, 43));
                if (fence.Count < 3) return bmp;
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
                PointF P(double e, double n) => ViewPoint(e, n, (minE + maxE) / 2, (minN + maxN) / 2, sc, w, h);

                var poly = new PointF[fence.Count];
                for (int i = 0; i < fence.Count; i++) poly[i] = P(fence[i].E, fence[i].N);
                // the headland: the whole field in the headland colour, the work area on top in the field colour
                using (var fill = new SolidBrush(Color.FromArgb(94, 78, 40))) g.FillPolygon(fill, poly);
                if (plan.WorkArea != null && plan.WorkArea.Count >= 3)
                {
                    var wa = new PointF[plan.WorkArea.Count];
                    for (int i = 0; i < wa.Length; i++) wa[i] = P(plan.WorkArea[i].E, plan.WorkArea[i].N);
                    using (var fill = new SolidBrush(Color.FromArgb(36, 69, 44))) g.FillPolygon(fill, wa);
                }
                using (var pen = new Pen(Color.WhiteSmoke, 2.5f)) g.DrawPolygon(pen, poly);

                foreach (PassScheme ps in plan.Scheme)
                {
                    if (ps.Pts.Count < 2) continue;
                    var line = new PointF[ps.Pts.Count];
                    for (int k = 0; k < line.Length; k++) line[k] = P(ps.Pts[k].E, ps.Pts[k].N);
                    using (var pen = new Pen(Color.FromArgb(255, 224, 102), 1.4f)) g.DrawLines(pen, line);
                }
                using (var font = new Font("Tahoma", 10F, FontStyle.Bold))
                    for (int t = 0; t < plan.HeadlandTracks.Count && t < 2; t++)
                    {
                        List<V2> tr = plan.HeadlandTracks[t];
                        if (tr.Count < 2) continue;
                        var line = new PointF[tr.Count];
                        for (int k = 0; k < line.Length; k++) line[k] = P(tr[k].E, tr[k].N);
                        Color c = t == 0 ? Color.FromArgb(0, 229, 255) : Color.FromArgb(255, 64, 255);
                        using (var pen = new Pen(c, 4f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line);
                        g.DrawString("U" + (t + 1) + ": " + (t < passes.Count && passes[t] > 0 ? passes[t] + " prohoda" : "uvratina"), font, new SolidBrush(c), line[line.Length / 2].X + 6, line[line.Length / 2].Y - 20);
                    }
            }
            return bmp;
        }

        public static Bitmap RenderPlan(FieldPlanResult plan, List<V2> fence, V2 vehiclePos, double vehicleHeading, bool showTrack, int w, int h)
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
                PointF P(double e, double n) => ViewPoint(e, n, (minE + maxE) / 2, (minN + maxN) / 2, sc, w, h);

                var poly = new PointF[fence.Count];
                for (int i = 0; i < fence.Count; i++) poly[i] = P(fence[i].E, fence[i].N);
                using (var fill = new SolidBrush(Color.FromArgb(36, 69, 44))) g.FillPolygon(fill, poly);
                using (var pen = new Pen(Color.WhiteSmoke, 2.5f)) g.DrawPolygon(pen, poly);

                // the edge the passes follow
                if (plan.ReferenceEdge != null && plan.ReferenceEdge.Count > 1)
                {
                    var re = new PointF[plan.ReferenceEdge.Count];
                    for (int k = 0; k < re.Length; k++) re[k] = P(plan.ReferenceEdge[k].E, plan.ReferenceEdge[k].N);
                    using (var pen = new Pen(Color.FromArgb(200, 224, 64, 251), 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, re);
                }

                // the lines of the pass scheme (step 1) whole, up to the headland: the plan is drawn over them, so the swing before
                // the end of a pass (the tractor leaves the line earlier than the machine) never makes a hole in a line
                if (plan.Scheme != null)
                    foreach (PassScheme ps in plan.Scheme)
                    {
                        if (ps.Pts == null || ps.Pts.Count < 2) continue;
                        var sl = new PointF[ps.Pts.Count];
                        for (int k = 0; k < sl.Length; k++) sl[k] = P(ps.Pts[k].E, ps.Pts[k].N);
                        using (var pen = new Pen(Color.FromArgb(255, 224, 102), 1.4f)) g.DrawLines(pen, sl);
                    }

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

                // the new curve track (first pass line), dashed
                if (showTrack && plan.TrackLine.Count > 1)
                {
                    var tl = new PointF[plan.TrackLine.Count];
                    for (int k = 0; k < tl.Length; k++) tl[k] = P(plan.TrackLine[k].E, plan.TrackLine[k].N);
                    using (var pen = new Pen(Color.FromArgb(0, 229, 255), 2f) { DashStyle = DashStyle.Dash })
                        g.DrawLines(pen, tl);
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
