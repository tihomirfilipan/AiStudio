using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AgLibrary.Controls
{
    /// <summary>
    /// Dark modern look applied at runtime to every open form: dark backgrounds, light text,
    /// flat buttons and dark menus. Only neutral (grey/white) colours are replaced, so colours
    /// that show a state (sections, autosteer, warnings) stay as they are. OpenGL views and
    /// pictures are not touched. Forms with Tag "modern-themed" have their own look.
    /// </summary>
    public static class ModernTheme
    {
        public static bool Enabled;

        public static readonly Color Back = Color.FromArgb(30, 35, 41);
        public static readonly Color PanelBack = Color.FromArgb(38, 44, 51);
        public static readonly Color Tile = Color.FromArgb(50, 58, 68);
        public static readonly Color TileHover = Color.FromArgb(64, 74, 86);
        public static readonly Color InputBack = Color.FromArgb(44, 51, 60);
        public static readonly Color Text = Color.FromArgb(234, 237, 240);
        public static readonly Color Accent = Color.FromArgb(39, 140, 72);

        private static readonly ConditionalWeakTable<object, object> styled = new ConditionalWeakTable<object, object>();
        private static bool installed;

        /// <summary>Starts styling every form that opens (once per control).</summary>
        public static void Install()
        {
            if (installed) return;
            installed = true;
            Application.Idle += (s, e) =>
            {
                if (!Enabled) return;
                foreach (Form f in Application.OpenForms)
                {
                    if (f.Tag is string t && t == "modern-themed") continue;
                    Hook(f);
                }
            };
        }

        private static void Hook(Control c)
        {
            if (styled.TryGetValue(c, out _)) return;
            styled.Add(c, null);

            Style(c);
            c.ControlAdded += (s, e) => Hook(e.Control);
            foreach (Control child in c.Controls) Hook(child);
        }

        /// <summary>Light grey, white or near-white (incl. lavender) colours that are only decoration.</summary>
        private static bool IsLightNeutral(Color c)
        {
            if (c.IsEmpty || c.A < 255) return false;
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min < 40 && min > 175;
        }

        private static bool IsDarkText(Color c)
        {
            if (c.IsEmpty) return true;
            return c.GetBrightness() < 0.45f;
        }

        /// <summary>Background actually seen behind a control (transparent ones show their parent).</summary>
        private static Color EffectiveBack(Control c)
        {
            Control x = c;
            while (x != null && (x.BackColor == Color.Transparent || x.BackColor.A < 255)) x = x.Parent;
            return x?.BackColor ?? Back;
        }

        private static bool OnDark(Control c) => EffectiveBack(c).GetBrightness() < 0.5f;

        private static void Style(Control c)
        {
            string typeName = c.GetType().Name;
            if (typeName.Contains("GLControl") || c is PictureBox) return;

            switch (c)
            {
                case Form f:
                    f.BackColor = Back;
                    f.ForeColor = Text;
                    break;

                case ButtonBase b when !(b is CheckBox cb0 && cb0.Appearance == Appearance.Normal)
                                     && !(b is RadioButton rb0 && rb0.Appearance == Appearance.Normal):
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = TileHover;
                    if (b.BackColor != Color.Transparent && IsLightNeutral(b.BackColor))
                    {
                        b.UseVisualStyleBackColor = false;
                        b.BackColor = Tile;
                    }
                    if (IsDarkText(b.ForeColor) && OnDark(b)) b.ForeColor = Text;
                    break;

                case Label _:
                case CheckBox _:
                case RadioButton _:
                    if (IsLightNeutral(c.BackColor)) c.BackColor = Color.Transparent;
                    if (IsDarkText(c.ForeColor) && OnDark(c)) c.ForeColor = Text;
                    break;

                case TextBoxBase _:
                case UpDownBase _:
                case ComboBox _:
                case ListBox _:
                    if (IsLightNeutral(c.BackColor)) c.BackColor = InputBack;
                    if (IsDarkText(c.ForeColor)) c.ForeColor = Text;
                    break;

                case ListView lv:
                    if (IsLightNeutral(lv.BackColor)) lv.BackColor = InputBack;
                    if (IsDarkText(lv.ForeColor)) lv.ForeColor = Text;
                    break;

                case DataGridView g:
                    g.EnableHeadersVisualStyles = false;
                    g.BackgroundColor = PanelBack;
                    g.GridColor = Tile;
                    g.DefaultCellStyle.BackColor = InputBack;
                    g.DefaultCellStyle.ForeColor = Text;
                    g.DefaultCellStyle.SelectionBackColor = Accent;
                    g.ColumnHeadersDefaultCellStyle.BackColor = Tile;
                    g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
                    g.RowHeadersDefaultCellStyle.BackColor = Tile;
                    g.RowHeadersDefaultCellStyle.ForeColor = Text;
                    break;

                case ToolStrip ts:
                    StyleToolStrip(ts);
                    break;

                default:
                    // panels, table/flow layouts, group boxes, user controls...
                    if (IsLightNeutral(c.BackColor)) c.BackColor = PanelBack;
                    if (IsDarkText(c.ForeColor) && OnDark(c)) c.ForeColor = Text;
                    break;
            }

            if (c.ContextMenuStrip != null) StyleToolStrip(c.ContextMenuStrip);
        }

        private static void StyleToolStrip(ToolStrip ts)
        {
            ts.Renderer = new DarkRenderer();
            if (IsLightNeutral(ts.BackColor) || ts.BackColor == SystemColors.Control) ts.BackColor = PanelBack;
            ts.ForeColor = Text;
            foreach (ToolStripItem item in ts.Items) StyleItem(item);
            ts.ItemAdded += (s, e) => StyleItem(e.Item);
        }

        private static void StyleItem(ToolStripItem item)
        {
            if (IsDarkText(item.ForeColor)) item.ForeColor = Text;
            if (item is ToolStripDropDownItem dd)
            {
                dd.DropDown.Renderer = new DarkRenderer();
                dd.DropDown.BackColor = PanelBack;
                foreach (ToolStripItem child in dd.DropDownItems) StyleItem(child);
            }
        }

        private class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (IsDarkText(e.TextColor)) e.TextColor = Text;
                base.OnRenderItemText(e);
            }
        }

        private class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => TileHover;
            public override Color MenuItemSelectedGradientBegin => TileHover;
            public override Color MenuItemSelectedGradientEnd => TileHover;
            public override Color MenuItemPressedGradientBegin => Tile;
            public override Color MenuItemPressedGradientEnd => Tile;
            public override Color MenuItemBorder => TileHover;
            public override Color MenuBorder => Tile;
            public override Color ToolStripDropDownBackground => PanelBack;
            public override Color ImageMarginGradientBegin => PanelBack;
            public override Color ImageMarginGradientMiddle => PanelBack;
            public override Color ImageMarginGradientEnd => PanelBack;
            public override Color MenuStripGradientBegin => PanelBack;
            public override Color MenuStripGradientEnd => PanelBack;
            public override Color ToolStripGradientBegin => PanelBack;
            public override Color ToolStripGradientMiddle => PanelBack;
            public override Color ToolStripGradientEnd => PanelBack;
            public override Color StatusStripGradientBegin => PanelBack;
            public override Color StatusStripGradientEnd => PanelBack;
            public override Color SeparatorDark => Tile;
            public override Color SeparatorLight => Tile;
            public override Color ButtonSelectedHighlight => TileHover;
            public override Color ButtonSelectedGradientBegin => TileHover;
            public override Color ButtonSelectedGradientEnd => TileHover;
            public override Color ButtonPressedGradientBegin => Tile;
            public override Color ButtonPressedGradientEnd => Tile;
            public override Color CheckBackground => Accent;
            public override Color CheckSelectedBackground => Accent;
        }
    }
}
