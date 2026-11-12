using AgOpenGPS.AutoPlan;
using AgOpenGPS.Controls;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Field
{
    /// <summary>
    /// Options for the automatic field plan, chosen with picture tiles, with a live preview of the
    /// plan, the choice of the edge the passes follow and a recommended setting. Work width and
    /// turning radius are prefilled from the tool and vehicle settings (this plan only).
    /// </summary>
    public class FormAutoPlanOptions : Form
    {
        private readonly NudlessNumericUpDown nudWidth, nudRadius, nudLaps;
        private readonly Label lblAutoLaps;

        private readonly PictureTile tilePath, tileGuidance;
        private readonly PictureTile tileEnds, tileAllAround;
        private readonly PictureTile tileTurnsEdge, tileTurnsShort, tileTurnsReverse;
        private readonly PictureTile tileOrderAuto, tileOrderNeighbour, tileOrderSkip;
        private readonly PictureTile tileWorkHeadland, tileExtraPass, tileAddTrack, tileAvoidWorked, tileSplit, tileSlant;

        // live preview
        private readonly List<V2> fence;
        private readonly V2 startPos;
        private readonly double startHeading;
        private readonly List<V2> pickedEdge;
        private readonly List<List<V2>> edges;
        private int edgeIndex = -1;            // -1: picked edge, or the longest one automatically
        private readonly PictureBox preview;
        private readonly Label lblPreview, lblEdge;
        private readonly Timer previewTimer;
        private int version, planVersion = -1;
        private readonly double appWidth, appRadius;

        /// <summary>Also add the first pass line as a new curve track.</summary>
        public bool AddTrack => tileAddTrack.Selected || GuidanceMode;

        /// <summary>
        /// true: make the first pass curve the active guidance line and use the app's automatic
        /// U-turn; false: save the whole plan as a recorded path.
        /// </summary>
        public bool GuidanceMode => tileGuidance.Selected;

        public FieldPlanSettings Settings { get; private set; }

        /// <summary>The plan of the preview when it matches the chosen options, else null.</summary>
        public FieldPlanResult Plan { get; private set; }

        private static string T(string en, string hr) => FormGPS.T(en, hr);

        // colours of the pictures
        private static readonly Color CField = Color.FromArgb(46, 84, 52);
        private static readonly Color CEdge = Color.FromArgb(255, 90, 60);
        private static readonly Color CPass = Color.FromArgb(255, 224, 102);
        private static readonly Color CHead = Color.FromArgb(160, 165, 172);
        private static readonly Color CTurn = Color.FromArgb(255, 112, 67);
        private static readonly Color CTurn2 = Color.FromArgb(38, 198, 218);
        private static readonly Color CTrack = Color.FromArgb(0, 229, 255);

        public FormAutoPlanOptions(double toolWidth, double toolOverlap, double turnRadius,
            List<V2> fence, V2 startPos, double startHeading, List<V2> pickedEdge)
        {
            this.fence = fence;
            this.startPos = startPos;
            this.startHeading = startHeading;
            this.pickedEdge = pickedEdge;
            edges = FieldPlanner.EdgeCandidates(fence, 8);
            appWidth = Math.Max(0.5, toolWidth - toolOverlap);
            appRadius = Math.Max(1.0, turnRadius);

            Text = T("Auto Field Plan - Options", "Automatski plan parcele - opcije");
            Font = new Font("Tahoma", 12F, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = true;
            MinimizeBox = false;
            BackColor = Color.FromArgb(32, 38, 43);
            ForeColor = Color.WhiteSmoke;
            Tag = "modern-themed";
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(1500, wa.Width - 10), Math.Min(980, wa.Height - 10));

            // --- right: preview, edge, recommended, buttons (always visible) ---
            var right = new Panel { Dock = DockStyle.Right, Width = Math.Min(520, Width / 2 - 40), Padding = new Padding(8) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 74, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var btnOk = new Button
            {
                Text = T("Make plan", "Napravi plan"),
                Size = new Size(190, 62),
                BackColor = Color.FromArgb(31, 122, 58),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Tahoma", 14F, FontStyle.Bold)
            };
            var btnCancel = new Button
            {
                Text = T("Cancel", "Odustani"),
                Size = new Size(150, 62),
                BackColor = Color.FromArgb(69, 79, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
                Font = new Font("Tahoma", 14F)
            };
            btnOk.Click += BtnOk_Click;
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            CancelButton = btnCancel;

            var edgeRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, WrapContents = false };
            var btnPrev = SmallButton("◀");
            var btnNext = SmallButton("▶");
            lblEdge = new Label { AutoSize = false, Size = new Size(230, 56), TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Tahoma", 10.5F) };
            var btnRecommended = SmallButton(T("Recommended", "Preporučeno"));
            btnRecommended.Width = 150;
            btnRecommended.BackColor = Color.FromArgb(21, 101, 192);
            btnPrev.Click += (s, e) => { edgeIndex = edgeIndex <= -1 ? edges.Count - 1 : edgeIndex - 1; OptionsChanged(); };
            btnNext.Click += (s, e) => { edgeIndex = edgeIndex >= edges.Count - 1 ? -1 : edgeIndex + 1; OptionsChanged(); };
            btnRecommended.Click += (s, e) => Recommended();
            edgeRow.Controls.Add(btnPrev);
            edgeRow.Controls.Add(lblEdge);
            edgeRow.Controls.Add(btnNext);
            edgeRow.Controls.Add(btnRecommended);

            lblPreview = new Label { Dock = DockStyle.Bottom, Height = 70, ForeColor = Color.Silver, Font = new Font("Tahoma", 10F) };
            preview = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 28, 32), SizeMode = PictureBoxSizeMode.Zoom };
            right.Controls.Add(preview);
            right.Controls.Add(lblPreview);
            right.Controls.Add(edgeRow);
            right.Controls.Add(buttons);

            // --- left: options, scrolls when the screen is small ---
            var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            left.Controls.Add(root);
            Controls.Add(left);
            Controls.Add(right);

            var info = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                ForeColor = Color.Silver,
                Margin = new Padding(3, 0, 3, 8),
                Text = T("Set in the app: ", "Postavljeno u aplikaciji: ") +
                    T("tool width ", "širina priključka ") + Fmt(toolWidth) + " m, " +
                    T("overlap ", "preklapanje ") + Fmt(toolOverlap) + " m, " +
                    T("turning radius ", "radijus okretanja ") + Fmt(turnRadius) + " m."
            };
            root.Controls.Add(info);

            var numbers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            nudWidth = AddNumber(numbers, T("Work width (m)", "Radna širina (m)"), 0.5m, 60m, 2, (decimal)appWidth);
            nudRadius = AddNumber(numbers, T("Turning radius (m)", "Radijus okretanja (m)"), 1m, 60m, 1, (decimal)appRadius);
            nudLaps = AddNumber(numbers, T("Headland passes (0 = auto)", "Uvratina u prohodima (0 = auto)"), 0m, 10m, 0, Properties.Settings.Default.setAutoPlan_headlandLaps);
            root.Controls.Add(numbers);
            lblAutoLaps = new Label { AutoSize = true, ForeColor = Color.Silver, Font = new Font("Tahoma", 10F), Margin = new Padding(3, 0, 3, 8) };
            root.Controls.Add(lblAutoLaps);

            var set = Properties.Settings.Default;
            var columns = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(1100, 0) };
            root.Controls.Add(columns);

            var colA = Column();
            tilePath = new PictureTile(T("Recorded path", "Snimljena putanja"), DrawRecordedPath);
            tileGuidance = new PictureTile(T("Guidance line + U-turn", "Linija + U-okret"), DrawGuidance);
            AddGroup(colA, T("Drive with", "Način vožnje"), true, set.setAutoPlan_guidanceMode ? 1 : 0, tilePath, tileGuidance);
            tileEnds = new PictureTile(T("Only at pass ends", "Samo na krajevima"), DrawHeadlandEnds);
            tileAllAround = new PictureTile(T("All around", "Svuda okolo"), DrawHeadlandAround);
            AddGroup(colA, T("Headland", "Uvratina"), true, set.setAutoPlan_headlandAllAround ? 1 : 0, tileEnds, tileAllAround);
            columns.Controls.Add(colA);

            var colB = Column();
            tileTurnsEdge = new PictureTile(T("Out at the edge", "Uz među"), DrawTurnsAtEdge);
            tileTurnsShort = new PictureTile(T("Short at pass end", "Kratki na kraju"), DrawTurnsShort);
            tileTurnsReverse = new PictureTile(T("With reversing", "S vožnjom unazad"), DrawTurnsReverse);
            AddGroup(colB, T("Turns", "Okreti"), true, set.setAutoPlan_reverseTurns ? 2 : (set.setAutoPlan_turnsAtBoundary ? 0 : 1),
                tileTurnsEdge, tileTurnsShort, tileTurnsReverse);
            tileOrderAuto = new PictureTile(T("Automatic", "Automatski"), DrawOrderAuto);
            tileOrderNeighbour = new PictureTile(T("Neighbour passes", "Susjedni prohodi"), DrawOrderNeighbour);
            tileOrderSkip = new PictureTile(T("Skip passes", "Preskakanje"), DrawOrderSkip);
            AddGroup(colB, T("Pass order", "Redoslijed prohoda"), true, Math.Max(0, Math.Min(2, set.setAutoPlan_orderMode)),
                tileOrderAuto, tileOrderNeighbour, tileOrderSkip);
            columns.Controls.Add(colB);

            var extras = Column();
            tileWorkHeadland = new PictureTile(T("Drive the headland at the end", "Obradi uvratine na kraju"), DrawWorkHeadland);
            tileExtraPass = new PictureTile(T("Extra pass along the last edge", "Dodatni prohod uz rub"), DrawExtraPass);
            tileAddTrack = new PictureTile(T("Add new track", "Dodaj novi trag"), DrawAddTrack);
            tileAvoidWorked = new PictureTile(T("Never drive over worked passes", "Ne vozi po obrađenom"), DrawAvoidWorked);
            tileWorkHeadland.Selected = set.setAutoPlan_workHeadland;
            tileExtraPass.Selected = set.setAutoPlan_extraPass;
            tileAddTrack.Selected = set.setAutoPlan_addTrack;
            tileAvoidWorked.Selected = set.setAutoPlan_avoidWorked;
            tileSplit = new PictureTile(T("Split field (own direction per part)", "Podijeli parcelu (svaki dio svoj smjer)"), DrawSplit);
            tileSplit.Selected = set.setAutoPlan_splitField;
            tileSlant = new PictureTile(T("Pass along a gently slanted edge", "Prohod uz kosu među"), DrawSlantPass);
            tileSlant.Selected = set.setAutoPlan_slantPass;
            AddGroup(extras, T("Extras (tap to switch on/off)", "Dodatno (dodir uključi/isključi)"), false, -1,
                tileWorkHeadland, tileExtraPass, tileAddTrack, tileAvoidWorked, tileSplit, tileSlant);
            root.Controls.Add(extras);

            // every change redraws the preview (after a short pause)
            previewTimer = new Timer { Interval = 450 };
            previewTimer.Tick += (s, e) => { previewTimer.Stop(); StartPreview(); };
            foreach (PictureTile t in new[] { tilePath, tileGuidance, tileEnds, tileAllAround, tileTurnsEdge, tileTurnsShort, tileTurnsReverse,
                tileOrderAuto, tileOrderNeighbour, tileOrderSkip, tileWorkHeadland, tileExtraPass, tileAddTrack, tileAvoidWorked, tileSplit, tileSlant })
                t.Click += (s, e) => OptionsChanged();
            nudWidth.ValueChanged += (s, e) => OptionsChanged();
            nudRadius.ValueChanged += (s, e) => OptionsChanged();
            nudLaps.ValueChanged += (s, e) => OptionsChanged();
            preview.Resize += (s, e) => { if (Plan != null) ShowPlan(Plan); };
            Shown += (s, e) => OptionsChanged();
            FormClosed += (s, e) => previewTimer.Dispose();
        }

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

        private static Button SmallButton(string text) => new Button
        {
            Text = text,
            Size = new Size(56, 56),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(69, 79, 87),
            ForeColor = Color.White,
            Font = new Font("Tahoma", 12F, FontStyle.Bold),
            Margin = new Padding(2)
        };

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
                (laps < AutoLaps() ? T("  (narrow: turns may leave the field)", "  (uska: okreti mogu izaći iz parcele)") : "");
        }

        private void UpdateEdgeLabel()
        {
            if (edgeIndex < 0)
                lblEdge.Text = pickedEdge != null
                    ? T("Edge: picked with 2 points", "Rub: odabran s 2 točke")
                    : T("Edge: automatic (longest)", "Rub: automatski (najdulji)");
            else
                lblEdge.Text = T("Edge ", "Rub ") + (edgeIndex + 1) + "/" + edges.Count + "  (" +
                    Fmt(Math.Round((edges[edgeIndex][1] - edges[edgeIndex][0]).Length)) + " m)";
        }

        /// <summary>Options as chosen now.</summary>
        private FieldPlanSettings CurrentSettings()
        {
            int order = tileOrderNeighbour.Selected ? 1 : (tileOrderSkip.Selected ? 2 : 0);
            return new FieldPlanSettings
            {
                WorkWidth = (double)nudWidth.Value,
                TurnRadius = (double)nudRadius.Value,
                HeadlandLaps = (int)nudLaps.Value,
                HeadlandOnLongSides = tileAllAround.Selected,
                WorkHeadland = tileWorkHeadland.Selected,
                AddExtraPass = tileExtraPass.Selected,
                OrderMode = order,
                TurnsAtBoundary = tileTurnsEdge.Selected,
                HeadlandReverseTurns = tileTurnsReverse.Selected,
                AvoidWorked = tileAvoidWorked.Selected,
                SplitField = tileSplit.Selected,
                SlantEdgePass = tileSlant.Selected,
                ReferenceEdge = edgeIndex >= 0 ? new List<V2>(edges[edgeIndex]) : pickedEdge
            };
        }

        /// <summary>Settings like the example plans: turns at the edge, headland left for the driver.</summary>
        private void Recommended()
        {
            nudWidth.Value = (decimal)appWidth;
            nudRadius.Value = (decimal)appRadius;
            nudLaps.Value = 0;
            tilePath.Selected = true; tileGuidance.Selected = false;
            tileEnds.Selected = true; tileAllAround.Selected = false;
            tileTurnsEdge.Selected = true; tileTurnsShort.Selected = false; tileTurnsReverse.Selected = false;
            tileOrderAuto.Selected = true; tileOrderNeighbour.Selected = false; tileOrderSkip.Selected = false;
            tileWorkHeadland.Selected = false;
            tileExtraPass.Selected = true;
            tileAddTrack.Selected = true;
            tileAvoidWorked.Selected = false;
            tileSplit.Selected = false;
            tileSlant.Selected = true;
            edgeIndex = -1;
            OptionsChanged();
        }

        private void OptionsChanged()
        {
            version++;
            Plan = null;
            UpdateAutoLaps();
            UpdateEdgeLabel();
            lblPreview.Text = T("Calculating ...", "Računam ...");
            previewTimer.Stop();
            previewTimer.Start();
        }

        private void StartPreview()
        {
            if (fence == null || fence.Count < 3) return;
            int v = version;
            FieldPlanSettings settings = CurrentSettings();
            Task.Run(() => FieldPlanner.Build(fence, settings, startPos, startHeading))
                .ContinueWith(t =>
                {
                    if (IsDisposed || v != version) return;   // options changed meanwhile
                    if (t.IsFaulted || t.Result == null) { lblPreview.Text = T("No plan", "Nema plana"); return; }
                    planVersion = v;
                    Plan = t.Result;
                    ShowPlan(t.Result);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowPlan(FieldPlanResult plan)
        {
            if (!plan.Ok)
            {
                lblPreview.Text = T("Not possible: ", "Nije moguće: ") + plan.Error;
                return;
            }
            int w = Math.Max(100, preview.ClientSize.Width), h = Math.Max(100, preview.ClientSize.Height);
            var old = preview.Image;
            preview.Image = FormAutoPlanPreview.RenderPlan(plan, fence, startPos, startHeading, tileAddTrack.Selected || tileGuidance.Selected, w, h);
            old?.Dispose();

            lblPreview.Text = T("Passes ", "Prohoda ") + plan.Passes + ", " + T("turns ", "okreta ") + plan.Turns +
                ", " + (plan.TotalLength / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " km (" +
                T("work ", "rad ") + (plan.WorkLength / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " km)" +
                (plan.TurnsLeavingField > 0 ? "\r\n" + T("Turns leaving the field: ", "Okreta izvan međe: ") + plan.TurnsLeavingField : "") +
                (plan.SkippedPieces > 0 ? "\r\n" + T("Parts not covered: ", "Nepokrivenih dijelova: ") + plan.SkippedPieces : "");
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            Settings = CurrentSettings();
            if (planVersion != version) Plan = null;   // the preview is not for these options

            var set = Properties.Settings.Default;
            set.setAutoPlan_headlandLaps = (int)nudLaps.Value;
            set.setAutoPlan_headlandAllAround = tileAllAround.Selected;
            set.setAutoPlan_workHeadland = tileWorkHeadland.Selected;
            set.setAutoPlan_extraPass = tileExtraPass.Selected;
            set.setAutoPlan_addTrack = tileAddTrack.Selected;
            set.setAutoPlan_guidanceMode = tileGuidance.Selected;
            set.setAutoPlan_orderMode = Settings.OrderMode;
            set.setAutoPlan_turnsAtBoundary = tileTurnsEdge.Selected;
            set.setAutoPlan_reverseTurns = tileTurnsReverse.Selected;
            set.setAutoPlan_avoidWorked = tileAvoidWorked.Selected;
            set.setAutoPlan_splitField = tileSplit.Selected;
            set.setAutoPlan_slantPass = tileSlant.Selected;
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

        private static void DrawSplit(Graphics g, Rectangle r)
        {
            // L shaped field, cut at the bend, each part with its own pass direction
            var poly = new[]
            {
                new Point(r.Left + 4, r.Top + 4), new Point(r.Left + 52, r.Top + 4), new Point(r.Left + 52, r.Top + 52),
                new Point(r.Right - 4, r.Top + 52), new Point(r.Right - 4, r.Bottom - 4), new Point(r.Left + 4, r.Bottom - 4)
            };
            using (var b = new SolidBrush(CField)) g.FillPolygon(b, poly);
            using (var p = new Pen(CEdge, 2f)) g.DrawPolygon(p, poly);
            using (Pen p = P(CPass, 1.5f))
            {
                for (int i = 0; i < 4; i++) g.DrawLine(p, r.Left + 12 + i * 12, r.Top + 10, r.Left + 12 + i * 12, r.Top + 48);
                for (int i = 0; i < 4; i++) g.DrawLine(p, r.Left + 10, r.Top + 60 + i * 9, r.Right - 10, r.Top + 60 + i * 9);
            }
            using (var p = new Pen(Color.White, 2f) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, r.Left + 4, r.Top + 52, r.Left + 52, r.Top + 52);
        }

        private static void DrawSlantPass(Graphics g, Rectangle r)
        {
            // gently slanted edge: one pass along it from the bottom up, then the passes
            var poly = new[] { new Point(r.Left + 4, r.Bottom - 4), new Point(r.Left + 4, r.Top + 4), new Point(r.Left + 40, r.Top + 4), new Point(r.Right - 4, r.Bottom - 4) };
            using (var b = new SolidBrush(CField)) g.FillPolygon(b, poly);
            using (var p = new Pen(CEdge, 2f)) g.DrawPolygon(p, poly);
            using (Pen p = P(Color.FromArgb(150, CPass), 1.3f))
                for (int i = 0; i < 4; i++) g.DrawLine(p, r.Left + 14 + i * 10, r.Bottom - 8, r.Left + 14 + i * 10, r.Top + 16 + i * 14);
            using (Pen p = P(Color.FromArgb(255, 105, 180), 3f))
            {
                p.CustomEndCap = new AdjustableArrowCap(4, 4);
                g.DrawLine(p, r.Right - 22, r.Bottom - 10, r.Left + 44, r.Top + 12);
            }
        }

        private static void DrawAvoidWorked(Graphics g, Rectangle r)
        {
            Field(g, r);
            using (var b = new SolidBrush(Color.FromArgb(70, 120, 200, 120))) g.FillRectangle(b, r.Left + 30, r.Top + 18, r.Width - 60, r.Height - 36);
            Passes(g, r.Left + 30, r.Right - 30, r.Top + 18, r.Bottom - 18, 6, CPass);
            using (Pen p = P(CTurn2, 2.4f))
                g.DrawLines(p, new[] { new Point(r.Right - 30, r.Bottom - 18), new Point(r.Right - 12, r.Bottom - 8), new Point(r.Right - 12, r.Top + 8), new Point(r.Left + 30, r.Top + 8), new Point(r.Left + 30, r.Top + 18) });
            using (Pen p = P(Color.FromArgb(230, 60, 60), 3.2f))
            {
                // crossed out shortcut over the worked passes
                g.DrawLine(p, r.Left + 40, r.Bottom - 22, r.Right - 40, r.Top + 22);
                g.DrawLine(p, r.Left + 64, r.Top + 30, r.Left + 86, r.Top + 52);
                g.DrawLine(p, r.Left + 86, r.Top + 30, r.Left + 64, r.Top + 52);
            }
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
