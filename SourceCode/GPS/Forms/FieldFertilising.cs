using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Operation: the kind of work (fertilising, spraying, tillage) with its kind (basic, top dressing, ploughing ...), the product and the
    /// rate (kg/ha) are chosen in a small window and shown on the screen with the area covered and the amount used. The work can be saved in
    /// a folder of the field named after the work (one file with the data and the coverage), so the coverage can be reset for the next work.
    /// </summary>
    public partial class FormGPS
    {
        private ToolStripMenuItem fertStripMenu;
        private Label lblFertStatus;

        private static readonly string[] FertOperations = { "Gnojidba", "Prskanje", "Obrada" };
        private static readonly Dictionary<string, string[]> FertKinds = new Dictionary<string, string[]>
        {
            { "Gnojidba", new[] { "Osnovna gnojidba", "Predsjetvena", "Prihrana 1", "Prihrana 2", "Folijarno" } },
            { "Obrada", new[] { "Oranje", "Rotodrljanje", "Tanjuranje", "Podrivanje", "Drljanje", "Kultivacija", "Sjetvospremanje" } },
            { "Prskanje", new string[0] }
        };
        private static readonly string[] FertProducts = { "NPK 7-20-30", "NPK 15-15-15", "UREA", "KAN", "Vlastiti unos" };

        /// <summary>The name of the work: the operation and its kind, e.g. "Gnojidba - Prihrana 1".</summary>
        private static string FertWorkName(Properties.Settings set)
            => string.IsNullOrEmpty(set.setFert_subtype) ? set.setFert_type : set.setFert_type + " - " + set.setFert_subtype;

        private void AddFertilisingMenu()
        {
            fertStripMenu = new ToolStripMenuItem
            {
                Font = recordedPathStripMenu.Font,
                Image = Properties.Resources.RecPath,
                Name = "fertStripMenu",
                Size = recordedPathStripMenu.Size,
                Text = T("Operation", "Radnja")
            };
            fertStripMenu.Click += (s, e) => ShowFertilisingDialog();
            // next to Vehicle / Tool in the file menu
            int idx = fileToolStripMenuItem.DropDownItems.IndexOf(loadVehicleToolToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(idx + 1, fertStripMenu);

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

        /// <summary>The line with the work and the area covered, and (when there is one) the product, the rate and the amount used.</summary>
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
            // the first line: the work and the area covered, the second: the product with the rate and the amount used (when there is one)
            string text = FertWorkName(set) + " | " + T("Covered: ", "Pokriveno: ") + hectares.ToString("N2", CultureInfo.CurrentCulture) + " ha";
            if (!string.IsNullOrEmpty(set.setFert_product) || set.setFert_rate > 0)
            {
                text += Environment.NewLine + set.setFert_product;
                if (set.setFert_rate > 0)
                    text += " (" + set.setFert_rate.ToString("N0", CultureInfo.CurrentCulture) + " kg/ha) | " + T("Used: ", "Utrošeno: ") + kg.ToString("N0", CultureInfo.CurrentCulture) + " kg";
            }
            if (lblFertStatus.Text != text) lblFertStatus.Text = text;
            if (!lblFertStatus.Visible) lblFertStatus.Visible = true;
        }

        /// <summary>The coverage in the colour of the work (switched on in the window; the colour setting itself is not changed).</summary>
        private void ApplyFertilisingColor()
        {
            var set = Properties.Settings.Default;
            if (!set.setFert_colorCoverage || string.IsNullOrEmpty(set.setFert_type)) return;
            string kind = set.setFert_subtype ?? "";
            if (set.setFert_type == "Gnojidba" && (kind.StartsWith("Osnovna") || kind.StartsWith("Predsjetvena"))) sectionColorDay = Color.FromArgb(255, 0, 120, 255);
            else if (set.setFert_type == "Gnojidba" && kind.StartsWith("Prihrana")) sectionColorDay = Color.FromArgb(255, 50, 205, 50);
            else if (set.setFert_type == "Prskanje") sectionColorDay = Color.FromArgb(255, 200, 80, 220);
            else if (set.setFert_type == "Obrada") sectionColorDay = Color.FromArgb(255, 160, 110, 60);
            else sectionColorDay = Color.FromArgb(255, 255, 160, 0);
        }

        private void ShowFertilisingDialog()
        {
            var set = Properties.Settings.Default;
            using (var form = new Form())
            {
                form.Text = T("Operation", "Radnja");
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = new Font("Tahoma", 14F);
                form.ClientSize = new Size(560, 540);

                var lblOp = new Label { Left = 20, Top = 15, AutoSize = true, Text = T("Operation", "Radnja") };
                var cbOp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = 45, Width = 520 };
                cbOp.Items.AddRange(FertOperations);
                cbOp.SelectedIndex = Math.Max(0, Array.IndexOf(FertOperations, set.setFert_type));
                var lblKind = new Label { Left = 20, Top = 85, AutoSize = true, Text = T("Kind", "Vrsta") };
                var cbKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = 115, Width = 520 };
                var lblProduct = new Label { Left = 20, Top = 155, AutoSize = true, Text = T("Fertiliser / product", "Gnojivo / sredstvo") };
                var cbProduct = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Left = 20, Top = 185, Width = 520 };
                cbProduct.Items.AddRange(FertProducts);
                cbProduct.Text = string.IsNullOrEmpty(set.setFert_product) ? FertProducts[0] : set.setFert_product;
                var lblRate = new Label { Left = 20, Top = 225, AutoSize = true, Text = T("Rate (kg/ha)", "Norma (kg/ha)") };
                var nudRate = new NumericUpDown { Left = 20, Top = 255, Width = 200, DecimalPlaces = 1, Minimum = 0, Maximum = 5000, Increment = 10, Value = (decimal)Math.Min(5000.0, Math.Max(0.0, set.setFert_rate > 0 ? set.setFert_rate : 250.0)) };
                var chkColor = new CheckBox { Left = 20, Top = 320, Width = 520, Text = T("Coverage in the colour of the work", "Pokrivenost u boji radnje"), Checked = set.setFert_colorCoverage };

                // the kinds of the chosen operation; tillage has no product and no rate
                void FillKinds(string wanted)
                {
                    string op = (string)cbOp.SelectedItem;
                    string[] kinds = FertKinds.TryGetValue(op, out var k) ? k : new string[0];
                    cbKind.Items.Clear();
                    cbKind.Items.AddRange(kinds);
                    if (kinds.Length > 0) cbKind.SelectedIndex = Math.Max(0, Array.IndexOf(kinds, wanted));
                    bool hasKinds = kinds.Length > 0;
                    lblKind.Visible = cbKind.Visible = hasKinds;
                    bool hasProduct = op != "Obrada";
                    lblProduct.Visible = cbProduct.Visible = lblRate.Visible = nudRate.Visible = hasProduct;
                }
                cbOp.SelectedIndexChanged += (s, e) => FillKinds(null);
                FillKinds(set.setFert_subtype);

                form.Controls.Add(lblOp);
                form.Controls.Add(cbOp);
                form.Controls.Add(lblKind);
                form.Controls.Add(cbKind);
                form.Controls.Add(lblProduct);
                form.Controls.Add(cbProduct);
                form.Controls.Add(lblRate);
                form.Controls.Add(nudRate);
                form.Controls.Add(chkColor);

                var btnOk = new Button { Text = T("OK", "U redu"), Left = 20, Top = 450, Width = 150, Height = 60, DialogResult = DialogResult.OK };
                var btnSave = new Button { Text = T("Save the operation", "Spremi radnju"), Left = 185, Top = 450, Width = 200, Height = 60 };
                var btnCancel = new Button { Text = T("Cancel", "Odustani"), Left = 400, Top = 450, Width = 140, Height = 60, DialogResult = DialogResult.Cancel };
                form.Controls.Add(btnOk);
                form.Controls.Add(btnSave);
                form.Controls.Add(btnCancel);
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                void Apply()
                {
                    string op = (string)cbOp.SelectedItem;
                    set.setFert_type = op;
                    set.setFert_subtype = cbKind.Visible && cbKind.SelectedItem != null ? (string)cbKind.SelectedItem : "";
                    bool hasProduct = op != "Obrada";
                    set.setFert_product = hasProduct ? cbProduct.Text.Trim() : "";
                    set.setFert_rate = hasProduct ? (double)nudRate.Value : 0;
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
        /// Saves the work in a folder of the field named after the work (Prihrana_1, Oranje, Prskanje ...): ONE file with the work, the
        /// product, the rate, the area covered, the amount used and the coverage itself. The first lines are text, which the loader of
        /// the coverage skips, so the file can be copied back as Sections.txt. After it the coverage can be reset for the next work.
        /// </summary>
        /// <param name="onClose">true when the field is closed: nothing is shown, it is saved only when an operation is chosen and there is some coverage</param>
        public void SaveFertilisingToFolder(bool onClose = false)
        {
            try
            {
                var set = Properties.Settings.Default;
                if (onClose && (string.IsNullOrEmpty(set.setFert_type) || fd.workedAreaTotal <= 0)) return;
                if (!isJobStarted)
                {
                    if (!onClose) TimedMessageBox(2500, T("Operation", "Radnja"), T("Open a field first", "Prvo otvorite polje"));
                    return;
                }
                string fieldDir = GetFieldDir(true);
                string folderName = !string.IsNullOrEmpty(set.setFert_subtype) ? set.setFert_subtype : (string.IsNullOrEmpty(set.setFert_type) ? "Radnja" : set.setFert_type);
                string folder = Path.Combine(fieldDir, MakeFileName(folderName));
                Directory.CreateDirectory(folder);

                // one file per day: saving again the same day updates it, a new day starts a new file
                string stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                double hectares = fd.workedAreaTotal * glm.m2ha;
                double kg = hectares * set.setFert_rate;

                // the coverage is written to Sections.txt first (everything up to now), then put in the same file under the text lines
                if (!onClose) FileSaveSections();      // (when the field is closed it has just been saved)
                string sections = Path.Combine(fieldDir, "Sections.txt");
                string coverage = File.Exists(sections) ? File.ReadAllText(sections) : "";

                // the vehicle and the implement (the names of their profiles and the main numbers from their settings)
                var vs = Properties.VehicleSettings.Default;
                var ts = Properties.ToolSettings.Default;
                string vehicleName = string.IsNullOrEmpty(RegistrySettings.vehicleProfileName) ? "-" : RegistrySettings.vehicleProfileName;
                string toolName = string.IsNullOrEmpty(RegistrySettings.toolProfileName) ? "-" : RegistrySettings.toolProfileName;

                string header = "Radnja: " + FertWorkName(set) + Environment.NewLine
                    + "Vozilo: " + vehicleName + " (osovinski razmak " + vs.setVehicle_wheelbase.ToString("F2", CultureInfo.InvariantCulture) + " m)" + Environment.NewLine
                    + "Prikljucak: " + toolName + " (radna sirina " + ts.setVehicle_toolWidth.ToString("F2", CultureInfo.InvariantCulture)
                    + " m, preklapanje " + ts.setVehicle_toolOverlap.ToString("F2", CultureInfo.InvariantCulture) + " m)" + Environment.NewLine
                    + "Gnojivo: " + set.setFert_product + Environment.NewLine
                    + "Norma_kg_ha: " + set.setFert_rate.ToString("F1", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Pokriveno_ha: " + hectares.ToString("F2", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Ukupno_kg: " + kg.ToString("F1", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Polje: " + currentFieldDirectory + Environment.NewLine
                    + "Datum: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine;
                string namePart = string.IsNullOrEmpty(set.setFert_product) ? MakeFileName(folderName) : MakeFileName(set.setFert_product);
                string file = Path.Combine(folder, stamp + "_" + namePart + ".txt");
                File.WriteAllText(file, header + coverage, new System.Text.UTF8Encoding(false));

                if (!onClose) TimedMessageBox(2500, T("Operation", "Radnja"), T("Saved in the folder of the work: ", "Spremljeno u mapu radnje: ") + Path.GetFileName(folder));
            }
            catch (Exception ex)
            {
                Log.EventWriter("Save operation: " + ex);
                if (!onClose) TimedMessageBox(3000, T("Operation: error while saving", "Radnja: greška pri spremanju"), ex.Message);
            }
        }

        private static string MakeFileName(string text)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Replace(' ', '_');
        }
    }
}
