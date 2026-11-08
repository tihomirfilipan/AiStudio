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
        private Button btnSimTimeFactor;

        private void AddSimTimeButton()
        {
            btnSimTimeFactor = new Button
            {
                Text = "PLAY x1",
                Font = new Font("Tahoma", 12F, FontStyle.Bold),
                Size = new Size(110, Math.Max(40, panelSim.Height)),
                BackColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Bottom,
                Visible = panelSim.Visible
            };
            btnSimTimeFactor.Click += (s, e) =>
            {
                simTimeFactor = simTimeFactor == 1 ? 10 : (simTimeFactor == 10 ? 50 : 1);
                btnSimTimeFactor.Text = "PLAY x" + simTimeFactor;
                btnSimTimeFactor.BackColor = simTimeFactor == 1 ? Color.WhiteSmoke : Color.Orange;
            };

            (panelSim.Parent ?? this).Controls.Add(btnSimTimeFactor);
            btnSimTimeFactor.BringToFront();

            void Place()
            {
                // left of the simulator controls, where the map is free (the right side is covered
                // by the button panel on many screens)
                btnSimTimeFactor.Height = Math.Max(40, panelSim.Height);
                btnSimTimeFactor.Location = new Point(Math.Max(0, panelSim.Left - btnSimTimeFactor.Width - 6),
                    panelSim.Bottom - btnSimTimeFactor.Height);
                btnSimTimeFactor.BringToFront();
                btnSimTimeFactor.Visible = panelSim.Visible;
                if (!panelSim.Visible && simTimeFactor != 1)
                {
                    simTimeFactor = 1;
                    btnSimTimeFactor.Text = "PLAY x1";
                    btnSimTimeFactor.BackColor = Color.WhiteSmoke;
                }
            }
            panelSim.LocationChanged += (s, e) => Place();
            panelSim.SizeChanged += (s, e) => Place();
            panelSim.VisibleChanged += (s, e) => Place();
            Place();
        }
    }
}
