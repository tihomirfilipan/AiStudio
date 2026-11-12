using AgLibrary.Logging;
using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Drawing;
using AgOpenGPS.Core.Models;
using AgOpenGPS.IO;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgOpenGPS
{
    /// <summary>
    /// Map shown on the main screen while no field is open:
    /// satellite background around the view, outlines and names of all saved fields
    /// (click a field to open it), and a free view mode to pan (drag) and rotate the map.
    /// </summary>
    public partial class FormGPS
    {
        private class SavedFieldOutline
        {
            public string Name;
            public string FieldFile;
            public List<List<Wgs84>> RingsWgs = new List<List<Wgs84>>();
            // rings in the current local plane, rebuilt when the plane changes
            public List<List<vec2>> Rings = new List<List<vec2>>();
            public double MinE, MaxE, MinN, MaxN;
        }

        // ---- saved fields ----
        private List<SavedFieldOutline> savedFields;
        private LocalPlane savedFieldsPlane;
        private bool savedFieldsLoading;
        private bool fieldBrowserWasJobStarted = true;

        // ---- free view (pan / rotate) ----
        public bool isFreeView;
        private double freeE, freeN, freeHeadingDeg;
        private bool freeViewSavedFollow;

        // ---- picking ----
        private readonly double[] fbModel = new double[16];
        private readonly double[] fbProj = new double[16];
        private bool fbMatricesValid;
        private Point fbDownPoint;
        private bool fbMouseDown, fbDragging;
        private double[] fbDownInverse;
        private double fbDownE, fbDownN;

        // ---- buttons ----
        private FlowLayoutPanel panelFieldBrowser;
        private Button btnFreeView;

        private static readonly Color FbButtonColor = Color.FromArgb(38, 50, 56);
        private static readonly Color FbButtonActive = Color.FromArgb(46, 125, 50);

        private bool IsFieldBrowserOn => Properties.Settings.Default.setDisplay_fieldBrowser;

        /// <summary>Camera look-at used by the main OpenGL view.</summary>
        private void SetMainCameraLookAt()
        {
            if (isFreeView && !isJobStarted)
                camera.SetLookAt(freeE, freeN, freeHeadingDeg);
            else
                camera.SetLookAt(pivotAxlePos.easting, pivotAxlePos.northing, camHeading);
        }

        /// <summary>Heading to use for 3D text, so it reads upright on screen.</summary>
        private double ViewHeadingDeg => (isFreeView && !isJobStarted) ? freeHeadingDeg : camHeading;

        #region Menu and buttons

        private void AddFieldBrowserMenu()
        {
            var item = new ToolStripMenuItem
            {
                Text = T("Fields on map (no field open)", "Polja na karti (bez otvorenog polja)"),
                Font = menustripLanguage.Font,
                Size = menustripLanguage.Size,
                CheckOnClick = true,
                Checked = Properties.Settings.Default.setDisplay_fieldBrowser
            };
            item.Click += (s, e) =>
            {
                Properties.Settings.Default.setDisplay_fieldBrowser = item.Checked;
                Properties.Settings.Default.Save();
                if (!item.Checked) SetFreeView(false);
                savedFields = null;
            };
            int i = fileToolStripMenuItem.DropDownItems.IndexOf(menustripLanguage);
            fileToolStripMenuItem.DropDownItems.Insert(i + 1, item);

            CreateFieldBrowserButtons();

            oglMain.MouseDown += FieldBrowser_MouseDown;
            oglMain.MouseMove += FieldBrowser_MouseMove;
            oglMain.MouseUp += FieldBrowser_MouseUp;
        }

        private void CreateFieldBrowserButtons()
        {
            panelFieldBrowser = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(20, 26, 30),
                Padding = new Padding(4),
                Visible = false,
                Tag = "modern-themed"
            };

            btnFreeView = MakeFbButton("✥ " + T("Move map", "Pomicanje"), 170, "Segoe UI");
            btnFreeView.Click += (s, e) => SetFreeView(!isFreeView);

            Button left = MakeFbButton("↺", 60, "Segoe UI Symbol");
            left.Click += (s, e) => RotateFreeView(-15);
            Button right = MakeFbButton("↻", 60, "Segoe UI Symbol");
            right.Click += (s, e) => RotateFreeView(15);
            Button north = MakeFbButton("N", 60, "Segoe UI");
            north.Click += (s, e) =>
            {
                if (!isFreeView) SetFreeView(true);
                freeHeadingDeg = 0;
                oglMain.Invalidate();
            };
            Button home = MakeFbButton("⌖ " + T("Vehicle", "Vozilo"), 130, "Segoe UI");
            home.Click += (s, e) => SetFreeView(false);

            panelFieldBrowser.Controls.AddRange(new Control[] { btnFreeView, left, right, north, home });
            Controls.Add(panelFieldBrowser);
            panelFieldBrowser.BringToFront();
        }

        private Button MakeFbButton(string text, int width, string fontName)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width, 52),
                Margin = new Padding(3),
                FlatStyle = FlatStyle.Flat,
                BackColor = FbButtonColor,
                ForeColor = Color.White,
                Font = new System.Drawing.Font(fontName, 14F, FontStyle.Bold),
                Tag = "modern-themed",
                TabStop = false
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(84, 110, 122);
            return b;
        }

        private void RotateFreeView(double deg)
        {
            if (!isFreeView) SetFreeView(true);
            freeHeadingDeg = (freeHeadingDeg + deg) % 360.0;
            if (freeHeadingDeg < 0) freeHeadingDeg += 360.0;
            oglMain.Invalidate();
        }

        private void SetFreeView(bool on)
        {
            if (on == isFreeView) return;
            if (on)
            {
                freeE = pivotAxlePos.easting;
                freeN = pivotAxlePos.northing;
                freeHeadingDeg = camera.FollowDirectionHint ? camHeading : 0;
                freeViewSavedFollow = camera.FollowDirectionHint;
                camera.FollowDirectionHint = true;
            }
            else
            {
                camera.FollowDirectionHint = freeViewSavedFollow;
            }
            isFreeView = on;
            if (btnFreeView != null) btnFreeView.BackColor = on ? FbButtonActive : FbButtonColor;
            oglMain.Invalidate();
        }

        private void UpdateFieldBrowserPanel(bool show)
        {
            if (panelFieldBrowser == null) return;
            if (panelFieldBrowser.Visible != show) panelFieldBrowser.Visible = show;
            if (!show) return;

            int x = oglMain.Left + (oglMain.Width - panelFieldBrowser.Width) / 2;
            int y = oglMain.Top + 8;
            if (panelFieldBrowser.Left != x || panelFieldBrowser.Top != y) panelFieldBrowser.Location = new Point(x, y);
            if (Controls.GetChildIndex(panelFieldBrowser) != 0) panelFieldBrowser.BringToFront();
        }

        #endregion

        #region Drawing

        /// <summary>
        /// Called in the 3D part of the main paint, right after the world grid.
        /// Draws only while no field is open.
        /// </summary>
        private void DrawFieldBrowser()
        {
            try
            {
                DrawFieldBrowserCore();
            }
            catch (Exception ex)
            {
                // never break the main view because of the field map
                Log.EventWriter("Field browser: draw failed " + ex.Message);
                FreeAllSatChunks();
                GL.Disable(EnableCap.Texture2D);
                GL.LineWidth(1);
            }
        }

        private void DrawFieldBrowserCore()
        {
            GL.GetDouble(GetPName.ModelviewMatrix, fbModel);
            GL.GetDouble(GetPName.ProjectionMatrix, fbProj);
            fbMatricesValid = true;

            if (isJobStarted || !IsFieldBrowserOn)
            {
                if (isFreeView) SetFreeView(false);
                UpdateFieldBrowserPanel(false);
                fieldBrowserWasJobStarted = isJobStarted;
                return;
            }

            // reload the field list each time a field was closed (new or changed fields)
            if (fieldBrowserWasJobStarted)
            {
                fieldBrowserWasJobStarted = false;
                savedFields = null;
            }

            UpdateFieldBrowserPanel(true);

            LocalPlane plane = AppModel.LocalPlane;
            if (plane == null) return;

            DrawBrowserSatellite(plane);

            if (savedFields == null)
            {
                LoadSavedFieldsAsync();
                return;
            }
            if (!ReferenceEquals(savedFieldsPlane, plane)) ConvertSavedFields(plane);

            double heading = ViewHeadingDeg;
            foreach (SavedFieldOutline f in savedFields)
            {
                foreach (List<vec2> ring in f.Rings)
                {
                    // dark outline under a bright one, readable on any satellite image
                    GL.LineWidth(6);
                    GL.Color3(0.05f, 0.05f, 0.05f);
                    GL.Begin(PrimitiveType.LineLoop);
                    foreach (vec2 p in ring) GL.Vertex3(p.easting, p.northing, 0);
                    GL.End();

                    GL.LineWidth(3);
                    GL.Color3(1.0f, 0.84f, 0.2f);
                    GL.Begin(PrimitiveType.LineLoop);
                    foreach (vec2 p in ring) GL.Vertex3(p.easting, p.northing, 0);
                    GL.End();
                }
            }

            DrawSavedFieldLabels(heading);
            GL.LineWidth(1);
        }


        #endregion

        /// <summary>
        /// Field names, small, only where the field is big enough on screen and the
        /// name does not overlap another one (bigger fields first).
        /// </summary>
        private void DrawSavedFieldLabels(double heading)
        {
            int w = oglMain.Width, h = oglMain.Height;
            var order = new List<SavedFieldOutline>(savedFields);
            order.Sort((a, b) => ((b.MaxE - b.MinE) * (b.MaxN - b.MinN)).CompareTo((a.MaxE - a.MinE) * (a.MaxN - a.MinN)));

            const double charPx = 9.0, linePx = 22.0;
            var shown = new List<RectangleF>();
            GL.Color3(1.0f, 1.0f, 1.0f);
            foreach (SavedFieldOutline f in order)
            {
                if (f.Rings.Count == 0) continue;
                if (!ProjectToScreen(f.MinE, f.MinN, out double x0, out double y0)
                    || !ProjectToScreen(f.MaxE, f.MaxN, out double x1, out double y1)
                    || !ProjectToScreen((f.MinE + f.MaxE) / 2, (f.MinN + f.MaxN) / 2, out double cx, out double cy))
                    continue;
                if (cx < -50 || cx > w + 50 || cy < -20 || cy > h + 20) continue;

                // too small on screen: outline only
                double diag = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (diag < 45) continue;

                double tw = f.Name.Length * charPx + 8;
                var r = new RectangleF((float)(cx - tw / 2), (float)(cy - linePx / 2), (float)tw, (float)linePx);
                bool free = true;
                foreach (RectangleF o in shown)
                    if (o.IntersectsWith(r)) { free = false; break; }
                if (!free) continue;
                shown.Add(r);

                font.DrawText3D((f.MinE + f.MaxE) / 2, (f.MinN + f.MaxN) / 2, f.Name, heading, 0.75, true);
            }
        }

        // World point (z = 0) to window pixels, using the matrices of this frame
        private bool ProjectToScreen(double e, double n, out double sx, out double sy)
        {
            sx = sy = 0;
            double cx = 0, cy = 0, cw = 0;
            for (int k = 0; k < 4; k++)
            {
                // eye = model * (e, n, 0, 1)
                double eye = fbModel[k] * e + fbModel[4 + k] * n + fbModel[12 + k];
                cx += fbProj[k * 4 + 0] * eye;
                cy += fbProj[k * 4 + 1] * eye;
                cw += fbProj[k * 4 + 3] * eye;
            }
            if (cw <= 1e-9) return false;
            sx = (cx / cw + 1) / 2 * oglMain.Width;
            sy = (1 - cy / cw) / 2 * oglMain.Height;
            return true;
        }

        #region Saved fields

        private void LoadSavedFieldsAsync()
        {
            if (savedFieldsLoading) return;
            savedFieldsLoading = true;
            string root = RegistrySettings.fieldsDirectory;

            Task.Run(() => ReadSavedFields(root))
                .ContinueWith(task =>
                {
                    savedFieldsLoading = false;
                    savedFields = task.IsFaulted ? new List<SavedFieldOutline>() : task.Result;
                    savedFieldsPlane = null;
                    oglMain.Invalidate();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Runs on a background thread
        private static List<SavedFieldOutline> ReadSavedFields(string root)
        {
            var list = new List<SavedFieldOutline>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;

            foreach (string dir in Directory.GetDirectories(root))
            {
                try
                {
                    string fieldFile = Path.Combine(dir, "Field.txt");
                    if (!File.Exists(fieldFile) || !File.Exists(Path.Combine(dir, "Boundary.txt"))) continue;

                    Wgs84 origin = FieldPlaneFiles.LoadOrigin(dir);
                    List<CBoundaryList> bnds = BoundaryFiles.Load(dir);
                    if (bnds.Count == 0) continue;

                    var plane = new LocalPlane(origin, new SharedFieldProperties());
                    var f = new SavedFieldOutline { Name = Path.GetFileName(dir), FieldFile = fieldFile };
                    foreach (CBoundaryList b in bnds)
                    {
                        if (b.fenceLine.Count < 3) continue;
                        var ring = new List<Wgs84>(b.fenceLine.Count);
                        foreach (vec3 p in b.fenceLine)
                            ring.Add(plane.ConvertGeoCoordToWgs84(new GeoCoord(p.northing, p.easting)));
                        f.RingsWgs.Add(ring);
                    }
                    if (f.RingsWgs.Count > 0) list.Add(f);
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Field browser: skipped " + dir + " " + ex.Message);
                }
            }
            return list;
        }

        private void ConvertSavedFields(LocalPlane plane)
        {
            foreach (SavedFieldOutline f in savedFields)
            {
                f.Rings.Clear();
                f.MinE = f.MinN = double.MaxValue;
                f.MaxE = f.MaxN = double.MinValue;
                foreach (List<Wgs84> ringWgs in f.RingsWgs)
                {
                    var ring = new List<vec2>(ringWgs.Count);
                    foreach (Wgs84 w in ringWgs)
                    {
                        GeoCoord g = plane.ConvertWgs84ToGeoCoord(w);
                        ring.Add(new vec2(g.Easting, g.Northing));
                        // label and quick hit test use the outer boundary only
                        if (f.Rings.Count == 0)
                        {
                            f.MinE = Math.Min(f.MinE, g.Easting); f.MaxE = Math.Max(f.MaxE, g.Easting);
                            f.MinN = Math.Min(f.MinN, g.Northing); f.MaxN = Math.Max(f.MaxN, g.Northing);
                        }
                    }
                    f.Rings.Add(ring);
                }
            }
            savedFieldsPlane = plane;
        }

        private SavedFieldOutline FieldAt(double e, double n)
        {
            if (savedFields == null || !ReferenceEquals(savedFieldsPlane, AppModel.LocalPlane)) return null;
            SavedFieldOutline best = null;
            double bestArea = double.MaxValue;
            foreach (SavedFieldOutline f in savedFields)
            {
                if (f.Rings.Count == 0 || e < f.MinE || e > f.MaxE || n < f.MinN || n > f.MaxN) continue;
                if (!InsideRing(f.Rings[0], e, n)) continue;
                // overlapping fields: take the smaller one
                double area = (f.MaxE - f.MinE) * (f.MaxN - f.MinN);
                if (area < bestArea) { best = f; bestArea = area; }
            }
            return best;
        }

        private static bool InsideRing(List<vec2> ring, double e, double n)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                if ((ring[i].northing > n) != (ring[j].northing > n)
                    && e < (ring[j].easting - ring[i].easting) * (n - ring[i].northing)
                        / (ring[j].northing - ring[i].northing) + ring[i].easting)
                    inside = !inside;
            }
            return inside;
        }

        #endregion

        #region Mouse

        private bool FieldBrowserActive =>
            !isJobStarted && IsFieldBrowserOn && fbMatricesValid && isGPSPositionInitialized;

        // Screen areas that keep their own meaning (zoom buttons, pan button)
        private bool IsReservedScreenArea(Point p)
        {
            return p.X > oglMain.Width - 80 || (p.X > 30 && p.X < 60 && p.Y > 50 && p.Y < 80);
        }

        /// <summary>
        /// True when the main mouse handler must ignore this click
        /// (free view drag, or a click on a saved field).
        /// </summary>
        private bool FieldBrowserCapturesClick(Point p)
        {
            if (!FieldBrowserActive || IsReservedScreenArea(p)) return false;
            if (isFreeView) return true;
            return ScreenToGround(InverseViewProjection(), p, out double e, out double n) && FieldAt(e, n) != null;
        }

        private void FieldBrowser_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !FieldBrowserActive || IsReservedScreenArea(e.Location)) return;
            fbMouseDown = true;
            fbDragging = false;
            fbDownPoint = e.Location;
            fbDownInverse = InverseViewProjection();
            fbDownE = freeE;
            fbDownN = freeN;
        }

        private void FieldBrowser_MouseMove(object sender, MouseEventArgs e)
        {
            if (!fbMouseDown || !isFreeView || fbDownInverse == null) return;
            int dx = e.X - fbDownPoint.X, dy = e.Y - fbDownPoint.Y;
            if (!fbDragging && dx * dx + dy * dy < 100) return;
            fbDragging = true;

            // both points with the matrices of the mouse down, so the grabbed spot follows the finger
            if (ScreenToGround(fbDownInverse, fbDownPoint, out double e0, out double n0)
                && ScreenToGround(fbDownInverse, e.Location, out double e1, out double n1))
            {
                freeE = fbDownE - (e1 - e0);
                freeN = fbDownN - (n1 - n0);
                oglMain.Invalidate();
            }
        }

        private void FieldBrowser_MouseUp(object sender, MouseEventArgs e)
        {
            if (!fbMouseDown) return;
            fbMouseDown = false;
            if (fbDragging || !FieldBrowserActive) return;

            if (!ScreenToGround(InverseViewProjection(), e.Location, out double ge, out double gn)) return;
            SavedFieldOutline f = FieldAt(ge, gn);
            if (f == null) return;

            Log.EventWriter("Field browser: open " + f.Name);
            SetFreeView(false);
            BeginInvoke((Action)(async () =>
            {
                try
                {
                    await FileOpenField(f.FieldFile);
                }
                catch (Exception ex)
                {
                    Log.EventWriter("Field browser: open failed " + ex.Message);
                }
            }));
        }

        // Inverse of (projection * modelview) of the last drawn frame, column-major
        private double[] InverseViewProjection()
        {
            var m = new double[16];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += fbProj[k * 4 + r] * fbModel[c * 4 + k];
                    m[c * 4 + r] = s;
                }
            return Invert4(m);
        }

        // Intersects the view ray through a screen point with the ground (z = 0)
        private bool ScreenToGround(double[] inv, Point p, out double e, out double n)
        {
            e = n = 0;
            if (inv == null || oglMain.Width <= 0 || oglMain.Height <= 0) return false;
            double x = 2.0 * p.X / oglMain.Width - 1.0;
            double y = 1.0 - 2.0 * p.Y / oglMain.Height;

            double[] a = Transform(inv, x, y, -1);
            double[] b = Transform(inv, x, y, 1);
            if (a == null || b == null || Math.Abs(a[2] - b[2]) < 1e-9) return false;
            double t = a[2] / (a[2] - b[2]);
            if (t < 0) return false;
            e = a[0] + t * (b[0] - a[0]);
            n = a[1] + t * (b[1] - a[1]);
            return true;
        }

        private static double[] Transform(double[] m, double x, double y, double z)
        {
            double ox = m[0] * x + m[4] * y + m[8] * z + m[12];
            double oy = m[1] * x + m[5] * y + m[9] * z + m[13];
            double oz = m[2] * x + m[6] * y + m[10] * z + m[14];
            double ow = m[3] * x + m[7] * y + m[11] * z + m[15];
            if (Math.Abs(ow) < 1e-12) return null;
            return new[] { ox / ow, oy / ow, oz / ow };
        }

        // General 4x4 inverse (cofactor expansion)
        private static double[] Invert4(double[] m)
        {
            var inv = new double[16];
            inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
            inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
            inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
            inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
            inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
            inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
            inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
            inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
            inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
            inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
            inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
            inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
            inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
            inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
            inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
            inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];

            double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
            if (Math.Abs(det) < 1e-18) return null;
            det = 1.0 / det;
            for (int i = 0; i < 16; i++) inv[i] *= det;
            return inv;
        }

        #endregion
    }
}
