using AgOpenGPS.AutoPlan;
using AgOpenGPS.Controls;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Field
{
    /// <summary>
    /// Options for the automatic field plan, chosen with picture tiles. Work width and turning
    /// radius are prefilled from the current tool and vehicle settings and can be changed for
    /// this plan only.
    /// </summary>
    public class FormAutoPlanOptions : Form
    {
        private readonly NudlessNumericUpDown nudWidth, nudRadius, nudLaps;
        private readonly Label lblAutoLaps;

        private readonly PictureTile tilePath, tileGuidance;
        private readonly PictureTile tileEnds, tileAllAround;
        private readonly PictureTile tileTurnsEdge, tileTurnsShort, tileTurnsReverse;
        private readonly PictureTile tileOrderAuto, tileOrderNeighbour, tileOrderSkip;
        private readonly PictureTile tileWorkHeadland, tileExtraPass, tileAddTrack;

        /// <summary>Also add the first pass line as a new curve track.</summary>
        public bool AddTrack => tileAddTrack.Selected || GuidanceMode;

        /// <summary>
        /// true: make the first pass curve the active guidance line and use the app's automatic
        /// U-turn; false: save the whole plan as a recorded path.
        /// </summary>
        public bool GuidanceMode => tileGuidance.Selected;

        public FieldPlanSettings Settings { get; private set; }

        private static string T(string en, string hr) => FormGPS.T(en, hr);

        // colours of the pictures
        private static readonly Color CField = Color.FromArgb(46, 84, 52);
        private static readonly Color CEdge = Color.FromArgb(255, 90, 60);
        private static readonly Color CPass = Color.FromArgb(255, 224, 102);
        private static readonly Color CHead = Color.FromArgb(160, 165, 172);
        private static readonly Color CTurn = Color.FromArgb(255, 112, 67);
        private static readonly Color CTurn2 = Color.FromArgb(38, 198, 218);
        private static readonly Color CTrack = Color.FromArgb(0, 229, 255);

        public FormAutoPlanOptions(double toolWidth, double toolOverlap, double turnRadius, bool edgePicked = false)
        {
            Text = T("Auto Field Plan - Options", "Automatski plan parcele - opcije");
            Font = new Font("Tahoma", 12F, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.FromArgb(32, 38, 43);
            ForeColor = Color.WhiteSmoke;
            Padding = new Padding(12);
            Tag = "modern-themed";

            var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(root);

            // --- what is set in the app ---
            var info = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1080, 0),
                ForeColor = Color.Silver,
                Margin = new Padding(3, 0, 3, 8),
                Text = T("Set in the app: ", "Postavljeno u aplikaciji: ") +
                    T("tool width ", "širina priključka ") + Fmt(toolWidth) + " m, " +
                    T("overlap ", "preklapanje ") + Fmt(toolOverlap) + " m, " +
                    T("turning radius ", "radijus okretanja ") + Fmt(turnRadius) + " m.  " +
                    (edgePicked
                        ? T("Passes follow the edge picked with 2 points.", "Prohodi idu uz rub odabran s 2 točke.")
                        : T("Passes follow the longest edge.", "Prohodi idu uz najdulji rub."))
            };
            root.Controls.Add(info, 0, 0);
            root.SetColumnSpan(info, 2);

            // --- numbers ---
            var numbers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            nudWidth = AddNumber(numbers, T("Work width (m)", "Radna širina (m)"), 0.5m, 60m, 2, (decimal)Math.Max(0.5, toolWidth - toolOverlap));
            nudRadius = AddNumber(numbers, T("Turning radius (m)", "Radijus okretanja (m)"), 1m, 60m, 1, (decimal)Math.Max(1.0, turnRadius));
            nudLaps = AddNumber(numbers, T("Headland passes (0 = auto)", "Uvratina u prohodima (0 = auto)"), 0m, 10m, 0, Properties.Settings.Default.setAutoPlan_headlandLaps);
            root.Controls.Add(numbers, 0, 1);
            root.SetColumnSpan(numbers, 2);
            lblAutoLaps = new Label { AutoSize = true, ForeColor = Color.Silver, Font = new Font("Tahoma", 10F), Margin = new Padding(3, 0, 3, 8) };
            root.Controls.Add(lblAutoLaps, 0, 2);
            root.SetColumnSpan(lblAutoLaps, 2);

            var set = Properties.Settings.Default;

            // --- left column ---
            var left = Column();
            tilePath = new PictureTile(T("Recorded path", "Snimljena putanja"), DrawRecordedPath);
            tileGuidance = new PictureTile(T("Guidance line + U-turn", "Linija + U-okret"), DrawGuidance);
            AddGroup(left, T("Drive with", "Način vožnje"), true, set.setAutoPlan_guidanceMode ? 1 : 0, tilePath, tileGuidance);

            tileEnds = new PictureTile(T("Only at pass ends", "Samo na krajevima"), DrawHeadlandEnds);
            tileAllAround = new PictureTile(T("All around", "Svuda okolo"), DrawHeadlandAround);
            AddGroup(left, T("Headland", "Uvratina"), true, set.setAutoPlan_headlandAllAround ? 1 : 0, tileEnds, tileAllAround);
            root.Controls.Add(left, 0, 3);

            // --- right column ---
            var right = Column();
            tileTurnsEdge = new PictureTile(T("Out at the edge", "Uz među"), DrawTurnsAtEdge);
            tileTurnsShort = new PictureTile(T("Short at pass end", "Kratki na kraju"), DrawTurnsShort);
            tileTurnsReverse = new PictureTile(T("With reversing", "S vožnjom unazad"), DrawTurnsReverse);
            AddGroup(right, T("Turns", "Okreti"), true, set.setAutoPlan_reverseTurns ? 2 : (set.setAutoPlan_turnsAtBoundary ? 0 : 1),
                tileTurnsEdge, tileTurnsShort, tileTurnsReverse);

            tileOrderAuto = new PictureTile(T("Automatic", "Automatski"), DrawOrderAuto);
            tileOrderNeighbour = new PictureTile(T("Neighbour passes", "Susjedni prohodi"), DrawOrderNeighbour);
            tileOrderSkip = new PictureTile(T("Skip passes", "Preskakanje"), DrawOrderSkip);
            AddGroup(right, T("Pass order", "Redoslijed prohoda"), true, Math.Max(0, Math.Min(2, set.setAutoPlan_orderMode)),
                tileOrderAuto, tileOrderNeighbour, tileOrderSkip);
            root.Controls.Add(right, 1, 3);

            // --- extras (each on/off) ---
            var extras = Column();
            tileWorkHeadland = new PictureTile(T("Drive the headland at the end", "Obradi uvratine na kraju"), DrawWorkHeadland);
            tileExtraPass = new PictureTile(T("Extra pass along the last edge", "Dodatni prohod uz rub"), DrawExtraPass);
            tileAddTrack = new PictureTile(T("Add new track", "Dodaj novi trag"), DrawAddTrack);
            tileWorkHeadland.Selected = set.setAutoPlan_workHeadland;
            tileExtraPass.Selected = set.setAutoPlan_extraPass;
            tileAddTrack.Selected = set.setAutoPlan_addTrack;
            AddGroup(extras, T("Extras (tap to switch on/off)", "Dodatno (dodir uključi/isključi)"), false, -1, tileWorkHeadland, tileExtraPass, tileAddTrack);
            root.Controls.Add(extras, 0, 4);
            root.SetColumnSpan(extras, 2);

            // --- buttons ---
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 10, 3, 3) };
            var btnOk = new Button
            {
                Text = T("Make plan", "Napravi plan"),
                AutoSize = true,
                MinimumSize = new Size(180, 60),
                BackColor = Color.FromArgb(31, 122, 58),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Tahoma", 14F, FontStyle.Bold)
            };
            var btnCancel = new Button
            {
                Text = T("Cancel", "Odustani"),
                AutoSize = true,
                MinimumSize = new Size(150, 60),
                BackColor = Color.FromArgb(69, 79, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
                Font = new Font("Tahoma", 14F)
            };
            btnOk.Click += BtnOk_Click;
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            root.Controls.Add(buttons, 0, 5);
            root.SetColumnSpan(buttons, 2);
            CancelButton = btnCancel;

            nudWidth.ValueChanged += (s, e) => UpdateAutoLaps();
            nudRadius.ValueChanged += (s, e) => UpdateAutoLaps();
            nudLaps.ValueChanged += (s, e) => UpdateAutoLaps();
            UpdateAutoLaps();
        }

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

        private static FlowLayoutPanel Column() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 14, 0)
        };

        /// <summary>A titled row of tiles; single = only one can be selected.</summary>
        private static void AddGroup(FlowLayoutPanel column, string title, bool single, int selected, params PictureTile[] tiles)
        {
            column.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Tahoma", 12F, FontStyle.Bold),
                ForeColor = Color.White,
                Margin = new Padding(3, 6, 3, 2)
            });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            for (int i = 0; i < tiles.Length; i++)
            {
                PictureTile t = tiles[i];
                if (single)
                {
                    t.Selected = i == selected;
                    t.Click += (s, e) => { foreach (PictureTile o in tiles) o.Selected = o == t; };
                }
                else
                {
                    t.Click += (s, e) => t.Selected = !t.Selected;
                }
                row.Controls.Add(t);
            }
            column.Controls.Add(row);
        }

        private NudlessNumericUpDown AddNumber(FlowLayoutPanel panel, string label, decimal min, decimal max, int decimals, decimal value)
        {
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(3, 0, 18, 0) };
            box.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(0, 0, 0, 2) });
            var nud = new NudlessNumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Width = 150,
                Font = new Font("Tahoma", 18F, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center
            };
            nud.Value = Math.Min(max, Math.Max(min, value));
            nud.Click += (s, e) => ((NudlessNumericUpDown)s).ShowKeypad(this);
            box.Controls.Add(nud);
            panel.Controls.Add(box);
            return nud;
        }

        private int AutoLaps() => Math.Max(1, (int)Math.Ceiling(((double)nudRadius.Value + 3.0) / (double)nudWidth.Value));

        private void UpdateAutoLaps()
        {
            int laps = nudLaps.Value > 0 ? (int)nudLaps.Value : AutoLaps();
            double w = laps * (double)nudWidth.Value;
            lblAutoLaps.Text = (nudLaps.Value > 0 ? T("Headland: ", "Uvratina: ") : T("Automatic headland: ", "Automatska uvratina: ")) +
                laps + " x " + Fmt((double)nudWidth.Value) + " m = " + Fmt(w) + " m" +
                (laps < AutoLaps() ? T("  (narrow: turns may leave the field)", "  (uska: okreti mogu izaći iz parcele)") : "") +
                T("   -   width and radius here are for this plan only", "   -   širina i radijus ovdje vrijede samo za ovaj plan");
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            int order = tileOrderNeighbour.Selected ? 1 : (tileOrderSkip.Selected ? 2 : 0);
            Settings = new FieldPlanSettings
            {
                WorkWidth = (double)nudWidth.Value,
                TurnRadius = (double)nudRadius.Value,
                HeadlandLaps = (int)nudLaps.Value,
                HeadlandOnLongSides = tileAllAround.Selected,
                WorkHeadland = tileWorkHeadland.Selected,
                AddExtraPass = tileExtraPass.Selected,
                OrderMode = order,
                TurnsAtBoundary = tileTurnsEdge.Selected,
                HeadlandReverseTurns = tileTurnsReverse.Selected
            };

            var set = Properties.Settings.Default;
            set.setAutoPlan_headlandLaps = (int)nudLaps.Value;
            set.setAutoPlan_headlandAllAround = tileAllAround.Selected;
            set.setAutoPlan_workHeadland = tileWorkHeadland.Selected;
            set.setAutoPlan_extraPass = tileExtraPass.Selected;
            set.setAutoPlan_addTrack = tileAddTrack.Selected;
            set.setAutoPlan_guidanceMode = tileGuidance.Selected;
            set.setAutoPlan_orderMode = order;
            set.setAutoPlan_turnsAtBoundary = tileTurnsEdge.Selected;
            set.setAutoPlan_reverseTurns = tileTurnsReverse.Selected;
            set.Save();

            DialogResult = DialogResult.OK;
            Close();
        }

        // =====================================================================
        // Pictures (drawn, 150 x 96)
        // =====================================================================

        private static void Field(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(CField)) g.FillRectangle(b, r);
            using (var p = new Pen(CEdge, 2f)) g.DrawRectangle(p, r);
        }

        private static Pen P(Color c, float w) => new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        // passes left to right between x0 and x1, from y0 to y1
        private static void Passes(Graphics g, int x0, int x1, int y0, int y1, int count, Color c)
        {
            using (Pen p = P(c, 1.6f))
                for (int i = 0; i < count; i++)
                {
                    int x = x0 + (x1 - x0) * i / Math.Max(1, count - 1);
                    g.DrawLine(p, x, y0, x, y1);
                }
        }

        private static void UTurn(Graphics g, Color c, int xa, int xb, int y, bool top)
        {
            int w = Math.Abs(xb - xa);
            using (Pen p = P(c, 2.4f))
                g.DrawArc(p, Math.Min(xa, xb), y - w / 2, w, w, top ? 180 : 0, 180);
        }

        private static void DrawRecordedPath(Graphics g, Rectangle r)
        {
            Field(g, r);
            int x0 = r.Left + 18, x1 = r.Right - 18, y0 = r.Top + 18, y1 = r.Bottom - 18;
            Passes(g, x0, x1, y0, y1, 6, CPass);
            int step = (x1 - x0) / 5;
            for (int i = 0; i < 5; i++)
                UTurn(g, i % 2 == 0 ? CTurn : CTurn2, x0 + i * step, x0 + (i + 1) * step, i % 2 == 0 ? y0 : y1, i % 2 == 0);
        }

        private static void DrawGuidance(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (var p = new Pen(CTrack, 2.4f) { DashStyle = DashStyle.Dash })
                g.DrawBezier(p, r.Left + 30, r.Bottom - 8, r.Left + 22, r.Top + 60, r.Left + 46, r.Top + 30, r.Left + 34, r.Top + 6);
            using (Pen p = P(Color.FromArgb(120, CPass), 1.2f))
                for (int i = 1; i <= 3; i++)
                    g.DrawBezier(p, r.Left + 30 + 26 * i, r.Bottom - 8, r.Left + 22 + 26 * i, r.Top + 60, r.Left + 46 + 26 * i, r.Top + 30, r.Left + 34 + 26 * i, r.Top + 6);
            // vehicle and U-turn arrow
            var tri = new[] { new Point(r.Left + 32, r.Top + 50), new Point(r.Left + 25, r.Top + 64), new Point(r.Left + 39, r.Top + 64) };
            g.FillPolygon(Brushes.Tomato, tri);
            using (Pen p = P(CTurn, 2.6f))
            {
                p.CustomEndCap = new AdjustableArrowCap(4, 4);
                g.DrawArc(p, r.Left + 36, r.Top + 4, 26, 22, 180, 180);
            }
        }

        private static void DrawHeadlandEnds(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (var b = new SolidBrush(Color.FromArgb(110, CHead)))
            {
                g.FillRectangle(b, r.Left + 1, r.Top + 1, r.Width - 1, 18);
                g.FillRectangle(b, r.Left + 1, r.Bottom - 18, r.Width - 1, 18);
            }
            Passes(g, r.Left + 6, r.Right - 6, r.Top + 19, r.Bottom - 19, 9, CPass);
        }

        private static void DrawHeadlandAround(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (var b = new SolidBrush(Color.FromArgb(110, CHead)))
            {
                g.FillRectangle(b, r.Left + 1, r.Top + 1, r.Width - 1, 18);
                g.FillRectangle(b, r.Left + 1, r.Bottom - 18, r.Width - 1, 18);
                g.FillRectangle(b, r.Left + 1, r.Top + 19, 18, r.Height - 37);
                g.FillRectangle(b, r.Right - 18, r.Top + 19, 18, r.Height - 37);
            }
            Passes(g, r.Left + 24, r.Right - 24, r.Top + 19, r.Bottom - 19, 7, CPass);
        }

        private static void DrawTurnsAtEdge(Graphics g, Rectangle r)
        {
            // slanted end: straight on to the edge, turn, parallel to the edge, turn back
            using (var b = new SolidBrush(CField))
            {
                var poly = new[] { new Point(r.Left, r.Bottom), new Point(r.Left, r.Top), new Point(r.Right - 50, r.Top), new Point(r.Right, r.Bottom) };
                g.FillPolygon(b, poly);
                using (var p = new Pen(CEdge, 2f)) g.DrawPolygon(p, poly);
            }
            using (Pen p = P(CPass, 1.6f))
                for (int i = 0; i < 5; i++)
                {
                    int y = r.Top + 12 + i * 18;
                    g.DrawLine(p, r.Left + 4, y, r.Left + 44 + i * 9, y);
                }
            using (Pen p = P(CTurn, 2.4f))
            {
                // pass 1 -> pass 3, out to the edge
                g.DrawLines(p, new[]
                {
                    new Point(r.Left + 44, r.Top + 12), new Point(r.Left + 80, r.Top + 12),
                    new Point(r.Left + 92, r.Top + 18), new Point(r.Left + 104, r.Top + 38),
                    new Point(r.Left + 100, r.Top + 46), new Point(r.Left + 62, r.Top + 48)
                });
                // pass 2 -> pass 4
                p.Color = CTurn2;
                g.DrawLines(p, new[]
                {
                    new Point(r.Left + 53, r.Top + 30), new Point(r.Left + 96, r.Top + 30),
                    new Point(r.Left + 108, r.Top + 36), new Point(r.Left + 121, r.Top + 58),
                    new Point(r.Left + 116, r.Top + 66), new Point(r.Left + 71, r.Top + 66)
                });
            }
        }

        private static void DrawTurnsShort(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (var p = new Pen(Color.FromArgb(150, CHead), 1f) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, r.Left + 4, r.Top + 30, r.Right - 4, r.Top + 30);
            Passes(g, r.Left + 18, r.Right - 18, r.Top + 30, r.Bottom - 6, 6, CPass);
            int step = (r.Width - 36) / 5;
            for (int i = 0; i < 5; i += 2)
                UTurn(g, i % 4 == 0 ? CTurn : CTurn2, r.Left + 18 + i * step, r.Left + 18 + (i + 1) * step, r.Top + 30, true);
        }

        private static void DrawTurnsReverse(Graphics g, Rectangle r)
        {
            // three point turn into the neighbour pass: forward turn, back, forward
            Field(g, r);
            using (var p = new Pen(Color.FromArgb(150, CHead), 1f) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, r.Left + 4, r.Top + 34, r.Right - 4, r.Top + 34);
            Passes(g, r.Left + 30, r.Right - 30, r.Top + 34, r.Bottom - 6, 4, CPass);
            int xa = r.Left + 30, xb = r.Left + 30 + (r.Width - 60) / 3;
            using (Pen p = P(CTurn, 2.6f))
                g.DrawArc(p, xa, r.Top + 10, 2 * (xb - xa), 48, 180, 90);   // forward, turning
            using (var p = new Pen(Color.White, 2.6f) { DashStyle = DashStyle.Dash })
            {
                p.CustomEndCap = new AdjustableArrowCap(4, 4);
                g.DrawLine(p, xa + (xb - xa), r.Top + 12, xa + (xb - xa) + 16, r.Top + 12);   // back
            }
            using (Pen p = P(CTurn2, 2.6f))
                g.DrawArc(p, xb, r.Top + 12, 32, 44, 270, 90);              // forward into the next pass
            using (var f = new Font("Tahoma", 9F, FontStyle.Bold))
                g.DrawString("R", f, Brushes.White, r.Right - 24, r.Top + 4);
        }

        private static void Numbers(Graphics g, Rectangle r, int[] order, int y)
        {
            using (var f = new Font("Tahoma", 8F, FontStyle.Bold))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center })
            {
                int count = order.Length;
                for (int i = 0; i < count; i++)
                {
                    int x = r.Left + 18 + (r.Width - 36) * i / (count - 1);
                    g.FillEllipse(Brushes.RoyalBlue, x - 7, y - 7, 14, 14);
                    g.DrawString(order[i].ToString(), f, Brushes.White, new RectangleF(x - 10, y - 6, 20, 14), fmt);
                }
            }
        }

        private static void DrawOrderAuto(Graphics g, Rectangle r)
        {
            DrawOrderSkip(g, r);
            using (var f = new Font("Tahoma", 16F, FontStyle.Bold))
            using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                g.DrawString("A", f, b, r.Right - 26, r.Bottom - 30);
        }

        private static void DrawOrderNeighbour(Graphics g, Rectangle r)
        {
            Field(g, r);
            int x0 = r.Left + 18, x1 = r.Right - 18;
            Passes(g, x0, x1, r.Top + 22, r.Bottom - 10, 6, CPass);
            int step = (x1 - x0) / 5;
            using (Pen p = P(CTurn, 2f))
                for (int i = 0; i < 5; i += 2)
                    g.DrawEllipse(p, x0 + i * step - 3, r.Top + 8, step + 6, 16);   // loop (omega) turns
            Numbers(g, r, new[] { 1, 2, 3, 4, 5, 6 }, r.Bottom - 18);
        }

        private static void DrawOrderSkip(Graphics g, Rectangle r)
        {
            Field(g, r);
            int x0 = r.Left + 18, x1 = r.Right - 18;
            Passes(g, x0, x1, r.Top + 22, r.Bottom - 10, 6, CPass);
            int step = (x1 - x0) / 5;
            UTurn(g, CTurn, x0, x0 + 2 * step, r.Top + 22, true);
            UTurn(g, CTurn2, x0 + 1 * step, x0 + 3 * step, r.Top + 22, true);
            Numbers(g, r, new[] { 1, 3, 2, 5, 4, 6 }, r.Bottom - 18);
        }

        private static void DrawWorkHeadland(Graphics g, Rectangle r)
        {
            DrawHeadlandEnds(g, r);
            using (Pen p = P(CHead, 3f))
            {
                g.DrawLine(p, r.Left + 6, r.Top + 9, r.Right - 6, r.Top + 9);
                g.DrawLine(p, r.Left + 6, r.Bottom - 9, r.Right - 6, r.Bottom - 9);
            }
        }

        private static void DrawExtraPass(Graphics g, Rectangle r)
        {
            Field(g, r);
            Passes(g, r.Left + 14, r.Right - 30, r.Top + 8, r.Bottom - 8, 6, CPass);
            using (Pen p = P(CTurn2, 3f)) g.DrawLine(p, r.Right - 10, r.Top + 8, r.Right - 10, r.Bottom - 8);
            using (var b = new SolidBrush(Color.FromArgb(60, CTurn2))) g.FillRectangle(b, r.Right - 26, r.Top + 1, 25, r.Height - 1);
        }

        private static void DrawAddTrack(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (Pen p = P(Color.FromArgb(110, CPass), 1.2f))
                for (int i = 1; i < 6; i++) g.DrawLine(p, r.Left + 16 + i * 22, r.Top + 6, r.Left + 16 + i * 22, r.Bottom - 6);
            using (var p = new Pen(CTrack, 3f) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, r.Left + 16, r.Top + 6, r.Left + 16, r.Bottom - 6);
            using (var f = new Font("Tahoma", 11F, FontStyle.Bold))
                g.DrawString("A", f, Brushes.White, r.Left + 22, r.Bottom - 24);
        }

        /// <summary>Selectable tile: a drawn picture with a caption under it.</summary>
        private class PictureTile : Control
        {
            private readonly Action<Graphics, Rectangle> draw;
            private bool selected;

            public bool Selected
            {
                get => selected;
                set { selected = value; Invalidate(); }
            }

            public PictureTile(string caption, Action<Graphics, Rectangle> draw)
            {
                this.draw = draw;
                Text = caption;
                Size = new Size(170, 150);
                Margin = new Padding(3);
                Cursor = Cursors.Hand;
                Tag = "modern-themed";
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(selected ? Color.FromArgb(27, 74, 40) : Color.FromArgb(45, 52, 58));
                var pic = new Rectangle(10, 10, Width - 20, 96);
                g.SetClip(new Rectangle(pic.Left, pic.Top, pic.Width + 1, pic.Height + 1));
                draw(g, pic);
                g.ResetClip();
                using (var f = new Font("Tahoma", 10.5F, selected ? FontStyle.Bold : FontStyle.Regular))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(selected ? Color.White : Color.Gainsboro))
                    g.DrawString(Text, f, b, new RectangleF(4, 108, Width - 8, Height - 110), fmt);
                using (var p = new Pen(selected ? Color.FromArgb(102, 187, 106) : Color.FromArgb(84, 96, 104), selected ? 4f : 1.5f))
                    g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
                if (selected)
                {
                    g.FillEllipse(Brushes.LimeGreen, Width - 30, 4, 24, 24);
                    using (Pen p = new Pen(Color.White, 3f)) g.DrawLines(p, new[] { new Point(Width - 24, 16), new Point(Width - 19, 22), new Point(Width - 11, 10) });
                }
            }
        }
    }
}
