using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Lets the user hide buttons of the track flyout panel (flp1). An 'Edit' button stays in
    /// the panel so hidden buttons can be switched on again.
    /// </summary>
    public partial class FormGPS
    {
        private Button btnTrackPanelEdit;

        private void AddTrackPanelEditButton()
        {
            btnTrackPanelEdit = new Button
            {
                Name = "btnTrackPanelEdit",
                Text = T("Edit", "Uredi"),
                Font = new Font("Tahoma", 12F, FontStyle.Bold),
                Size = new Size(80, 44),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.WhiteSmoke,
                Margin = new Padding(0, 6, 0, 0)
            };
            btnTrackPanelEdit.FlatAppearance.BorderSize = 0;
            btnTrackPanelEdit.Click += (s, e) =>
            {
                flp1.Visible = false;
                using (var form = new Forms.FormTrackPanelButtons(TrackPanelButtonInfo(), BottomPanelButtonInfo()))
                {
                    if (form.ShowDialog(this) == DialogResult.OK) PanelUpdateRightAndBottom();
                }
            };
            flp1.Controls.Add(btnTrackPanelEdit);
        }

        /// <summary>Buttons of the track panel with a description, top to bottom as shown.</summary>
        private List<Tuple<Control, string>> TrackPanelButtonInfo()
        {
            return new List<Tuple<Control, string>>
            {
                Tuple.Create<Control, string>(btnRefNudge, T("Ref - move the reference line of the track permanently left/right",
                    "Ref - trajni pomak referentne linije traga lijevo/desno")),
                Tuple.Create<Control, string>(cboxAutoSnapToPivot, T("Auto snap - the line jumps to the vehicle when autosteer is switched on",
                    "Automatsko privlačenje - linija se pomakne na traktor kad uključite autopilot")),
                Tuple.Create<Control, string>(btnTracksOff, T("Guidance off - deactivates the current track",
                    "Isključi navođenje - deaktivira trenutni trag")),
                Tuple.Create<Control, string>(btnBuildTracks, T("Track list - choose, edit or create tracks",
                    "Popis tragova - odabir, uređivanje i izrada tragova")),
                Tuple.Create<Control, string>(btnPlusAB, T("Quick AB / A+ - make a line while driving",
                    "Brzi AB / A+ - napravi liniju iz vožnje")),
                Tuple.Create<Control, string>(btnABDraw, T("Draw AB - lines along the boundary, Auto plan",
                    "Generiraj AB - linije po međi, Auto plan")),
                Tuple.Create<Control, string>(btnNudge, T("Nudge - shift the line temporarily, 0 resets",
                    "Pomak linije - privremeni pomak lijevo/desno, 0 vraća")),
            };
        }

        /// <summary>Buttons of the bottom bar that may be hidden (the track menu button always stays).</summary>
        private List<Tuple<Control, string>> BottomPanelButtonInfo()
        {
            return new List<Tuple<Control, string>>
            {
                Tuple.Create<Control, string>(btnSnapToPivot, T("Snap to pivot - move the line to the vehicle", "Privuci liniju na traktor")),
                Tuple.Create<Control, string>(btnAdjLeft, T("Move the line left", "Pomakni liniju lijevo")),
                Tuple.Create<Control, string>(btnAdjRight, T("Move the line right", "Pomakni liniju desno")),
                Tuple.Create<Control, string>(btnFlag, T("Flag - mark a point", "Zastavica - označi točku")),
                Tuple.Create<Control, string>(btnHeadlandOnOff, T("Headland on/off", "Uvratine uključi/isključi")),
                Tuple.Create<Control, string>(cboxIsSectionControlled, T("Sections switched by the headland", "Sekcije se gase na uvratini")),
                Tuple.Create<Control, string>(btnHydLift, T("Hydraulic lift", "Hidraulično dizanje")),
                Tuple.Create<Control, string>(btnTramDisplayMode, T("Tram lines display", "Prikaz tragova u polju (tramline)")),
                Tuple.Create<Control, string>(btnResetToolHeading, T("Reset tool heading - straighten the tool behind the vehicle", "Poravnaj priključak iza traktora")),
                Tuple.Create<Control, string>(btnChangeMappingColor, T("Coverage colour", "Boja obrađene površine")),
                Tuple.Create<Control, string>(btnYouSkipEnable, T("Skip rows in U-turns on/off", "Preskakanje redova kod okretanja")),
                Tuple.Create<Control, string>(cboxpRowWidth, T("Number of rows to skip", "Broj redova za preskakanje")),
            };
        }

        private static HashSet<string> HiddenSet(string csv) =>
            new HashSet<string>((csv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));

        public bool IsBottomPanelHidden(Control c) => HiddenSet(Properties.Settings.Default.setBottomPanel_hidden).Contains(c.Name);

        private void ApplyBottomPanelHidden()
        {
            var hidden = HiddenSet(Properties.Settings.Default.setBottomPanel_hidden);
            foreach (Control c in panelBottom.Controls)
                if (hidden.Contains(c.Name)) c.Visible = false;
        }

        private void ApplyTrackPanelHidden()
        {
            var hidden = new HashSet<string>((Properties.Settings.Default.setTrackPanel_hidden ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            foreach (Control c in flp1.Controls)
                if (hidden.Contains(c.Name)) c.Visible = false;
            if (btnTrackPanelEdit != null) btnTrackPanelEdit.Visible = true;
        }
    }
}

namespace AgOpenGPS.Forms
{
    public class FormTrackPanelButtons : Form
    {
        private readonly List<Tuple<Control, CheckBox>> rows = new List<Tuple<Control, CheckBox>>();
        private readonly List<Tuple<Control, CheckBox>> bottomRows = new List<Tuple<Control, CheckBox>>();

        public FormTrackPanelButtons(List<Tuple<Control, string>> buttons, List<Tuple<Control, string>> bottomButtons = null)
        {
            Text = FormGPS.T("Track panel buttons", "Gumbi panela tragova");
            Font = new Font("Tahoma", 13F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.WhiteSmoke;
            Padding = new Padding(12);

            var hidden = new HashSet<string>((Properties.Settings.Default.setTrackPanel_hidden ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));

            var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            var scroll = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            scroll.Controls.Add(table);
            table.Dock = DockStyle.Top;
            Controls.Add(scroll);
            AutoSize = false;
            Size = new Size(760, Math.Min(900, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 40));

            var head = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(640, 0),
                ForeColor = Color.DimGray,
                Margin = new Padding(3, 3, 3, 10),
                Text = FormGPS.T("Tick the buttons to show. 'Edit' in the track panel and the track menu button always stay.",
                    "Označite gumbe koji se prikazuju. Gumb 'Uredi' u panelu tragova i gumb izbornika tragova uvijek ostaju.")
            };
            table.Controls.Add(head, 0, 0);
            table.SetColumnSpan(head, 3);

            int row = 1;
            row = AddSection(table, row, FormGPS.T("Track panel", "Panel tragova"), buttons, hidden, rows);
            if (bottomButtons != null)
                row = AddSection(table, row, FormGPS.T("Bottom bar", "Donja traka"), bottomButtons,
                    new HashSet<string>((Properties.Settings.Default.setBottomPanel_hidden ?? "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())), bottomRows);
            var buttonsPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 14, 3, 3) };
            var ok = new Button { Text = FormGPS.T("Save", "Spremi"), Size = new Size(150, 56), BackColor = Color.FromArgb(31, 122, 58), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = FormGPS.T("Cancel", "Odustani"), Size = new Size(140, 56), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                Properties.Settings.Default.setTrackPanel_hidden =
                    string.Join(",", rows.Where(r => !r.Item2.Checked).Select(r => r.Item1.Name));
                Properties.Settings.Default.setBottomPanel_hidden =
                    string.Join(",", bottomRows.Where(r => !r.Item2.Checked).Select(r => r.Item1.Name));
                Properties.Settings.Default.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            buttonsPanel.Controls.Add(ok);
            buttonsPanel.Controls.Add(cancel);
            table.Controls.Add(buttonsPanel, 0, row);
            table.SetColumnSpan(buttonsPanel, 3);
            CancelButton = cancel;
        }

        private static int AddSection(TableLayoutPanel table, int row, string title, List<Tuple<Control, string>> buttons,
            HashSet<string> hidden, List<Tuple<Control, CheckBox>> target)
        {
            var head = new Label { Text = title, AutoSize = true, Font = new Font("Tahoma", 14F, FontStyle.Bold), Margin = new Padding(3, 12, 3, 4) };
            table.Controls.Add(head, 0, row);
            table.SetColumnSpan(head, 3);
            row++;
            foreach (var b in buttons)
            {
                Image img = (b.Item1 as ButtonBase)?.Image ?? (b.Item1 as ButtonBase)?.BackgroundImage;
                var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(54, 54), Margin = new Padding(3, 4, 10, 4) };
                var cb = new CheckBox { Checked = !hidden.Contains(b.Item1.Name), AutoSize = true, Anchor = AnchorStyles.Left, Text = FormGPS.T("Show", "Prikaži"), Margin = new Padding(3, 4, 12, 4) };
                var lbl = new Label { Text = b.Item2, AutoSize = true, MaximumSize = new Size(470, 0), Anchor = AnchorStyles.Left };
                table.Controls.Add(pic, 0, row);
                table.Controls.Add(cb, 1, row);
                table.Controls.Add(lbl, 2, row);
                target.Add(Tuple.Create(b.Item1, cb));
                row++;
            }
            return row;
        }
    }
}
