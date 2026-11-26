using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AgOpenGPS
{
    /// <summary>
    /// The files of the operations (Radnje): reading the patches of the coverage from Sections.txt, the area of some of them and the names of
    /// the files. Pure functions without any window, so they can be tried on their own.
    /// </summary>
    internal static class FertilisingFiles
    {
        /// <summary>The patches of a Sections.txt: each is the line with the number N and the N lines after it (the colour first, then the vertices).</summary>
        public static List<List<string>> ReadBlocks(string path)
        {
            var blocks = new List<List<string>>();
            if (!File.Exists(path)) return blocks;
            string[] lines = File.ReadAllLines(path);
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i].Trim();
                // (a line that is not a whole number is skipped, as the loader of the coverage does)
                if (line.Length > 0 && int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                {
                    var block = new List<string> { line };
                    for (int k = 1; k <= n && i + k < lines.Length; k++) block.Add(lines[i + k]);
                    blocks.Add(block);
                    i += n + 1;
                }
                else i++;
            }
            return blocks;
        }

        /// <summary>The area (m2) of the patches, worked out the same way as when the coverage of a field is loaded.</summary>
        public static double AreaOfBlocks(IEnumerable<List<string>> blocks)
        {
            double area = 0;
            foreach (List<string> block in blocks)
            {
                // block[0] = the number of lines, block[1] = the colour, block[2..] = the vertices of the strip
                int count = block.Count - 1;
                var e = new double[count];
                var nn = new double[count];
                for (int j = 0; j < count; j++)
                {
                    string[] parts = block[j + 1].Split(',');
                    if (parts.Length < 2 ||
                        !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e[j]) ||
                        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out nn[j]))
                    { e[j] = 0; nn[j] = 0; }
                }
                int verts = count - 2;
                if (verts < 2) continue;
                for (int j = 1; j < verts; j++)
                {
                    double temp = e[j] * (nn[j + 1] - nn[j + 2]) + e[j + 1] * (nn[j + 2] - nn[j]) + e[j + 2] * (nn[j] - nn[j + 1]);
                    area += Math.Abs(temp * 0.5);
                }
            }
            return area;
        }

        /// <summary>The patches as text, in the format of Sections.txt.</summary>
        public static string BlocksText(IEnumerable<List<string>> blocks)
        {
            var sb = new StringBuilder();
            foreach (List<string> block in blocks)
                foreach (string line in block) sb.Append(line.TrimEnd('\r', '\n')).Append(Environment.NewLine);
            return sb.ToString();
        }

        /// <summary>A part of a file name: spaces and the characters not allowed in a name become "_".</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "x";
            var sb = new StringBuilder(text.Trim().Length);
            // (the characters not allowed in a name on Windows, whatever system this runs on, and those of the system)
            char[] bad = ("\\/:*?\"<>|" + new string(Path.GetInvalidFileNameChars())).ToCharArray();
            foreach (char c in text.Trim())
                sb.Append(c == ' ' || Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        /// <summary>yyyy-MM-dd_Field_Folder_Product_Vehicle.txt (one file a day for a field, a work, a product and a vehicle).</summary>
        public static string FileName(DateTime day, string field, string folder, string product, string vehicle)
        {
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + Clean(field) + "_" + Clean(folder) + "_"
                + Clean(string.IsNullOrWhiteSpace(product) ? folder : product) + "_" + Clean(vehicle) + ".txt";
        }
    }
}
