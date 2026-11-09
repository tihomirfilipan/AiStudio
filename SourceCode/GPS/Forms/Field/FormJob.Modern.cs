using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Modern dark look for the field menu: header with title and clock, large rounded tiles,
    /// the main action (resume, otherwise new field) highlighted. Only the look changes.
    /// </summary>
    public partial class FormJob
    {
        private static readonly Color ThemeBack = Color.FromArgb(28, 33, 39);
        private static readonly Color ThemeTile = Color.FromArgb(44, 51, 60);
        private static readonly Color ThemeTileHover = Color.FromArgb(58, 68, 80);
        private static readonly Color ThemeTileDisabled = Color.FromArgb(36, 41, 48);
        private static readonly Color ThemeAccent = Color.FromArgb(39, 140, 72);
        private static readonly Color ThemeAccentHover = Color.FromArgb(52, 163, 88);
        private static readonly Color ThemeText = Color.FromArgb(236, 239, 242);
        private static readonly Color ThemeSubText = Color.FromArgb(160, 170, 180);

        private Label lblClock;
        private Timer clockTimer;

        private void ApplyModernLook()
        {
            const int headerHeight = 84;

            SuspendLayout();
            Tag = "modern-themed"; // own look, the general dark theme leaves it alone
            BackColor = ThemeBack;
            ForeColor = ThemeText;

            // room for the header
            foreach (Control c in Controls) c.Top += headerHeight;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + headerHeight);

            var header = new Panel { Location = new Point(0, 0), Size = new Size(ClientSize.Width, headerHeight), BackColor = Color.FromArgb(20, 24, 29), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var title = new Label
            {
                Text = FormGPS.T("Field", "Parcela"),
                Font = new Font("Segoe UI", 22F, FontStyle.Bold),
                ForeColor = ThemeText,
                AutoSize = true,
                Location = new Point(18, 8),
                BackColor = Color.Transparent
            };
            var sub = new Label
            {
                Text = mf.isJobStarted
                    ? FormGPS.T("Open: ", "Otvorena: ") + mf.currentFieldDirectory
                    : (string.IsNullOrEmpty(mf.currentFieldDirectory) ? FormGPS.T("No field open", "Nijedna parcela nije otvorena")
                        : FormGPS.T("Last: ", "Zadnja: ") + mf.currentFieldDirectory),
                Font = new Font("Segoe UI", 11F),
                ForeColor = ThemeSubText,
                AutoSize = true,
                Location = new Point(21, 50),
                BackColor = Color.Transparent
            };
            lblClock = new Label
            {
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                ForeColor = ThemeText,
                AutoSize = true,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            var accentLine = new Panel { Location = new Point(0, headerHeight - 3), Size = new Size(ClientSize.Width, 3), BackColor = ThemeAccent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            header.Controls.Add(title);
            header.Controls.Add(sub);
            header.Controls.Add(lblClock);
            header.Controls.Add(accentLine);
            Controls.Add(header);
            header.BringToFront();

            UpdateClock();
            clockTimer = new Timer { Interval = 1000 };
            clockTimer.Tick += (s, e) => UpdateClock();
            clockTimer.Start();
            FormClosed += (s, e) => { clockTimer.Stop(); clockTimer.Dispose(); };

            // main action
            Button primary = btnJobResume.Enabled ? btnJobResume : (btnJobNew.Enabled ? btnJobNew : null);

            StyleChildren(this, primary);
            ResumeLayout(true);
        }

        private void UpdateClock()
        {
            if (lblClock == null) return;
            lblClock.Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
            lblClock.Location = new Point(ClientSize.Width - lblClock.PreferredWidth - 20, 22);
        }

        private void StyleChildren(Control parent, Button primary)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button b) StyleTile(b, b == primary);
                else if (c is Label l && l != lblClock && l.ForeColor != ThemeSubText && !(l.Parent is Panel p && p.BackColor == Color.FromArgb(20, 24, 29)))
                {
                    l.ForeColor = ThemeSubText;
                    l.BackColor = Color.Transparent;
                }
                else if (c is TableLayoutPanel || (c is Panel && c.BackColor != Color.FromArgb(20, 24, 29) && c.BackColor != ThemeAccent))
                {
                    c.BackColor = ThemeBack;
                }

                if (c.HasChildren && !(c is Button)) StyleChildren(c, primary);
            }
        }

        private static void StyleTile(Button b, bool isPrimary)
        {
            Color normal = isPrimary ? ThemeAccent : ThemeTile;
            Color hover = isPrimary ? ThemeAccentHover : ThemeTileHover;

            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.UseVisualStyleBackColor = false;
            b.BackColor = b.Enabled ? normal : ThemeTileDisabled;
            b.ForeColor = ThemeText;
            b.Font = new Font("Segoe UI", isPrimary ? 13F : 12F, FontStyle.Bold);
            b.Cursor = Cursors.Hand;

            b.EnabledChanged += (s, e) => b.BackColor = b.Enabled ? normal : ThemeTileDisabled;

            void Round()
            {
                if (b.Width < 10 || b.Height < 10) return;
                const int r = 14;
                var path = new GraphicsPath();
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(b.Width - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
                path.AddArc(b.Width - r * 2 - 1, b.Height - r * 2 - 1, r * 2, r * 2, 0, 90);
                path.AddArc(0, b.Height - r * 2 - 1, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                b.Region?.Dispose();
                b.Region = new Region(path);
            }
            b.SizeChanged += (s, e) => Round();
            Round();
        }
    }
}
