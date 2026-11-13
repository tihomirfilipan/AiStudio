using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Fast simulation: x1 / x10 / x50 sim steps per timer tick. Every step is a normal step
    /// (same distance, same guidance update), only more of them run per tick.
    /// </summary>
    public partial class FormGPS
    {
        public int simTimeFactor = 1;
        public bool isSimSubStep;
        private Button[] btnSimTime;
        private static readonly int[] SimFactors = { 1, 10, 50 };

        private void AddSimTimeButton()
        {
            // three buttons side by side: PLAY x1, x10, x50 - the one in use is orange
            btnSimTime = new Button[SimFactors.Length];
            for (int i = 0; i < SimFactors.Length; i++)
            {
                int factor = SimFactors[i];
                var b = new Button
                {
                    Text = i == 0 ? "PLAY x1" : "x" + factor,
                    Font = new Font("Tahoma", 12F, FontStyle.Bold),
                    Size = new Size(i == 0 ? 100 : 66, Math.Max(40, panelSim.Height)),
                    FlatStyle = FlatStyle.Flat,
                    Anchor = AnchorStyles.Bottom,
                    Visible = panelSim.Visible
                };
                b.Click += (s, e) => SetSimTimeFactor(factor);
                (panelSim.Parent ?? this).Controls.Add(b);
                btnSimTime[i] = b;
            }
            SetSimTimeFactor(1);

            void Place()
            {
                // left of the simulator controls, where the map is free (the right side is covered
                // by the button panel on many screens)
                int x = panelSim.Left - 6;
                for (int i = btnSimTime.Length - 1; i >= 0; i--)
                {
                    Button b = btnSimTime[i];
                    b.Height = Math.Max(40, panelSim.Height);
                    x -= b.Width;
                    b.Location = new Point(Math.Max(0, x), panelSim.Bottom - b.Height);
                    x -= 2;
                    b.BringToFront();
                    b.Visible = panelSim.Visible;
                }
                if (!panelSim.Visible && simTimeFactor != 1) SetSimTimeFactor(1);
            }
            panelSim.LocationChanged += (s, e) => Place();
            panelSim.SizeChanged += (s, e) => Place();
            panelSim.VisibleChanged += (s, e) => Place();
            Place();
        }

        private void SetSimTimeFactor(int factor)
        {
            simTimeFactor = factor;
            if (btnSimTime == null) return;
            for (int i = 0; i < btnSimTime.Length; i++)
                btnSimTime[i].BackColor = SimFactors[i] == factor ? Color.Orange : Color.WhiteSmoke;
        }
    }
}

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private void AddThemeMenu()
        {
            var item = new System.Windows.Forms.ToolStripMenuItem
            {
                Text = T("Dark theme", "Tamni izgled"),
                Font = menustripLanguage.Font,
                Size = menustripLanguage.Size,
                CheckOnClick = true,
                Checked = Properties.Settings.Default.setDisplay_modernTheme
            };
            item.Click += (s, e) =>
            {
                Properties.Settings.Default.setDisplay_modernTheme = item.Checked;
                Properties.Settings.Default.Save();
                Forms.FormDialog.Show(T("Dark theme", "Tamni izgled"),
                    T("Restart AgOpenGPS to apply the change.", "Ponovno pokrenite AgOpenGPS da se promjena primijeni."),
                    Forms.DialogSeverity.Info);
            };
            int idx = fileToolStripMenuItem.DropDownItems.IndexOf(menustripLanguage);
            fileToolStripMenuItem.DropDownItems.Insert(idx + 1, item);
        }
    }
}
