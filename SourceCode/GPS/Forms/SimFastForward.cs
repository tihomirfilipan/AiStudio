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
                Text = "x1",
                Font = new Font("Tahoma", 14F, FontStyle.Bold),
                Size = new Size(70, panelSim.Height),
                BackColor = Color.WhiteSmoke,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Bottom,
                Visible = panelSim.Visible
            };
            btnSimTimeFactor.Click += (s, e) =>
            {
                simTimeFactor = simTimeFactor == 1 ? 10 : (simTimeFactor == 10 ? 50 : 1);
                btnSimTimeFactor.Text = "x" + simTimeFactor;
                btnSimTimeFactor.BackColor = simTimeFactor == 1 ? Color.WhiteSmoke : Color.Orange;
            };

            (panelSim.Parent ?? this).Controls.Add(btnSimTimeFactor);
            btnSimTimeFactor.BringToFront();

            void Place()
            {
                btnSimTimeFactor.Location = new Point(panelSim.Right + 4, panelSim.Top);
                btnSimTimeFactor.Height = panelSim.Height;
                btnSimTimeFactor.Visible = panelSim.Visible;
                if (!panelSim.Visible && simTimeFactor != 1)
                {
                    simTimeFactor = 1;
                    btnSimTimeFactor.Text = "x1";
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
