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
            { "Prskanje", new[] { "Kukuruz", "Pšenica", "Soja", "Ječam", "Suncokret", "Uljana repica", "Lucerna", "DTS", "Tritikale" } }   // spraying: the crop
        };
        private static readonly string[] FertProducts = { "NPK 7-20-30", "NPK 15-15-15", "UREA", "KAN", "Vlastiti unos" };

        // the choice for spraying: the preparation and what it is for (the name is what is saved, the rest goes to the note)
        private static readonly string[] SprayProducts =
        {
            "Adengo – zemljišni i rani folijarni herbicid (kukuruz)",
            "Laudis – folijarni herbicid za širokolisne korove (kukuruz)",
            "Equip – folijarni herbicid za uskolisne korove / sirak (kukuruz)",
            "Motivell Extra 6 OD – folijarni herbicid za uskolisne korove / sirak (kukuruz)",
            "Elumis Peak – kombinirani folijarni herbicid za sve korove (kukuruz)",
            "Alister New – jesenski/proljetni herbicid širokog spektra (pšenica)",
            "Tornado Forte – jesenski/proljetni herbicid širokog spektra (pšenica)",
            "Sekator OD – proljetni folijarni herbicid za širokolisne korove (pšenica)",
            "Biathlon 4D – proljetni folijarni herbicid za širokolisne korove (pšenica)",
            "Elatus Era – fungicid za zaštitu lista u proljeće (pšenica)",
            "Ascra Xpro – fungicid za zaštitu lista u proljeće (pšenica)",
            "Prosaro 250 EC – fungicid za zaštitu klasa u cvatnji (pšenica)",
            "Karate Zeon – insekticid za žitnog balca i lisne uši (pšenica)",
            "Decis 2,5 EC – insekticid širokog spektra (pšenica, kukuruz)",
            "Sencor WG 70 (ili Mistral 70 WG) – zemljišni herbicid prije nicanja (soja)",
            "Frontier X2 (ili Proman) – zemljišni partner herbicid prije nicanja (soja)",
            "Corum – folijarni herbicid za širokolisne korove (soja)",
            "Laguna 75 WG + Harmony SX – folijarna kombinacija za širokolisne korove (soja)",
            "Focus Ultra (ili Fusilade Forte) – folijarni herbicid za uskolisne korove / trave (soja)",
            "Vertimec 018 EC – akaricid protiv crvenog pauka / grinje (soja)"
        };
        private static string SprayName(string text) { int i = text.IndexOf(" – ", StringComparison.Ordinal); return (i > 0 ? text.Substring(0, i) : text).Trim(); }
        private static string[] SprayNames()
        {
            var names = new string[SprayProducts.Length];
            for (int i = 0; i < names.Length; i++) names[i] = SprayName(SprayProducts[i]);
            return names;
        }
        /// <summary>What a preparation from the list is for (empty for one typed in).</summary>
        private static string SprayNoteFor(string name)
        {
            name = (name ?? "").Trim();
            foreach (string item in SprayProducts) if (SprayName(item) == name) return SprayNote(item);
            return "";
        }
        private static string SprayNote(string text) { int i = text.IndexOf(" – ", StringComparison.Ordinal); return i > 0 ? text.Substring(i + 3).Trim() : ""; }

        private static bool IsSpray(string type) => type == "Prskanje";
        /// <summary>The unit of the amount: litres for spraying, kilograms otherwise.</summary>
        private static string FertUnit(string type) => IsSpray(type) ? "l" : "kg";
        /// <summary>The product (fertiliser / preparation) and the rate of the chosen operation; tillage has none.</summary>
        private static string FertProductOf(Properties.Settings s) => s.setFert_type == "Obrada" ? "" : (IsSpray(s.setFert_type) ? s.setFert_sprayProduct : s.setFert_product);
        private static double FertRateOf(Properties.Settings s) => s.setFert_type == "Obrada" ? 0 : (IsSpray(s.setFert_type) ? s.setFert_sprayRate : s.setFert_rate);

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
                ForeColor = isDay ? textColorDay : textColorNight,     // follows the day / night look like the other texts
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
            string product = FertProductOf(set);
            double rate = FertRateOf(set);
            string unit = FertUnit(set.setFert_type);
            double amount = hectares * rate;
            // the first line: the work and the area covered, the second: the product with the rate and the amount used (when there is one)
            string text = FertWorkName(set) + " | " + T("Covered: ", "Pokriveno: ") + hectares.ToString("N2", CultureInfo.CurrentCulture) + " ha";
            if (!string.IsNullOrEmpty(product) || rate > 0)
            {
                text += Environment.NewLine + product;
                if (rate > 0)
                    text += " (" + rate.ToString("N0", CultureInfo.CurrentCulture) + " " + unit + "/ha) | " + T("Used: ", "Utrošeno: ") + amount.ToString("N0", CultureInfo.CurrentCulture) + " " + unit;
            }
            Color want = isDay ? textColorDay : textColorNight;
            if (lblFertStatus.ForeColor != want) lblFertStatus.ForeColor = want;
            if (lblFertStatus.Text != text) lblFertStatus.Text = text;
            if (!lblFertStatus.Visible) lblFertStatus.Visible = true;
        }

        private const string FertCustomEntry = "Vlastiti unos";

        private static string FertColorKey(string type, string kind)
            => ((type ?? "") + "|" + (kind ?? "")).Replace('=', '_').Replace(';', '_').Trim();

        /// <summary>The colour chosen by the user for this work (null when none was chosen).</summary>
        private static Color? ChosenFertColor(string type, string kind)
        {
            string key = FertColorKey(type, kind);
            foreach (string part in (Properties.Settings.Default.setFert_colors ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0 || part.Substring(0, eq) != key) continue;
                string[] rgb = part.Substring(eq + 1).Split(',');
                if (rgb.Length == 3 && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int bl))
                    return Color.FromArgb(255, Math.Max(0, Math.Min(255, r)), Math.Max(0, Math.Min(255, g)), Math.Max(0, Math.Min(255, bl)));
            }
            return null;
        }

        /// <summary>Remembers the colour chosen for this work (null = back to the default colour).</summary>
        private static void SetChosenFertColor(string type, string kind, Color? color)
        {
            string key = FertColorKey(type, kind);
            var kept = new System.Collections.Generic.List<string>();
            foreach (string part in (Properties.Settings.Default.setFert_colors ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) != key) kept.Add(part);
            }
            if (color.HasValue) kept.Add(key + "=" + color.Value.R + "," + color.Value.G + "," + color.Value.B);
            Properties.Settings.Default.setFert_colors = string.Join(";", kept);
        }

        /// <summary>The colour of the coverage for a work: the chosen one, or the default of the kind of work (the same one is shown in the window).</summary>
        private static Color FertColor(string type, string kind)
        {
            Color? chosen = ChosenFertColor(type, kind);
            if (chosen.HasValue) return chosen.Value;
            kind = kind ?? "";
            if (type == "Gnojidba" && (kind.StartsWith("Osnovna") || kind.StartsWith("Predsjetvena"))) return Color.FromArgb(255, 0, 120, 255);
            if (type == "Gnojidba" && kind.StartsWith("Prihrana")) return Color.FromArgb(255, 50, 205, 50);
            if (type == "Prskanje") return Color.FromArgb(255, 200, 80, 220);
            if (type == "Obrada") return Color.FromArgb(255, 0, 160, 70);       // green (a darker green than the top dressing)
            return Color.FromArgb(255, 255, 160, 0);
        }

        /// <summary>The coverage in the colour of the work (switched on in the window; the colour setting itself is not changed).</summary>
        private void ApplyFertilisingColor()
        {
            var set = Properties.Settings.Default;
            if (!set.setFert_colorCoverage || string.IsNullOrEmpty(set.setFert_type)) return;
            sectionColorDay = FertColor(set.setFert_type, set.setFert_subtype);
        }

        private void ShowFertilisingDialog()
        {
            var set = Properties.Settings.Default;
            string colorsBefore = set.setFert_colors;      // the colours chosen in the window are kept only when the choice is accepted
            bool applied = false;
            using (var form = new Form())
            {
                form.Text = T("Operation", "Radnja");
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = new Font("Tahoma", 14F);
                form.ClientSize = new Size(560, 640);

                // every choice can be typed in as well ("Vlastiti unos"); the hotkeys of the program do not take the letters typed in
                ComboBox NewCombo(int top, params string[] items)
                {
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Left = 20, Top = top, Width = 520, DropDownWidth = 520 };
                    cb.Items.AddRange(items);
                    cb.Items.Add(FertCustomEntry);
                    // the entry "Vlastiti unos": the text is cleared and typed in
                    cb.SelectionChangeCommitted += (s, e) =>
                    {
                        if (cb.SelectedItem as string == FertCustomEntry) { cb.SelectedIndex = -1; cb.Text = ""; cb.Focus(); }
                    };
                    return cb;
                }

                var lblOp = new Label { Left = 20, Top = 15, AutoSize = true, Text = T("Operation", "Radnja") };
                var cbOp = NewCombo(45, FertOperations);
                cbOp.Text = string.IsNullOrEmpty(set.setFert_type) ? FertOperations[0] : set.setFert_type;
                var lblKind = new Label { Left = 20, Top = 85, AutoSize = true, Text = T("Kind", "Vrsta") };
                var cbKind = NewCombo(115);
                var lblProduct = new Label { Left = 20, Top = 155, AutoSize = true, Text = T("Fertiliser / product", "Gnojivo / sredstvo") };
                var cbProduct = NewCombo(185, FertProducts);
                // what the chosen preparation is for, under the choice
                var lblNote = new Label { Left = 20, Top = 222, Width = 520, Height = 38, ForeColor = Color.DimGray, Font = new Font("Tahoma", 10.5F) };
                var lblRate = new Label { Left = 20, Top = 265, AutoSize = true, Text = T("Rate (kg/ha)", "Norma (kg/ha)") };
                var nudRate = new NumericUpDown { Left = 20, Top = 295, Width = 200, DecimalPlaces = 1, Minimum = 0, Maximum = 5000, Increment = 10 };
                var chkColor = new CheckBox { Left = 20, Top = 360, Width = 440, Text = T("Coverage in the colour of the work", "Pokrivenost u boji radnje"), Checked = set.setFert_colorCoverage };
                // the colour that the coverage will have, next to the choice, and the buttons to choose another one or to go back to the default
                var swatch = new Panel { Left = 475, Top = 356, Width = 65, Height = 38, BorderStyle = BorderStyle.FixedSingle };
                var btnColor = new Button { Text = T("Colour ...", "Boja ..."), Left = 20, Top = 405, Width = 160, Height = 45 };
                var btnColorReset = new Button { Text = T("Default colour", "Zadana boja"), Left = 195, Top = 405, Width = 200, Height = 45 };

                string Op() => cbOp.Text.Trim();
                string Kind() => cbKind.Text.Trim();
                string lastOp = null, lastCategory = null;

                // the choices that depend on the operation; every operation keeps its own last product and rate (fertiliser, spraying)
                void FillForOperation(string wantedKind)
                {
                    string op = Op();
                    bool opChanged = op != lastOp;
                    lastOp = op;
                    string[] kinds = FertKinds.TryGetValue(op, out var k) ? k : new string[0];
                    bool spray = IsSpray(op);
                    string category = spray ? "spray" : (op == "Obrada" ? "none" : "fert");

                    if (opChanged)
                    {
                        string keep = wantedKind ?? Kind();
                        cbKind.Items.Clear();
                        cbKind.Items.AddRange(kinds);
                        cbKind.Items.Add(FertCustomEntry);
                        if (wantedKind != null) cbKind.Text = wantedKind;
                        else if (kinds.Length > 0 && Array.IndexOf(kinds, keep) < 0) cbKind.Text = kinds[0];
                    }
                    lblKind.Text = spray ? T("Crop", "Kultura") : T("Kind", "Vrsta");

                    bool hasProduct = op != "Obrada";
                    lblProduct.Visible = cbProduct.Visible = lblRate.Visible = nudRate.Visible = hasProduct;
                    lblProduct.Text = spray ? T("Preparation", "Preparat") : T("Fertiliser / product", "Gnojivo / sredstvo");
                    lblRate.Text = spray ? T("Rate (l/ha)", "Norma (l/ha)") : T("Rate (kg/ha)", "Norma (kg/ha)");
                    lblNote.Visible = spray;

                    if (category != lastCategory)
                    {
                        lastCategory = category;
                        cbProduct.Items.Clear();
                        // spraying: only the names in the list, what each is for is written under the choice
                        cbProduct.Items.AddRange(spray ? SprayNames() : FertProducts);
                        cbProduct.Items.Add(FertCustomEntry);
                        string storedProduct = spray ? set.setFert_sprayProduct : set.setFert_product;
                        double storedRate = spray ? set.setFert_sprayRate : set.setFert_rate;
                        cbProduct.Text = string.IsNullOrEmpty(storedProduct) ? (spray ? "" : FertProducts[0]) : storedProduct;
                        nudRate.Value = (decimal)Math.Min(5000.0, Math.Max(0.0, storedRate > 0 ? storedRate : (spray ? 0.0 : 250.0)));
                    }
                    lblNote.Text = spray ? SprayNoteFor(cbProduct.Text) : "";
                }
                void UpdateSwatch() => swatch.BackColor = FertColor(Op(), Kind());

                cbOp.TextChanged += (s, e) => { FillForOperation(null); UpdateSwatch(); };
                cbKind.TextChanged += (s, e) => UpdateSwatch();
                cbProduct.TextChanged += (s, e) => { if (IsSpray(Op())) lblNote.Text = SprayNoteFor(cbProduct.Text); };
                FillForOperation(set.setFert_subtype ?? "");
                UpdateSwatch();

                btnColor.Click += (s, e) =>
                {
                    using (var picker = new FormColorPicker(this, FertColor(Op(), Kind())))
                    {
                        if (picker.ShowDialog(form) == DialogResult.OK)
                        {
                            SetChosenFertColor(Op(), Kind(), picker.useThisColor);
                            chkColor.Checked = true;
                            UpdateSwatch();
                        }
                    }
                };
                btnColorReset.Click += (s, e) => { SetChosenFertColor(Op(), Kind(), null); UpdateSwatch(); };

                form.Controls.Add(lblOp);
                form.Controls.Add(cbOp);
                form.Controls.Add(lblKind);
                form.Controls.Add(cbKind);
                form.Controls.Add(lblProduct);
                form.Controls.Add(cbProduct);
                form.Controls.Add(lblNote);
                form.Controls.Add(lblRate);
                form.Controls.Add(nudRate);
                form.Controls.Add(chkColor);
                form.Controls.Add(swatch);
                form.Controls.Add(btnColor);
                form.Controls.Add(btnColorReset);

                var btnOk = new Button { Text = T("OK", "U redu"), Left = 20, Top = 550, Width = 150, Height = 60, DialogResult = DialogResult.OK };
                var btnSave = new Button { Text = T("Save the operation", "Spremi radnju"), Left = 185, Top = 550, Width = 200, Height = 60 };
                var btnCancel = new Button { Text = T("Cancel", "Odustani"), Left = 400, Top = 550, Width = 140, Height = 60, DialogResult = DialogResult.Cancel };
                form.Controls.Add(btnOk);
                form.Controls.Add(btnSave);
                form.Controls.Add(btnCancel);
                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                void Apply()
                {
                    applied = true;
                    string op = Op();
                    set.setFert_type = op;
                    set.setFert_subtype = Kind() == FertCustomEntry ? "" : Kind();
                    string typed = cbProduct.Text.Trim();
                    if (typed == FertCustomEntry) typed = "";
                    if (IsSpray(op))
                    {
                        // from the list: the name is saved and what it is for goes to the note; typed in: only the name
                        set.setFert_sprayProduct = SprayName(typed);
                        set.setFert_sprayNote = SprayNoteFor(typed);
                        set.setFert_sprayRate = (double)nudRate.Value;
                    }
                    else if (op != "Obrada")
                    {
                        set.setFert_product = typed;
                        set.setFert_rate = (double)nudRate.Value;
                    }
                    set.setFert_colorCoverage = chkColor.Checked;
                    set.Save();
                    ApplyFertilisingColor();
                    UpdateFertilisingStatus();
                }
                btnSave.Click += (s, e) =>
                {
                    Apply();
                    SaveFertilisingToFolder();     // does nothing when no field is open
                    if (!isJobStarted) form.DialogResult = DialogResult.OK;
                };

                if (form.ShowDialog(this) == DialogResult.OK) Apply();
                else if (!applied) set.setFert_colors = colorsBefore;
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
                // no field open: nothing to save (the choice itself is kept in the settings), no question asked
                if (!isJobStarted) return;
                string fieldDir = GetFieldDir(true);
                // the folder: the kind of the work (Prihrana 1, Oranje ...), for spraying the operation and the crop (Prskanje_Kukuruz)
                string folderName = IsSpray(set.setFert_type) && !string.IsNullOrEmpty(set.setFert_subtype) ? set.setFert_type + "_" + set.setFert_subtype
                    : (!string.IsNullOrEmpty(set.setFert_subtype) ? set.setFert_subtype : (string.IsNullOrEmpty(set.setFert_type) ? "Radnja" : set.setFert_type));
                string folder = Path.Combine(fieldDir, MakeFileName(folderName));
                Directory.CreateDirectory(folder);

                // one file per day: saving again the same day updates it, a new day starts a new file
                string stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                double hectares = fd.workedAreaTotal * glm.m2ha;
                string product = FertProductOf(set);
                double rate = FertRateOf(set);
                string unit = FertUnit(set.setFert_type);
                double amount = hectares * rate;

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
                    + (IsSpray(set.setFert_type) && !string.IsNullOrEmpty(set.setFert_subtype) ? "Kultura: " + set.setFert_subtype + Environment.NewLine : "")
                    + (IsSpray(set.setFert_type) ? "Preparat: " : "Gnojivo: ") + product + Environment.NewLine
                    + (IsSpray(set.setFert_type) && !string.IsNullOrEmpty(set.setFert_sprayNote) ? "Namjena: " + set.setFert_sprayNote + Environment.NewLine : "")
                    + "Norma_" + unit + "_ha: " + rate.ToString("F1", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Pokriveno_ha: " + hectares.ToString("F2", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Ukupno_" + unit + ": " + amount.ToString("F1", CultureInfo.InvariantCulture) + Environment.NewLine
                    + "Polje: " + currentFieldDirectory + Environment.NewLine
                    + "Datum: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine;
                string namePart = string.IsNullOrEmpty(product) ? MakeFileName(folderName) : MakeFileName(product);
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
