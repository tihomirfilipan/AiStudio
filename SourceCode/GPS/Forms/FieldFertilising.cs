using AgLibrary.Logging;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Fertilising: the kind of the work (basic, pre-sowing, top dressing ...), the product and the rate (kg/ha) are chosen
    /// in a small window and shown on the screen with the area covered and the amount used. The work can be saved in
    /// the folder Gnojidba of the field (a report and a copy of the coverage), so the coverage can be reset for the next work.
    /// </summary>
    public partial class FormGPS
    {
        private ToolStripMenuItem fertStripMenu;
        private Label lblFertStatus;

        private static readonly string[] FertTypes = { "Osnovna gnojidba", "Predsjetvena", "Prihrana 1", "Prihrana 2", "Folijarno" };
        private static readonly string[] FertProducts = { "NPK 7-20-30", "NPK 15-15-15", "UREA", "KAN", "Vlastiti unos" };

        private void AddFertilisingMenu()
        {
            fertStripMenu = new ToolStripMenuItem
            {
                Font = recordedPathStripMenu.Font,
                Image = Properties.Resources.RecPath,
                Name = "fertStripMenu",
                Size = recordedPathStripMenu.Size,
                Text = T("Fertilising", "Gnojidba")
            };
            fertStripMenu.Click += (s, e) => ShowFertilisingDialog();
            int idx = toolStripBtnFieldTools.DropDownItems.IndexOf(autoPlanStripMenu != null ? (ToolStripItem)autoPlanStripMenu : recordedPathStripMenu);
            toolStripBtnFieldTools.DropDownItems.Insert(idx + 1, fertStripMenu);

            // the line on the screen under the name of the field
            lblFertStatus = new Label
            {
                Name = "lblFertStatus",
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = Color.Yellow,
                Font = new Font("Arial", 12F, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Location = new Point(lblCurrentField.Left, lblCurrentField.Bottom + 2),
                Visible = false
            };
            lblCurrentField.Parent.Controls.Add(lblFertStatus);
            lblFertStatus.BringToFront();
            ApplyFertilisingColor();
        }

        /// <summary>The line with the kind of work, the product, the rate, the area covered and the amount used.</summary>
        private void UpdateFertilisingStatus()
        {
            if (lblFertStatus == null) return;
            var set = Properties.Settings.Default;
            if (!isJobStarted || string.IsNullOrEmpty(set.setFert_type))
            {
                if (lblFertStatus.Visible) lblFertStatus.Visible = false;
                return;
            }
            double hectares = fd.workedAreaTotal * glm.m2ha;
            double kg = hectares * set.setFert_rate;
            string text = set.setFert_type + " | " + set.setFert_product + " (" + set.setFert_rate.ToString("N0", CultureInfo.CurrentCulture) + " kg/ha)"
                + " | " + T("Covered: ", "Pokriveno: ") + hectares.ToString("N2", CultureInfo.CurrentCulture) + " ha"
                + " | " + T("Used: ", "Utrošeno: ") + kg.ToString("N0", CultureInfo.CurrentCulture) + " kg";
            if (lblFertStatus.Text != text) lblFertStatus.Text = text;
            if (!lblFertStatus.Visible) lblFertStatus.Visible = true;
        }

        /// <summary>The coverage in the colour of the kind of work (switched on in the window; the colour setting itself is not changed).</summary>
        private void ApplyFertilisingColor()
        {
            var set = Properties.Settings.Default;
            if (!set.setFert_colorCoverage || string.IsNullOrEmpty(set.setFert_type)) return;
            if (set.setFert_type.StartsWith("Osnovna") || set.setFert_type.StartsWith("Predsjetvena")) sectionColorDay = Color.FromArgb(255, 0, 120, 255);
            else if (set.setFert_type.StartsWith("Prihrana")) sectionColorDay = Color.FromArgb(255, 50, 205, 50);
            else sectionColorDay = Color.FromArgb(255, 255, 160, 0);
        }

        private void ShowFertilisingDialog()
        {
            var set = Properties.Settings.Default;
            using (var form = new Form())
            {
                form.Text = T("Fertilising", "Gnojidba");
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = new Font("Tahoma", 14F);
                form.ClientSize = new Size(560, 420);

                var cbType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = 45, Width = 520 };
                cbType.Items.AddRange(FertTypes);
                cbType.SelectedIndex = Math.Max(0, Array.IndexOf(FertTypes, set.setFert_type));
                var cbProduct = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Left = 20, Top = 115, Width = 520 };
                cbProduct.Items.AddRange(FertProducts);
                cbProduct.Text = string.IsNullOrEmpty(set.setFert_product) ? FertProducts[0] : set.setFert_product;
                var nudRate = new NumericUpDown { Left = 20, Top = 185, Width = 200, DecimalPlaces = 1, Minimum = 0, Maximum = 5000, Increment = 10, Value = (decimal)Math.Min(5000.0, Math.Max(0.0, set.setFert_rate > 0 ? set.setFert_rate : 250.0)) };
                var chkColor = new CheckBox { Left = 20, Top = 240, Width = 520, Text = T("Coverage in the colour of the work", "Pokrivenost u boji radnje"), Checked = set.setFert_colorCoverage };

                form.Controls.Add(new Label { Left = 20, Top = 15, AutoSize = true, Text = T("Work", "Radnja") });
                form.Controls.Add(cbType);
                form.Controls.Add(new Label { Left = 20, Top = 85, AutoSize = true, Text = T("Fertiliser", "Gnojivo") });
                form.Controls.Add(cbProduct);
                form.Controls.Add(new Label { Left = 20, Top = 155, AutoSize = true, Text = T("Rate (kg/ha)", "Norma (kg/ha)") });
                form.Controls.Add(nudRate);
                form.Controls.Add(chkColor);

                var btnOk = new Button { Text = T("OK", "U redu"), Left = 20, Top = 330, Width = 150, Height = 60, DialogResult = DialogResult.OK };
                var btnSave = new Button { Text = T("Save the work", "Spremi gnojidbu"), Left = 185, Top = 330, Width = 200, Height = 60 };
                var btnCancel = new Button { Text = T("Cancel", "Odustani"), Left = 400, Top = 330, Width = 140, Height = 60, DialogResult = DialogResult.Cancel };
                form.Controls.Add(btnOk);
                form.Controls.Add(btnSave);
                form.Controls.Add(btnCancel);
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                void Apply()
                {
                    set.setFert_type = (string)cbType.SelectedItem;
                    set.setFert_product = cbProduct.Text.Trim();
                    set.setFert_rate = (double)nudRate.Value;
                    set.setFert_colorCoverage = chkColor.Checked;
                    set.Save();
                    ApplyFertilisingColor();
                    UpdateFertilisingStatus();
                }
                btnSave.Click += (s, e) =>
                {
                    Apply();
                    SaveFertilisingToFolder();
                };

                if (form.ShowDialog(this) == DialogResult.OK) Apply();
            }
        }

        /// <summary>
        /// Saves the work in the folder Gnojidba of the field: a report (CSV, opens in Excel) and a copy of the coverage
        /// (Sections.txt), so after it the coverage can be reset and the next work started from zero.
        /// </summary>
        public void SaveFertilisingToFolder()
        {
            try
            {
                if (!isJobStarted)
                {
                    TimedMessageBox(2500, T("Fertilising", "Gnojidba"), T("Open a field first", "Prvo otvorite polje"));
                    return;
                }
                var set = Properties.Settings.Default;
                string fieldDir = GetFieldDir(true);
                string folder = Path.Combine(fieldDir, "Gnojidba");
                Directory.CreateDirectory(folder);

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
                string name = MakeFileName(set.setFert_type + "_" + set.setFert_product);
                double hectares = fd.workedAreaTotal * glm.m2ha;
                double kg = hectares * set.setFert_rate;

                string report = "Datum;Polje;Radnja;Vrsta_Gnojiva;Norma_kg_ha;Pokriveno_ha;Ukupno_kg" + Environment.NewLine
                    + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + ";" + currentFieldDirectory + ";" + set.setFert_type + ";"
                    + set.setFert_product + ";" + set.setFert_rate.ToString("F1", CultureInfo.CurrentCulture) + ";"
                    + hectares.ToString("F2", CultureInfo.CurrentCulture) + ";" + kg.ToString("F1", CultureInfo.CurrentCulture);
                File.WriteAllText(Path.Combine(folder, stamp + "_" + name + ".csv"), report, new System.Text.UTF8Encoding(true));

                // the coverage is written to Sections.txt first, then copied under its own name (the next work does not overwrite it)
                FileSaveSections();
                string sections = Path.Combine(fieldDir, "Sections.txt");
                if (File.Exists(sections)) File.Copy(sections, Path.Combine(folder, "Pokrivenost_" + stamp + "_" + name + ".txt"), true);

                TimedMessageBox(2500, T("Fertilising", "Gnojidba"), T("Saved in the folder Gnojidba of the field", "Spremljeno u mapu Gnojidba polja"));
            }
            catch (Exception ex)
            {
                Log.EventWriter("Save fertilising: " + ex);
                TimedMessageBox(3000, T("Fertilising: error while saving", "Gnojidba: greška pri spremanju"), ex.Message);
            }
        }

        private static string MakeFileName(string text)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Replace(' ', '_');
        }
    }
}
