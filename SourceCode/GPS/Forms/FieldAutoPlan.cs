using AgLibrary.Logging;
using AgOpenGPS.AutoPlan;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Forms;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Menu entry that plans one recorded path covering the whole field
    /// (curved passes along the longest edge, smart turns, headland laps).
    /// </summary>
    public partial class FormGPS
    {
        private ToolStripMenuItem autoPlanStripMenu;

        // Speeds stored in the path (only used by the simulator), km/h
        private const double AutoPlanWorkSpeed = 10.0;
        private const double AutoPlanTurnSpeed = 6.0;
        // reverse parts of a turn: negative speed, the simulator drives backwards there
        private const double AutoPlanReverseSpeed = -4.0;
        // Headland laps/strips are stored with this speed so they can be recognised after loading
        public const double AutoPlanHeadlandSpeed = 8.0;

        private static bool IsCroatian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hr";
        internal static string T(string en, string hr) => IsCroatian ? hr : en;

        private void AddAutoPlanMenu()
        {
            autoPlanStripMenu = new ToolStripMenuItem
            {
                Font = recordedPathStripMenu.Font,
                Image = Properties.Resources.RecPath,
                Name = "autoPlanStripMenu",
                Size = recordedPathStripMenu.Size,
                Text = T("Auto Field Plan", "Automatski plan parcele")
            };
            autoPlanStripMenu.Click += (s, e) => AutoPlanField(null);

            int idx = toolStripBtnFieldTools.DropDownItems.IndexOf(recordedPathStripMenu);
            toolStripBtnFieldTools.DropDownItems.Insert(idx + 1, autoPlanStripMenu);

            AddAutoPlanBottomButton();
            AddPlanCardMenu();
        }

        private Button btnAutoPlan;

        /// <summary>Auto plan button in the bottom bar, next to the track button.</summary>
        private void AddAutoPlanBottomButton()
        {
            btnAutoPlan = new Button
            {
                Name = "btnAutoPlan",
                Anchor = AnchorStyles.None,
                BackColor = Color.Transparent,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0),
                Size = btnTrack.Size,
                Image = AutoPlanIcon(48),
                ImageAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false
            };
            btnAutoPlan.FlatAppearance.BorderSize = 0;
            btnAutoPlan.Click += (s, e) => AutoPlanField(null);
            panelBottom.Controls.Add(btnAutoPlan);
            panelBottom.Controls.SetChildIndex(btnAutoPlan, panelBottom.Controls.GetChildIndex(btnTrack) + 1);
        }

        /// <summary>Icon: field with passes and a U-turn at the edge.</summary>
        private static Bitmap AutoPlanIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float s = size / 48f;
                var field = new[] { new PointF(4 * s, 44 * s), new PointF(4 * s, 8 * s), new PointF(32 * s, 4 * s), new PointF(44 * s, 44 * s) };
                using (var b = new SolidBrush(Color.FromArgb(56, 142, 60))) g.FillPolygon(b, field);
                using (var p = new Pen(Color.FromArgb(255, 112, 67), 2.5f * s)) g.DrawPolygon(p, field);
                using (var p = new Pen(Color.FromArgb(255, 224, 102), 2.2f * s))
                    for (int i = 0; i < 4; i++) g.DrawLine(p, (11 + i * 8) * s, 40 * s, (11 + i * 8) * s, (16 - i) * s);
                using (var p = new Pen(Color.White, 2.6f * s))
                {
                    g.DrawArc(p, 11 * s, 9 * s, 8 * s, 10 * s, 180, 180);
                    g.DrawArc(p, 27 * s, 7 * s, 8 * s, 10 * s, 180, 180);
                }
                using (var b = new SolidBrush(Color.FromArgb(21, 101, 192))) g.FillEllipse(b, 26 * s, 26 * s, 20 * s, 20 * s);
                using (var f = new Font("Tahoma", 9f * s, FontStyle.Bold))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("A", f, Brushes.White, new RectangleF(26 * s, 26 * s, 20 * s, 20 * s), fmt);
            }
            return bmp;
        }

        private bool IsPlanCardShown => Properties.Settings.Default.setDisplay_planCard;

        private void AddPlanCardMenu()
        {
            var item = new ToolStripMenuItem
            {
                Text = T("Plan progress card", "Kartica napretka plana"),
                Font = menustripLanguage.Font,
                Size = menustripLanguage.Size,
                CheckOnClick = true,
                Checked = Properties.Settings.Default.setDisplay_planCard
            };
            item.Click += (s, e) =>
            {
                Properties.Settings.Default.setDisplay_planCard = item.Checked;
                Properties.Settings.Default.Save();
            };
            int i = fileToolStripMenuItem.DropDownItems.IndexOf(menustripLanguage);
            fileToolStripMenuItem.DropDownItems.Insert(i + 1, item);
        }

        /// <summary>
        /// Builds the whole-field plan. refEdge: part of the boundary the passes follow
        /// (picked in the AB draw window), or null for the longest edge.
        /// </summary>
        public void AutoPlanField(List<V2> refEdge = null)
        {
            string title = T("Auto Field Plan", "Automatski plan parcele");

            if (!isJobStarted)
            {
                TimedMessageBox(3000, gStr.gsFieldNotOpen, gStr.gsStartNewField);
                return;
            }
            if (bnd.bndList.Count == 0 || bnd.bndList[0].fenceLine.Count < 3)
            {
                FormDialog.Show(title, T("Create an outer field boundary first.", "Prvo napravite vanjsku među parcele."), DialogSeverity.Error);
                return;
            }
            if (bnd.bndList.Count > 1)
            {
                FormDialog.Show(title, T("Fields with inner boundaries are not supported yet.",
                    "Parcele s unutarnjim međama (prepreke) još nisu podržane."), DialogSeverity.Error);
                return;
            }
            if (recPath.isDrivingRecordedPath || recPath.isRecordOn)
            {
                FormDialog.Show(title, T("Stop the recorded path first.", "Prvo zaustavite snimljenu putanju."), DialogSeverity.Warning);
                return;
            }

            var fence = new List<V2>(bnd.bndList[0].fenceLine.Count);
            foreach (vec3 pt in bnd.bndList[0].fenceLine) fence.Add(new V2(pt.easting, pt.northing));
            GetPlanStart(out V2Start start);

            // Options dialog with live preview, prefilled with the tool width and turning radius from the app
            FieldPlanSettings settings;
            FieldPlanResult plan;
            bool addTrack, guidanceMode;
            using (var form = new Forms.Field.FormAutoPlanOptions(tool.width, tool.overlap, yt.youTurnRadius,
                fence, new V2(start.E, start.N), start.Heading, refEdge))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                settings = form.Settings;
                plan = form.Plan;
                addTrack = form.AddTrack;
                guidanceMode = form.GuidanceMode;
            }

            if (plan == null)
            {
                Cursor old = Cursor.Current;
                try
                {
                    Cursor.Current = Cursors.WaitCursor;
                    plan = FieldPlanner.Build(fence, settings, new V2(start.E, start.N), start.Heading);
                }
                finally
                {
                    Cursor.Current = old;
                }
            }

            if (!plan.Ok)
            {
                Log.EventWriter("Auto field plan failed: " + plan.Error);
                FormDialog.Show(title, T("The plan could not be made: ", "Plan nije moguće napraviti: ") + plan.Error, DialogSeverity.Error);
                return;
            }

            Log.EventWriter($"Auto field plan: {plan.Passes} passes, {plan.Turns} turns, {plan.HeadlandLaps} headland laps, " +
                $"{plan.TotalLength:F0} m, order '{plan.OrderDescription}', turns leaving field {plan.TurnsLeavingField}");

            string msg = T("Passes: ", "Prohoda: ") + plan.Passes +
                "\r\n" + T("Turns: ", "Okreta: ") + plan.Turns +
                "\r\n" + T("Headland laps: ", "Krugova po uvratinama: ") + plan.HeadlandLaps +
                "\r\n" + T("Length: ", "Duljina: ") + (plan.TotalLength / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " km" +
                T(" (working ", " (rad ") + (plan.WorkLength / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " km)" +
                "\r\n" + T("Order: ", "Raspored: ") + OrderText(plan.OrderDescription);

            if (plan.TurnsLeavingField > 0)
                msg += "\r\n\r\n" + T("WARNING: ", "UPOZORENJE: ") + plan.TurnsLeavingField +
                    T(" turn(s) leave the boundary - check the map!", " okreta izlazi izvan međe - provjerite na karti!");
            if (plan.SkippedPieces > 0)
                msg += "\r\n" + plan.SkippedPieces + T(" field part(s) are not covered (irregular shape).", " dijelova parcele nije pokriveno (nepravilan oblik).");
            if (plan.ExtraSidePass)
                msg += "\r\n" + T("An extra overlapping pass covers the strip left after the last pass.",
                    "Dodan je prohod s preklapanjem da se pokrije traka koja ostaje iza zadnjeg prohoda.");
            if (Math.Abs(tool.offset) > 0.01)
                msg += "\r\n" + T("Tool offset is not taken into account.", "Bočni pomak priključka se ne uzima u obzir.");

            if (guidanceMode)
            {
                msg += "\r\n\r\n" + T("Save: the curve of the first pass (dashed line) becomes the active guidance line and the automatic U-turn is switched on. The planned headland replaces the app's headland line and switches the sections off in it. Switch on autosteer as usual. The app's own U-turn settings (skip, distance) are used; the recorded path is not changed.",
                    "Spremi: krivulja prvog prohoda (isprekidana linija) postaje aktivna linija za navođenje i uključuje se automatsko okretanje. Uvratina iz plana postaje linija uvratine aplikacije i u njoj se sekcije gase. Autopilot uključite normalno. Koriste se postavke okretanja iz aplikacije (preskakanje, udaljenost); snimljena putanja se ne mijenja.");
            }
            else
            {
                if (addTrack && plan.TrackLine.Count > 2)
                    msg += "\r\n" + T("A new curve track of the first pass will be added (dashed line).", "Bit će dodan novi trag - krivulja prvog prohoda (isprekidana linija).");

                msg += "\r\n" + T("'Sections controlled by headland' will be switched off: the plan switches the sections.",
                    "'Sekcije kontrolira uvratina' će se isključiti: sekcije uključuje i isključuje plan.");

                msg += "\r\n\r\n" + T("Save: the plan becomes the recorded path. Start it with Play in the recorded path panel or with the autosteer button - steering switches on by itself.",
                    "Spremi: plan postaje snimljena putanja. Pokrenite je gumbom Play u panelu snimljene putanje ili gumbom autopilota - upravljanje se uključuje samo.");
            }

            // Preview with the plan drawn, save or cancel
            using (var preview = new Forms.Field.FormAutoPlanPreview(plan, fence, msg,
                new V2(pivotAxlePos.easting, pivotAxlePos.northing), pivotAxlePos.heading, addTrack))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
            }

            if (guidanceMode)
            {
                int idx = plan.TrackLine.Count > 2 ? AddAutoPlanTrack(plan.TrackLine) : -1;
                if (idx < 0)
                {
                    FormDialog.Show(title, T("No guidance line could be made.", "Linija za navođenje nije napravljena."), DialogSeverity.Error);
                    return;
                }
                // the app's headland line = inner edge of the planned headland, sections off there,
                // so U-turns along the boundary do not work the headland
                ApplyPlanHeadland(plan.WorkArea);
                ActivateTrackWithUTurn(idx);
                return;
            }

            if (addTrack && plan.TrackLine.Count > 2)
            {
                // the new track becomes the active one: its guide lines lie on the planned passes
                int ti = AddAutoPlanTrack(plan.TrackLine);
                // wedge field: a second track along the other long side
                if (plan.TrackLine2.Count > 2) AddAutoPlanTrack(plan.TrackLine2, true);
                if (ti >= 0)
                {
                    trk.idx = ti;
                    curve.isCurveValid = false;
                    ABLine.isABValid = false;
                }
            }

            // The plan drives the headland itself: section control by headland would switch the
            // sections off there and leave unworked stripes between the headland strips
            // the plan switches the sections (also in the straight part of turns out to the
            // boundary): the app's headland must not switch them off
            // Without worked headland: the app's headland line is the plan's switch line at the
            // pass ends, every section switches there by itself (a straight end of the work)
            bool sectionLine = plan.SectionLine.Count >= 10;
            if (sectionLine)
            {
                ApplyPlanHeadland(plan.SectionLine);
                planHeadlandDashed = true;
            }
            else if (cboxIsSectionControlled.Checked)
            {
                cboxIsSectionControlled.Checked = false;
                cboxIsSectionControlled_Click(cboxIsSectionControlled, EventArgs.Empty);
                Log.EventWriter("Auto field plan: section control by headland switched off (plan works the headland)");
            }

            recPath.StopDrivingRecordedPath();
            recPath.recList.Clear();

            // sections follow the tool, which is behind (or in front of) the pivot the path is for
            double toolBehind = -(tool.hitchLength
                + (tool.isToolTrailing ? tool.trailingHitchLength + (tool.isToolTBT ? tool.tankTrailingHitchLength : 0) : 0));
            toolBehind = Math.Max(-20.0, Math.Min(30.0, toolBehind));
            // (with the switch line the master stays on a tool width longer on the straight, the
            // sections are cut at the line one by one)
            var planPoints = FieldPlanner.ShiftWorkFlags(
                sectionLine ? FieldPlanner.ExtendWorkStraight(plan.Points, tool.width) : plan.Points, toolBehind);
            Log.EventWriter("Auto field plan: section switching shifted by " + toolBehind.ToString("0.0", CultureInfo.InvariantCulture) + " m");

            foreach (PlanPoint p in planPoints)
            {
                double speed = p.IsReverse ? AutoPlanReverseSpeed
                    : !p.IsWork ? AutoPlanTurnSpeed : (p.IsHeadland ? AutoPlanHeadlandSpeed : AutoPlanWorkSpeed);
                recPath.recList.Add(new CRecPathPt(p.E, p.N, p.Heading, speed, p.IsWork));
            }

            // Active path of the field, plus a named copy for the path picker
            FileSaveRecPath("RecPath.txt");
            FileSaveRecPath("AutoPlan.rec");

            recPath.resumeState = 0;
            recPath.currentPositonIndex = 0;
            btnResumePath.Image = Properties.Resources.pathResumeStart;
            panelDrag.Visible = true;
        }

        // Pass number labels of the recorded path, rebuilt when the path changes
        private List<RunLabel> recPathLabels;
        private int recPathLabelKey;

        /// <summary>
        /// Draws pass numbers (1, 2, 3... in driving order) in the middle of every working run
        /// of the recorded path, and U1, U2... on headland laps.
        /// </summary>
        private double[] recPathCumLength;

        private const double PassNumberSize = 2.0;

        private void DrawRecPathPassNumbers()
        {
            if (!UpdateRecPathLabels()) return;

            // World size of the text, same formula as Font.DrawText3D, used to keep labels apart
            double dist = -camera.camSetDistance;
            double textSize = camera.PitchInDegrees < -45 ? Math.Pow(dist, 0.8) / 800.0 : Math.Pow(dist, 0.85) / 1000.0;
            double minSep = AgOpenGPS.Core.DrawLib.Font.GlyphHeight * textSize * PassNumberSize * 1.15;
            double minSepSq = minSep * minSep;

            // Current and next run are always shown, the rest only where there is room
            int idx = recPath.isFollowingRecPath ? recPath.currentPositonIndex : 0;
            int current = recPathLabels.FindIndex(l => l.EndIndex >= idx);
            var order = new List<int>(recPathLabels.Count);
            if (current >= 0) order.Add(current);
            if (current >= 0 && current + 1 < recPathLabels.Count) order.Add(current + 1);
            for (int i = 0; i < recPathLabels.Count; i++) if (i != current && i != current + 1) order.Add(i);

            var shown = new List<RunLabel>(recPathLabels.Count);
            foreach (int i in order)
            {
                RunLabel label = recPathLabels[i];
                bool free = true;
                foreach (RunLabel o in shown)
                {
                    double dx = o.E - label.E, dy = o.N - label.N;
                    if (dx * dx + dy * dy < minSepSq) { free = false; break; }
                }
                if (!free && shown.Count >= 2) continue;
                shown.Add(label);

                GL.PointSize(26.0f);
                GL.Begin(PrimitiveType.Points);
                if (label.IsHeadland) GL.Color3(0.45f, 0.47f, 0.50f);
                else GL.Color3(0.05f, 0.28f, 0.63f);
                GL.Vertex3(label.E, label.N, 0);
                GL.End();

                if (label.IsHeadland) GL.Color3(0.82f, 0.84f, 0.86f);
                else GL.Color3(1.0f, 0.84f, 0.31f);
                font.DrawText3D(label.E, label.N, label.Text, camHeading, PassNumberSize, true);
            }
            GL.PointSize(1.0f);
        }

        /// <summary>
        /// Rebuilds the pass labels when the recorded path changed.
        /// Returns false if there is nothing to number (no path or a single plain recording).
        /// </summary>
        private bool UpdateRecPathLabels()
        {
            var list = recPath.recList;
            int n = list.Count;
            if (n < 5 || recPath.isRecordOn) return false;

            int key = n ^ list[0].easting.GetHashCode() ^ (list[n - 1].northing.GetHashCode() * 31);
            if (recPathLabels == null || key != recPathLabelKey)
            {
                var pts = new List<PlanPoint>(n);
                foreach (CRecPathPt p in list)
                    pts.Add(new PlanPoint
                    {
                        E = p.easting,
                        N = p.northing,
                        Heading = p.heading,
                        IsWork = p.autoBtnState,
                        IsHeadland = p.autoBtnState && Math.Abs(p.speed - AutoPlanHeadlandSpeed) < 0.05
                    });
                recPathLabels = FieldPlanner.GetRunLabels(pts);
                recPathLabelKey = key;

                // cumulative distance along the path, for progress
                recPathCumLength = new double[n];
                for (int i = 1; i < n; i++)
                    recPathCumLength[i] = recPathCumLength[i - 1] + glm.Distance(list[i - 1].easting, list[i - 1].northing, list[i].easting, list[i].northing);
            }

            // Nothing to number on a single plain recording
            return recPathLabels.Count >= 2;
        }

        /// <summary>
        /// 2D card with the plan progress: current pass, next pass, passes done, remaining distance.
        /// Shown while the recorded path panel is open. Text is ASCII only (OpenGL font).
        /// </summary>
        private void DrawAutoPlanCard()
        {
            if (!IsPlanCardShown) return;
            if (!panelDrag.Visible && !recPath.isDrivingRecordedPath) return;
            if (!UpdateRecPathLabels()) return;

            int n = recPath.recList.Count;
            int idx = recPath.isFollowingRecPath ? recPath.currentPositonIndex : 0;
            if (idx < 0) idx = 0;
            if (idx >= n) idx = n - 1;

            int totalPasses = 0, donePasses = 0;
            int current = -1;
            for (int i = 0; i < recPathLabels.Count; i++)
            {
                RunLabel l = recPathLabels[i];
                if (!l.IsHeadland)
                {
                    totalPasses++;
                    if (l.EndIndex < idx) donePasses++;
                }
                if (current < 0 && l.EndIndex >= idx) current = i;
            }

            string state;
            string next;
            bool inRun = current >= 0 && idx >= recPathLabels[current].StartIndex;

            if (!recPath.isDrivingRecordedPath)
            {
                state = T("Ready - Play or autosteer", "Spremno - Play ili autopilot");
                next = current >= 0 ? RunName(recPathLabels[current]) : "-";
            }
            else if (recPath.isFollowingDubinsToPath)
            {
                state = T("Driving to start", "Prilaz putanji");
                next = current >= 0 ? RunName(recPathLabels[current]) : "-";
            }
            else if (inRun)
            {
                state = RunName(recPathLabels[current]);
                if (!recPathLabels[current].IsHeadland) state += " / " + totalPasses;
                next = current + 1 < recPathLabels.Count ? RunName(recPathLabels[current + 1]) : T("end", "kraj");
            }
            else
            {
                state = T("Turn", "Okret");
                next = current >= 0 ? RunName(recPathLabels[current]) : T("end", "kraj");
            }

            double total = recPathCumLength[n - 1];
            double done = recPathCumLength[idx];
            int percent = total > 0 ? (int)Math.Round(done * 100.0 / total) : 0;
            double remainingKm = (total - done) / 1000.0;

            string[] lines =
            {
                T("FIELD PLAN", "PLAN PARCELE"),
                state,
                T("Next: ", "Slijedi: ") + next,
                T("Progress: ", "Napredak: ") + donePasses + "/" + totalPasses + "  " + percent + "%",
                T("Remaining: ", "Preostalo: ") + remainingKm.ToString("0.0", CultureInfo.InvariantCulture) + " km"
            };

            // Card in the upper left corner, below the mini map (it used to cover the field at the bottom)
            double size = 0.85;
            int lineH = 30;
            int width = 330;
            int height = lineH * lines.Length + 20;
            double left = -oglMain.Width / 2.0 + MiniMapLeftOffset;
            double top = MiniMapTop + (IsMiniMapShown ? MiniMapHeight + 10 : 0);
            // no room below the mini map: put the card above it
            if (top + height > oglMain.Height) top = Math.Max(0, MiniMapTop - height - 10);

            GL.Color4(0.12, 0.14, 0.17, 0.85);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(left, top);
            GL.Vertex2(left + width, top);
            GL.Vertex2(left + width, top + height);
            GL.Vertex2(left, top + height);
            GL.End();

            // progress bar along the bottom edge of the card
            GL.Color4(0.25, 0.49, 0.31, 0.95);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(left, top + height - 6);
            GL.Vertex2(left + width * percent / 100.0, top + height - 6);
            GL.Vertex2(left + width * percent / 100.0, top + height);
            GL.Vertex2(left, top + height);
            GL.End();

            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0) GL.Color3(1.0f, 0.84f, 0.31f);
                else if (i == 1) GL.Color3(1.0f, 1.0f, 1.0f);
                else GL.Color3(0.82f, 0.85f, 0.88f);
                font.DrawText(left + 12, top + 8 + i * lineH, lines[i], i == 1 ? size * 1.1 : size);
            }
        }

        /// <summary>Big "REVERSE" sign while the recorded path goes backwards (driver shifts gear).</summary>
        private void DrawReverseHint()
        {
            if (!recPath.isFollowingRecPath || recPath.recList.Count == 0) return;
            int c = Math.Max(0, Math.Min(recPath.recList.Count - 1, recPath.currentPositonIndex));
            // also warn a few metres before the direction changes
            bool now = recPath.recList[c].speed < 0;
            bool soon = false;
            for (int i = c; i < Math.Min(recPath.recList.Count, c + 6) && !now; i++)
                if (recPath.recList[i].speed < 0) soon = true;
            if (!now && !soon) return;

            string text = now ? T("< REVERSE", "< VOŽNJA UNAZAD") : T("STOP - REVERSE NEXT", "STANI - SLIJEDI UNAZAD");
            double size = 2.2;
            double w = text.Length * AgOpenGPS.Core.DrawLib.Font.CharXSpacing * size + 30;
            double h = AgOpenGPS.Core.DrawLib.Font.GlyphHeight * size + 16;
            double left = -w / 2, top = oglMain.Height * 0.22;
            GL.Enable(EnableCap.Blend);
            if (now) GL.Color4(0.75, 0.1, 0.1, 0.85); else GL.Color4(0.85, 0.55, 0.05, 0.85);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(left, top);
            GL.Vertex2(left + w, top);
            GL.Vertex2(left + w, top + h);
            GL.Vertex2(left, top + h);
            GL.End();
            GL.Color3(1.0f, 1.0f, 1.0f);
            font.DrawText(left + 15, top + 8, text, size);
        }

        private const int MiniMapWidth = 300;
        private const int MiniMapHeight = 280;
        private bool miniMapDragging;
        private Point miniMapDragOffset;

        // Mini map position in map pixels (left/top), kept inside the map
        private int MiniMapX => Math.Max(0, Math.Min(oglMain.Width - MiniMapWidth, Properties.Settings.Default.setDisplay_miniMapX));
        private int MiniMapY => Math.Max(0, Math.Min(oglMain.Height - MiniMapHeight, Properties.Settings.Default.setDisplay_miniMapY));
        private int MiniMapLeftOffset => MiniMapX;
        private int MiniMapTop => MiniMapY;

        private bool IsMiniMapShown =>
            Properties.Settings.Default.setDisplay_planMiniMap && (panelDrag.Visible || recPath.isDrivingRecordedPath)
            && recPath.recList.Count > 4 && !recPath.isRecordOn;

        /// <summary>
        /// Overview of the whole field like the main map: boundary, worked area (coverage),
        /// the plan, the vehicle. Turns with the vehicle (driving direction up) and can be dragged.
        /// Drawn in its own viewport with its own projection.
        /// </summary>
        private void DrawPlanMiniMap()
        {
            if (!IsMiniMapShown) return;

            var list = recPath.recList;
            int n = list.Count;

            // field centre and radius (rotation proof fit)
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            var fence = bnd.bndList.Count > 0 ? bnd.bndList[0].fenceLine : null;
            if (fence != null && fence.Count > 2)
            {
                foreach (vec3 p in fence)
                {
                    minE = Math.Min(minE, p.easting); maxE = Math.Max(maxE, p.easting);
                    minN = Math.Min(minN, p.northing); maxN = Math.Max(maxN, p.northing);
                }
            }
            else
            {
                foreach (CRecPathPt p in list)
                {
                    minE = Math.Min(minE, p.easting); maxE = Math.Max(maxE, p.easting);
                    minN = Math.Min(minN, p.northing); maxN = Math.Max(maxN, p.northing);
                }
            }
            if (maxE - minE < 1 || maxN - minN < 1) return;
            double cx = (minE + maxE) / 2, cy = (minN + maxN) / 2;
            double radius = 0.5 * Math.Sqrt((maxE - minE) * (maxE - minE) + (maxN - minN) * (maxN - minN)) * 1.05;

            int x = MiniMapX, y = MiniMapY;

            // background (in the normal 2D overlay coordinates)
            double left = -oglMain.Width / 2.0 + x;
            GL.Color4(0.12, 0.14, 0.17, 0.88);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(left, y);
            GL.Vertex2(left + MiniMapWidth, y);
            GL.Vertex2(left + MiniMapWidth, y + MiniMapHeight);
            GL.Vertex2(left, y + MiniMapHeight);
            GL.End();

            // own viewport and projection: field fitted, vehicle heading up
            GL.Viewport(x, oglMain.Height - y - MiniMapHeight, MiniMapWidth, MiniMapHeight);
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            double aspect = (double)MiniMapWidth / MiniMapHeight;
            GL.Ortho(-radius * aspect, radius * aspect, -radius, radius, -10, 10);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();
            // rotating with the vehicle (vehicle heading up) or north up
            if (Properties.Settings.Default.setDisplay_miniMapRotate)
                GL.Rotate(glm.toDegrees(pivotAxlePos.heading), 0, 0, 1);
            GL.Translate(-cx, -cy, 0);

            // worked area, like the main map (first entry of each patch holds the colour)
            for (int j = 0; j < triStrip.Count; j++)
            {
                foreach (var triList in triStrip[j].patchList)
                {
                    int count = triList.Count;
                    if (count < 4) continue;
                    GL.Color4((byte)triList[0].easting, (byte)triList[0].northing, (byte)triList[0].heading, (byte)200);
                    GL.Begin(PrimitiveType.TriangleStrip);
                    for (int i = 1; i < count; i++) GL.Vertex3(triList[i].easting, triList[i].northing, 0);
                    GL.End();
                }
            }

            // boundary
            if (fence != null && fence.Count > 2)
            {
                GL.LineWidth(2);
                GL.Color3(0.92f, 0.92f, 0.92f);
                GL.Begin(PrimitiveType.LineLoop);
                foreach (vec3 p in fence) GL.Vertex3(p.easting, p.northing, 0);
                GL.End();
            }

            // plan: what is still ahead, passes yellow, turns grey
            int idx = recPath.isFollowingRecPath ? recPath.currentPositonIndex : 0;
            int step = Math.Max(1, n / 4000);
            GL.LineWidth(1.2f);
            GL.Begin(PrimitiveType.LineStrip);
            for (int i = Math.Max(0, idx); i < n; i += step)
            {
                CRecPathPt p = list[i];
                if (p.autoBtnState) GL.Color3(0.98f, 0.88f, 0.40f);
                else GL.Color3(0.60f, 0.63f, 0.67f);
                GL.Vertex3(p.easting, p.northing, 0);
            }
            GL.End();

            // vehicle
            GL.PointSize(10.0f);
            GL.Begin(PrimitiveType.Points);
            GL.Color3(1.0f, 0.32f, 0.32f);
            GL.Vertex3(pivotAxlePos.easting, pivotAxlePos.northing, 0);
            GL.End();
            GL.PointSize(1.0f);
            GL.LineWidth(1);

            // back to the full map
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.Viewport(0, 0, oglMain.Width, oglMain.Height);

            DrawMiniMapRotateButton(left, y);
        }

        private const int MiniMapButton = 40;

        private Rectangle MiniMapRotateButtonRect =>
            new Rectangle(MiniMapX + MiniMapWidth - MiniMapButton - 4, MiniMapY + 4, MiniMapButton, MiniMapButton);

        /// <summary>
        /// Compass button in the corner of the mini map: the arrow shows north; green = map locked
        /// north up, dark = map turns with the vehicle. Tap to switch.
        /// </summary>
        private void DrawMiniMapRotateButton(double mapLeft, int mapTop)
        {
            bool rotate = Properties.Settings.Default.setDisplay_miniMapRotate;
            double bx = mapLeft + MiniMapWidth - MiniMapButton - 4, by = mapTop + 4;
            if (rotate) GL.Color4(0.20, 0.24, 0.28, 0.95); else GL.Color4(0.18, 0.49, 0.20, 0.95);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(bx, by);
            GL.Vertex2(bx + MiniMapButton, by);
            GL.Vertex2(bx + MiniMapButton, by + MiniMapButton);
            GL.Vertex2(bx, by + MiniMapButton);
            GL.End();

            // north arrow (screen y grows downwards)
            double cxs = bx + MiniMapButton / 2.0, cys = by + MiniMapButton / 2.0;
            double a = rotate ? pivotAxlePos.heading : 0.0;   // north turns with the map
            double sa = Math.Sin(a), ca = Math.Cos(a);
            Vector2Point(cxs, cys, sa, ca, 0, -15, out double tx, out double ty);
            Vector2Point(cxs, cys, sa, ca, -7, 8, out double lx, out double ly);
            Vector2Point(cxs, cys, sa, ca, 7, 8, out double rx, out double ry);
            GL.Color3(1.0f, 0.32f, 0.32f);
            GL.Begin(PrimitiveType.Triangles);
            GL.Vertex2(tx, ty); GL.Vertex2(lx, ly); GL.Vertex2(cxs, cys);
            GL.End();
            GL.Color3(0.92f, 0.92f, 0.92f);
            GL.Begin(PrimitiveType.Triangles);
            GL.Vertex2(tx, ty); GL.Vertex2(cxs, cys); GL.Vertex2(rx, ry);
            GL.End();
        }

        // point (dx, dy) around the centre, rotated by the angle (screen coordinates)
        private static void Vector2Point(double cx, double cy, double sa, double ca, double dx, double dy, out double x, out double y)
        {
            // map rotation is clockwise by the heading on screen, so north turns anticlockwise
            x = cx + dx * ca + dy * sa;
            y = cy - dx * sa + dy * ca;
        }

        private void ToggleMiniMapRotate()
        {
            Properties.Settings.Default.setDisplay_miniMapRotate = !Properties.Settings.Default.setDisplay_miniMapRotate;
            Properties.Settings.Default.Save();
            if (miniMapRotateItem != null) miniMapRotateItem.Checked = Properties.Settings.Default.setDisplay_miniMapRotate;
            oglMain.Invalidate();
        }

        private ToolStripMenuItem miniMapRotateItem;

        // Dragging the mini map (left button / finger inside it)
        private void HookMiniMapDrag()
        {
            oglMain.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || !IsMiniMapShown) return;
                if (MiniMapRotateButtonRect.Contains(e.Location))
                {
                    ToggleMiniMapRotate();
                    return;
                }
                if (e.X >= MiniMapX && e.X <= MiniMapX + MiniMapWidth && e.Y >= MiniMapY && e.Y <= MiniMapY + MiniMapHeight)
                {
                    miniMapDragging = true;
                    miniMapDragOffset = new Point(e.X - MiniMapX, e.Y - MiniMapY);
                }
            };
            oglMain.MouseMove += (s, e) =>
            {
                if (!miniMapDragging) return;
                Properties.Settings.Default.setDisplay_miniMapX = e.X - miniMapDragOffset.X;
                Properties.Settings.Default.setDisplay_miniMapY = e.Y - miniMapDragOffset.Y;
                oglMain.Invalidate();
            };
            oglMain.MouseUp += (s, e) =>
            {
                if (!miniMapDragging) return;
                miniMapDragging = false;
                Properties.Settings.Default.setDisplay_miniMapX = MiniMapX;
                Properties.Settings.Default.setDisplay_miniMapY = MiniMapY;
                Properties.Settings.Default.Save();
            };
        }

        private void AddMiniMapMenu()
        {
            var item = new ToolStripMenuItem
            {
                Text = T("Plan mini map", "Mala karta plana"),
                Font = menustripLanguage.Font,
                Size = menustripLanguage.Size,
                CheckOnClick = true,
                Checked = Properties.Settings.Default.setDisplay_planMiniMap
            };
            item.Click += (s, e) =>
            {
                Properties.Settings.Default.setDisplay_planMiniMap = item.Checked;
                Properties.Settings.Default.Save();
            };
            int i = fileToolStripMenuItem.DropDownItems.IndexOf(menustripLanguage);
            fileToolStripMenuItem.DropDownItems.Insert(i + 1, item);

            miniMapRotateItem = new ToolStripMenuItem
            {
                Text = T("Mini map: turn with the vehicle", "Mala karta: okretanje s vozilom"),
                Font = menustripLanguage.Font,
                Size = menustripLanguage.Size,
                Checked = Properties.Settings.Default.setDisplay_miniMapRotate
            };
            miniMapRotateItem.Click += (s, e) => ToggleMiniMapRotate();
            fileToolStripMenuItem.DropDownItems.Insert(i + 2, miniMapRotateItem);

            HookMiniMapDrag();
        }

        private static string RunName(RunLabel l)
        {
            return l.IsHeadland ? T("Headland ", "Uvratine ") + l.Text : T("Pass ", "Prohod ") + l.Text;
        }

        /// <summary>Adds the first pass line as a new curve track (not activated).</summary>
        private int AddAutoPlanTrack(List<V2> line, bool second = false)
        {
            var des = new List<vec3>(line.Count);
            foreach (V2 p in line) des.Add(new vec3(p.E, p.N, 0));
            CABCurve.MakePointMinimumSpacing(ref des, 1.6);
            CABCurve.CalculateHeadings(ref des);
            if (des.Count < 4) return -1;

            double x = 0, y = 0;
            foreach (vec3 pt in des) { x += Math.Cos(pt.heading); y += Math.Sin(pt.heading); }
            double heading = Math.Atan2(y, x);
            if (heading < 0) heading += glm.twoPI;

            var track = new CTrk
            {
                mode = TrackMode.Curve,
                heading = heading,
                ptA = new vec2(des[0].easting, des[0].northing),
                ptB = new vec2(des[des.Count - 1].easting, des[des.Count - 1].northing),
                isVisible = true,
                name = (second ? "Auto plan 2 " : "Auto plan ") + Math.Round(glm.toDegrees(heading), 1).ToString(CultureInfo.InvariantCulture) + "\u00B0"
            };

            // tail extensions like every curve track
            curve.AddFirstLastPoints(ref des);
            CABCurve.CalculateHeadings(ref des);
            track.curvePts.AddRange(des);

            // tracks of earlier plans would not match these passes: kept, but hidden (in the
            // background, they can be switched visible again in the track list)
            if (!second)
                foreach (CTrk old in trk.gArr)
                    if (old.name != null && old.name.StartsWith("Auto plan ")) old.isVisible = false;
            trk.gArr.Add(track);
            FileSaveTracks();
            Log.EventWriter("Auto field plan: track added " + track.name);
            return trk.gArr.Count - 1;
        }

        /// <summary>
        /// Uses the inner edge of the planned headland as the app's headland line, switches the
        /// headland on and lets it switch the sections off (they stay off in the headland).
        /// </summary>
        // the headland line set by the auto plan is drawn dashed, only where it is off the boundary
        public bool planHeadlandDashed;

        private List<vec3> planDashCache;
        private List<vec3> planDashSource;
        private int planDashSourceCount;

        private void DrawPlanHeadlandDashed()
        {
            if (bnd.bndList.Count == 0) return;
            var hd = bnd.bndList[0].hdLine;
            var fence = bnd.bndList[0].fenceLine;
            int n = hd.Count;
            if (n < 3 || fence.Count < 3) return;

            // dashes worked out once per headland line
            if (planDashCache == null || !ReferenceEquals(planDashSource, hd) || planDashSourceCount != n)
            {
                planDashCache = new List<vec3>();
                planDashSource = hd;
                planDashSourceCount = n;
                const double dash = 3.0, gap = 2.0;
                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    vec3 a = hd[i], b = hd[(i + 1) % n];
                    if (DistToFence(a, fence) < 0.5 && DistToFence(b, fence) < 0.5) { phase = 0; continue; }
                    double len = Math.Sqrt((b.easting - a.easting) * (b.easting - a.easting) + (b.northing - a.northing) * (b.northing - a.northing));
                    if (len < 1e-6) continue;
                    double t = 0;
                    while (t < len)
                    {
                        double cycle = phase % (dash + gap);
                        double stepLen = cycle < dash ? Math.Min(dash - cycle, len - t) : Math.Min(dash + gap - cycle, len - t);
                        if (cycle < dash)
                        {
                            planDashCache.Add(new vec3(a.easting + (b.easting - a.easting) * t / len, a.northing + (b.northing - a.northing) * t / len, 0));
                            planDashCache.Add(new vec3(a.easting + (b.easting - a.easting) * (t + stepLen) / len, a.northing + (b.northing - a.northing) * (t + stepLen) / len, 0));
                        }
                        t += stepLen;
                        phase += stepLen;
                    }
                }
            }

            GL.Begin(PrimitiveType.Lines);
            foreach (vec3 p in planDashCache) GL.Vertex3(p.easting, p.northing, 0);
            GL.End();
        }

        private static double DistToFence(vec3 p, List<vec3> fence)
        {
            double best = double.MaxValue;
            int n = fence.Count;
            for (int i = 0; i < n; i++)
            {
                vec3 a = fence[i], b = fence[(i + 1) % n];
                double dx = b.easting - a.easting, dy = b.northing - a.northing;
                double l2 = dx * dx + dy * dy;
                double u = l2 > 1e-12 ? ((p.easting - a.easting) * dx + (p.northing - a.northing) * dy) / l2 : 0;
                u = Math.Max(0, Math.Min(1, u));
                double ex = a.easting + u * dx - p.easting, ey = a.northing + u * dy - p.northing;
                best = Math.Min(best, ex * ex + ey * ey);
            }
            return Math.Sqrt(best);
        }

        private void ApplyPlanHeadland(List<V2> work)
        {
            planHeadlandDashed = false;
            planDashCache = null;
            if (work == null || work.Count < 10 || bnd.bndList.Count == 0) return;

            var hd = bnd.bndList[0].hdLine;
            hd.Clear();
            int n = work.Count;
            for (int i = 0; i < n; i++)
            {
                V2 a = work[i], b = work[(i + 1) % n];
                double heading = Math.Atan2(b.E - a.E, b.N - a.N);
                if (heading < 0) heading += glm.twoPI;
                hd.Add(new vec3(a.E, a.N, heading));
            }
            FileSaveHeadland();

            if (!bnd.isHeadlandOn) btnHeadlandOnOff.PerformClick();
            if (!cboxIsSectionControlled.Checked)
            {
                cboxIsSectionControlled.Checked = true;
                cboxIsSectionControlled_Click(cboxIsSectionControlled, EventArgs.Empty);
            }
            Log.EventWriter("Auto field plan: headland line set from the plan, sections controlled by headland");
        }

        /// <summary>Makes a track the active guidance line and switches the automatic U-turn on.</summary>
        private void ActivateTrackWithUTurn(int idx)
        {
            // a running recorded path would fight the guidance line
            if (recPath.isDrivingRecordedPath) btnPathGoStop.PerformClick();
            if (panelDrag.Visible) panelDrag.Visible = false;

            curve.isCurveValid = false;
            ABLine.isABValid = false;
            curve.desList?.Clear();
            if (yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();

            trk.idx = idx;
            yt.ResetYouTurn();
            PanelUpdateRightAndBottom();

            if (!yt.isYouTurnBtnOn) btnAutoYouTurn.PerformClick();
            Log.EventWriter("Auto field plan: guidance line active " + trk.gArr[idx].name);
        }

        private static string OrderText(string order)
        {
            if (!IsCroatian) return order;
            if (order == "neighbour passes") return "susjedni prohodi (omega okreti)";
            if (order == "shortest turns") return "najkraći okreti (preskakanje redova)";
            if (order != null && order.StartsWith("skip passes")) return "preskakanje redova";
            return order;
        }
    }
}
