using AgLibrary.Logging;
using AgOpenGPS.AutoPlan;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Forms;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
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
        // Headland laps/strips are stored with this speed so they can be recognised after loading
        public const double AutoPlanHeadlandSpeed = 8.0;

        private static bool IsCroatian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hr";
        private static string T(string en, string hr) => IsCroatian ? hr : en;

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
            autoPlanStripMenu.Click += (s, e) => AutoPlanField();

            int idx = toolStripBtnFieldTools.DropDownItems.IndexOf(recordedPathStripMenu);
            toolStripBtnFieldTools.DropDownItems.Insert(idx + 1, autoPlanStripMenu);
        }

        private void AutoPlanField()
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

            var settings = new FieldPlanSettings
            {
                WorkWidth = tool.width - tool.overlap,
                TurnRadius = yt.youTurnRadius,
                WorkHeadland = true
            };

            var fence = new List<V2>(bnd.bndList[0].fenceLine.Count);
            foreach (vec3 pt in bnd.bndList[0].fenceLine) fence.Add(new V2(pt.easting, pt.northing));

            FieldPlanResult plan;
            Cursor old = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                plan = FieldPlanner.Build(fence, settings, new V2(pivotAxlePos.easting, pivotAxlePos.northing), pivotAxlePos.heading);
            }
            finally
            {
                Cursor.Current = old;
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
                msg += "\r\n" + T("An extra overlapping pass covers the strip along the far long side.",
                    "Dodan je prohod uz drugi dugi rub (s preklapanjem) da se pokrije preostala traka.");
            if (Math.Abs(tool.offset) > 0.01)
                msg += "\r\n" + T("Tool offset is not taken into account.", "Bočni pomak priključka se ne uzima u obzir.");

            msg += "\r\n\r\n" + T("Save as recorded path? Start it with Play in the recorded path panel, with autosteer on.",
                "Spremiti kao snimljenu putanju? Pokrenite je gumbom Play u panelu snimljene putanje, uz uključen autopilot.");

            if (FormDialog.ShowQuestion(title, msg) != DialogResult.OK) return;

            recPath.StopDrivingRecordedPath();
            recPath.recList.Clear();
            foreach (PlanPoint p in plan.Points)
            {
                double speed = !p.IsWork ? AutoPlanTurnSpeed : (p.IsHeadland ? AutoPlanHeadlandSpeed : AutoPlanWorkSpeed);
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
                state = T("Ready - press Play", "Spremno - pritisni Play");
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

            // Card in the lower left corner of the map
            double size = 0.85;
            int lineH = 30;
            int width = 330;
            int height = lineH * lines.Length + 20;
            double left = -oglMain.Width / 2.0 + 12;
            double top = oglMain.Height - height - 110;

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

        private static string RunName(RunLabel l)
        {
            return l.IsHeadland ? T("Headland ", "Uvratine ") + l.Text : T("Pass ", "Prohod ") + l.Text;
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
