using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AgLibrary.Controls;

namespace AgOpenGPS
{
    /// <summary>
    /// Modern look for the main screen (with the dark theme on): the buttons of the right column,
    /// the bottom bar and the track flyout become rounded tiles like in the field menu.
    /// Only buttons that keep their background colour are changed (state is shown by their image);
    /// section and GPS status buttons, which change colour, are left alone.
    /// </summary>
    public partial class FormGPS
    {
        private static readonly Color TileDisabled = Color.FromArgb(38, 43, 50);

        private void ApplyMainScreenTiles()
        {
            if (!ModernTheme.Enabled) return;

            foreach (Control panel in new Control[] { panelRight, panelBottom, flp1 })
            {
                foreach (Control c in panel.Controls) MakeTile(c);
                panel.ControlAdded += (s, e) => MakeTile(e.Control);
            }
        }

        private static void MakeTile(Control c)
        {
            if (!(c is ButtonBase b)) return;
            if (b is CheckBox cb && cb.Appearance != Appearance.Button) return;

            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ModernTheme.TileHover;
            b.FlatAppearance.MouseDownBackColor = ModernTheme.TileHover;
            if (b is CheckBox) b.FlatAppearance.CheckedBackColor = ModernTheme.Accent;
            b.UseVisualStyleBackColor = false;
            b.BackColor = b.Enabled ? ModernTheme.Tile : TileDisabled;
            b.ForeColor = ModernTheme.Text;
            b.EnabledChanged += (s, e) => b.BackColor = b.Enabled ? ModernTheme.Tile : TileDisabled;

            void Round()
            {
                if (b.Width < 12 || b.Height < 12) return;
                int r = System.Math.Min(12, System.Math.Min(b.Width, b.Height) / 4);
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
