using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;

namespace AgLibrary.Translations
{
    /// <summary>
    /// Translates UI text that is hard coded in forms (Designer files, message boxes),
    /// using an embedded "English TAB Translation" dictionary per language.
    /// Every open form is scanned once; afterwards text changes are caught through
    /// TextChanged, so runtime assignments with known English text are translated too.
    /// Only exact matches (ignoring surrounding whitespace) are replaced.
    /// </summary>
    public static class UiTranslator
    {
        private static Dictionary<string, string> _map;
        private static readonly ConditionalWeakTable<object, object> _hooked = new ConditionalWeakTable<object, object>();
        private static bool _isApplying;

        /// <summary>
        /// Call once at startup, after the UI culture is set. Does nothing if there is
        /// no dictionary for the current UI language.
        /// </summary>
        public static void Install()
        {
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            _map = LoadMap(lang);
            if (_map == null || _map.Count == 0) return;

            Application.Idle += (s, e) =>
            {
                foreach (Form form in Application.OpenForms)
                {
                    HookControl(form);
                }
            };
            Log.EventWriter($"UiTranslator: {_map.Count} UI texts loaded for '{lang}'");
        }

        /// <summary>
        /// Returns the translation of a text, or the text itself if there is none.
        /// </summary>
        public static string Translate(string text)
        {
            if (_map == null || string.IsNullOrEmpty(text)) return text;

            string trimmed = text.Trim();
            if (trimmed.Length == 0 || !_map.TryGetValue(trimmed, out string translated)) return text;

            // Keep the original surrounding whitespace
            int start = text.IndexOf(trimmed, StringComparison.Ordinal);
            return text.Substring(0, start) + translated + text.Substring(start + trimmed.Length);
        }

        private static Dictionary<string, string> LoadMap(string lang)
        {
            string resourceName = $"AgLibrary.Translations.UiText_{lang}.txt";
            Assembly asm = typeof(UiTranslator).Assembly;
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue; // comment or empty line

                        string key = Unescape(line.Substring(0, tab)).Trim();
                        string value = Unescape(line.Substring(tab + 1)).Trim();
                        if (key.Length > 0 && value.Length > 0) map[key] = value;
                    }
                }
                return map;
            }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case 'r': sb.Append('\r'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append('\\').Append(n); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static bool TryMarkHooked(object item)
        {
            if (_hooked.TryGetValue(item, out _)) return false;
            _hooked.Add(item, null);
            return true;
        }

        private static void HookControl(Control control)
        {
            // Never touch user input fields
            if (control is TextBoxBase || control is ComboBox || control is UpDownBase) return;
            if (!TryMarkHooked(control)) return;

            ApplyText(control);
            control.TextChanged += (s, e) => ApplyText((Control)s);
            control.ControlAdded += (s, e) => HookControl(e.Control);

            if (control.ContextMenuStrip != null) HookControl(control.ContextMenuStrip);

            if (control is ToolStrip toolStrip)
            {
                foreach (ToolStripItem item in toolStrip.Items) HookItem(item);
                toolStrip.ItemAdded += (s, e) => HookItem(e.Item);
            }

            foreach (Control child in control.Controls) HookControl(child);
        }

        private static void HookItem(ToolStripItem item)
        {
            if (item is ToolStripTextBox || item is ToolStripComboBox) return;
            if (!TryMarkHooked(item)) return;

            ApplyItemText(item);
            item.TextChanged += (s, e) => ApplyItemText((ToolStripItem)s);

            if (item is ToolStripDropDownItem dropDownItem)
            {
                foreach (ToolStripItem child in dropDownItem.DropDownItems) HookItem(child);
                dropDownItem.DropDown.ItemAdded += (s, e) => HookItem(e.Item);
            }
        }

        private static void ApplyText(Control control)
        {
            if (_isApplying) return;
            string translated = Translate(control.Text);
            if (ReferenceEquals(translated, control.Text) || translated == control.Text) return;
            try
            {
                _isApplying = true;
                control.Text = translated;
            }
            finally
            {
                _isApplying = false;
            }
        }

        private static void ApplyItemText(ToolStripItem item)
        {
            if (_isApplying) return;
            string translated = Translate(item.Text);
            if (ReferenceEquals(translated, item.Text) || translated == item.Text) return;
            try
            {
                _isApplying = true;
                item.Text = translated;
            }
            finally
            {
                _isApplying = false;
            }
        }
    }
}
