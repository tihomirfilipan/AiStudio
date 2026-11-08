using AgLibrary.Logging;
using AgOpenGPS.AutoPlan;
using AgOpenGPS.Core.Translations;
using AgOpenGPS.Forms;
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
            if (Math.Abs(tool.offset) > 0.01)
                msg += "\r\n" + T("Tool offset is not taken into account.", "Bočni pomak priključka se ne uzima u obzir.");

            msg += "\r\n\r\n" + T("Save as recorded path? Start it with Play in the recorded path panel, with autosteer on.",
                "Spremiti kao snimljenu putanju? Pokrenite je gumbom Play u panelu snimljene putanje, uz uključen autopilot.");

            if (FormDialog.ShowQuestion(title, msg) != DialogResult.OK) return;

            recPath.StopDrivingRecordedPath();
            recPath.recList.Clear();
            foreach (PlanPoint p in plan.Points)
            {
                recPath.recList.Add(new CRecPathPt(p.E, p.N, p.Heading,
                    p.IsWork ? AutoPlanWorkSpeed : AutoPlanTurnSpeed, p.IsWork));
            }

            // Active path of the field, plus a named copy for the path picker
            FileSaveRecPath("RecPath.txt");
            FileSaveRecPath("AutoPlan.rec");

            recPath.resumeState = 0;
            recPath.currentPositonIndex = 0;
            btnResumePath.Image = Properties.Resources.pathResumeStart;
            panelDrag.Visible = true;
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
