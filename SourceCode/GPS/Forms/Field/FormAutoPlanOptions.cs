using AgOpenGPS.AutoPlan;
using AgOpenGPS.Controls;
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AgOpenGPS.Forms.Field
{
    /// <summary>
    /// Options for the automatic field plan. Work width and turning radius are prefilled from the
    /// current tool and vehicle settings and can be changed for this plan only.
    /// </summary>
    public class FormAutoPlanOptions : Form
    {
        private readonly NudlessNumericUpDown nudWidth, nudRadius, nudLaps;
        private readonly CheckBox cboxWorkHeadland, cboxAllAround, cboxExtraPass, cboxAddTrack;

        /// <summary>Also add the first pass line as a new curve track.</summary>
        public bool AddTrack => cboxAddTrack.Checked;
        private readonly Label lblAutoLaps;

        public FieldPlanSettings Settings { get; private set; }

        private static string T(string en, string hr) => FormGPS.T(en, hr);

        public FormAutoPlanOptions(double toolWidth, double toolOverlap, double turnRadius, bool edgePicked = false)
        {
            Text = T("Auto Field Plan - Options", "Automatski plan parcele - opcije");
            Font = new Font("Tahoma", 14F, FontStyle.Regular);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.WhiteSmoke;
            Padding = new Padding(16);

            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(table);

            // What is set in the app
            var info = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 3, 3, 14),
                Text = T("Set in the app: ", "Postavljeno u aplikaciji: ") +
                    T("tool width ", "širina priključka ") + Fmt(toolWidth) + " m, " +
                    T("overlap ", "preklapanje ") + Fmt(toolOverlap) + " m, " +
                    T("turning radius ", "radijus okretanja ") + Fmt(turnRadius) + " m"
            };
            info.Text += "\r\n" + (edgePicked
                ? T("Passes follow the edge picked with 2 points.", "Prohodi idu uz rub odabran s 2 točke.")
                : T("Passes follow the longest edge (pick another edge with 2 points in the AB draw window).",
                    "Prohodi idu uz najdulji rub (drugi rub odaberite s 2 točke u prozoru Generiraj AB)."));
            table.Controls.Add(info, 0, 0);
            table.SetColumnSpan(info, 2);

            nudWidth = AddNumber(table, 1, T("Work width (m)", "Radna širina (m)"),
                0.5m, 60m, 2, (decimal)Math.Max(0.5, toolWidth - toolOverlap));
            nudRadius = AddNumber(table, 2, T("Turning radius (m)", "Radijus okretanja (m)"),
                1m, 60m, 1, (decimal)Math.Max(1.0, turnRadius));

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.DimGray,
                Font = new Font("Tahoma", 11F),
                Margin = new Padding(3, 0, 3, 12),
                Text = T("Width = tool width minus overlap. Changes here are used for this plan only.",
                    "Širina = širina priključka minus preklapanje. Promjene ovdje vrijede samo za ovaj plan.")
            };
            table.Controls.Add(hint, 0, 3);
            table.SetColumnSpan(hint, 2);

            nudLaps = AddNumber(table, 4, T("Headland width in passes (0 = auto)", "Širina uvratine u prohodima (0 = auto)"),
                0m, 10m, 0, Properties.Settings.Default.setAutoPlan_headlandLaps);
            lblAutoLaps = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Tahoma", 11F), Margin = new Padding(3, 0, 3, 12) };
            table.Controls.Add(lblAutoLaps, 0, 5);
            table.SetColumnSpan(lblAutoLaps, 2);

            cboxAllAround = AddCheck(table, 6, T("Headland also along the long sides", "Uvratine i uz duge rubove"),
                Properties.Settings.Default.setAutoPlan_headlandAllAround);
            cboxWorkHeadland = AddCheck(table, 7, T("Drive the headland at the end of the plan", "Obradi uvratine na kraju plana"),
                Properties.Settings.Default.setAutoPlan_workHeadland);
            cboxExtraPass = AddCheck(table, 8, T("Extra pass for the strip left along the last edge", "Dodatni prohod za traku uz zadnji rub"),
                Properties.Settings.Default.setAutoPlan_extraPass);

            cboxAddTrack = AddCheck(table, 9, T("Add a new track (curve of the first pass)", "Dodaj novi trag (krivulja prvog prohoda)"),
                Properties.Settings.Default.setAutoPlan_addTrack);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 18, 3, 3) };
            var btnOk = new Button { Text = T("Make plan", "Napravi plan"), AutoSize = true, MinimumSize = new Size(170, 60), BackColor = Color.FromArgb(31, 122, 58), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = T("Cancel", "Odustani"), AutoSize = true, MinimumSize = new Size(150, 60), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            btnOk.Click += BtnOk_Click;
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            table.Controls.Add(buttons, 0, 10);
            table.SetColumnSpan(buttons, 2);
            CancelButton = btnCancel;

            nudWidth.ValueChanged += (s, e) => UpdateAutoLaps();
            nudRadius.ValueChanged += (s, e) => UpdateAutoLaps();
            nudLaps.ValueChanged += (s, e) => UpdateAutoLaps();
            UpdateAutoLaps();
        }

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

        private NudlessNumericUpDown AddNumber(TableLayoutPanel table, int row, string label, decimal min, decimal max, int decimals, decimal value)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 12, 10) }, 0, row);
            var nud = new NudlessNumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Width = 140,
                Font = new Font("Tahoma", 18F, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                Margin = new Padding(3, 4, 3, 4)
            };
            nud.Value = Math.Min(max, Math.Max(min, value));
            nud.Click += (s, e) => ((NudlessNumericUpDown)s).ShowKeypad(this);
            table.Controls.Add(nud, 1, row);
            return nud;
        }

        private static CheckBox AddCheck(TableLayoutPanel table, int row, string label, bool value)
        {
            var cb = new CheckBox { Text = label, Checked = value, AutoSize = true, Margin = new Padding(3, 8, 3, 8), Padding = new Padding(4) };
            table.Controls.Add(cb, 0, row);
            table.SetColumnSpan(cb, 2);
            return cb;
        }

        private int AutoLaps() => Math.Max(1, (int)Math.Ceiling(((double)nudRadius.Value + 3.0) / (double)nudWidth.Value));

        private void UpdateAutoLaps()
        {
            int laps = nudLaps.Value > 0 ? (int)nudLaps.Value : AutoLaps();
            double w = laps * (double)nudWidth.Value;
            lblAutoLaps.Text = (nudLaps.Value > 0 ? T("Headland: ", "Uvratina: ") : T("Automatic headland: ", "Automatska uvratina: ")) +
                laps + T(" x ", " x ") + Fmt((double)nudWidth.Value) + " m = " + Fmt(w) + " m" +
                (laps < AutoLaps() ? T("  (narrow: turns may leave the field)", "  (uska: okreti mogu izaći iz parcele)") : "");
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            Settings = new FieldPlanSettings
            {
                WorkWidth = (double)nudWidth.Value,
                TurnRadius = (double)nudRadius.Value,
                HeadlandLaps = (int)nudLaps.Value,
                HeadlandOnLongSides = cboxAllAround.Checked,
                WorkHeadland = cboxWorkHeadland.Checked,
                AddExtraPass = cboxExtraPass.Checked
            };

            var set = Properties.Settings.Default;
            set.setAutoPlan_headlandLaps = (int)nudLaps.Value;
            set.setAutoPlan_headlandAllAround = cboxAllAround.Checked;
            set.setAutoPlan_workHeadland = cboxWorkHeadland.Checked;
            set.setAutoPlan_extraPass = cboxExtraPass.Checked;
            set.setAutoPlan_addTrack = cboxAddTrack.Checked;
            set.Save();

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
