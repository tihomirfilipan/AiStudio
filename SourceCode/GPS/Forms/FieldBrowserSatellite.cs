using AgLibrary.Logging;
using AgOpenGPS.Core.Drawing;
using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace AgOpenGPS
{
    /// <summary>
    /// Satellite background for the field map (no field open), stitched from many pieces.
    /// Each piece is a block of 4 x 4 web map tiles. Close up, detailed pieces are used,
    /// zoomed out, coarser and bigger ones. Pieces load in the background, nearest first,
    /// and the oldest unused ones are freed.
    /// </summary>
    public partial class FormGPS
    {
        private class SatChunk
        {
            public int Zoom;
            public long X, Y;
            public Bitmap Bitmap;          // until it is uploaded to the GPU
            public Texture2D Texture;
            public Wgs84[] CornersWgs;     // NW, NE, SE, SW
            public LocalPlane Plane;
            public double[] CornersE = new double[4], CornersN = new double[4];
            public bool Loading;
            public DateTime RetryAt = DateTime.MinValue;
            public DateTime LastUsed;
        }

        private const int SatChunkTiles = 4;
        private const int SatMaxChunks = 90;
        private const int SatMaxParallel = 2;
        private static readonly int[] SatLevels = { 13, 15, 17 };

        private readonly Dictionary<string, SatChunk> satChunks = new Dictionary<string, SatChunk>();
        private int satLoadingCount;

        private void DrawBrowserSatellite(LocalPlane plane)
        {
            if (!Properties.Settings.Default.setMap_isAutoSatellite) return;

            GMapProvider provider = GMapProviders.BingSatelliteMap;
            var projection = provider.Projection;
            long tile = projection.TileSize.Width;
            long chunkPx = tile * SatChunkTiles;

            double cE = isFreeView ? freeE : pivotAxlePos.easting;
            double cN = isFreeView ? freeN : pivotAxlePos.northing;
            double d = camera.DistanceToLookAt;

            int level = d < 900 ? 17 : (d < 4000 ? 15 : 13);
            double metersPerPx = 156543.03 * Math.Cos(plane.Origin.Latitude * Math.PI / 180.0) / Math.Pow(2, level);
            double chunkMeters = chunkPx * metersPerPx;
            double range = Math.Min(d * 2.0 + 400.0, chunkMeters * 3.0);

            // pieces needed around the view center at the current level
            Wgs84 nw = plane.ConvertGeoCoordToWgs84(new GeoCoord(cN + range, cE - range));
            Wgs84 se = plane.ConvertGeoCoordToWgs84(new GeoCoord(cN - range, cE + range));
            GPoint pNW = projection.FromLatLngToPixel(nw.Latitude, nw.Longitude, level);
            GPoint pSE = projection.FromLatLngToPixel(se.Latitude, se.Longitude, level);

            DateTime now = DateTime.Now;
            var missing = new List<SatChunk>();
            for (long y = pNW.Y / chunkPx; y <= pSE.Y / chunkPx; y++)
            {
                for (long x = pNW.X / chunkPx; x <= pSE.X / chunkPx; x++)
                {
                    string key = level + "/" + x + "/" + y;
                    if (!satChunks.TryGetValue(key, out SatChunk c))
                    {
                        c = new SatChunk { Zoom = level, X = x, Y = y };
                        satChunks[key] = c;
                    }
                    c.LastUsed = now;
                    if (c.Texture == null && c.Bitmap == null && !c.Loading && now >= c.RetryAt) missing.Add(c);
                }
            }

            // draw coarse levels first, finer ones on top
            GLW.SetColor(Colors.BingMapBackgroundColor);
            GL.Enable(EnableCap.Texture2D);
            for (int li = 0; li < SatLevels.Length; li++)
            {
                int z = SatLevels[li];
                if (z > level) break;
                double zCoord = -0.09 + 0.02 * li;
                foreach (SatChunk c in satChunks.Values)
                {
                    if (c.Zoom != z || (c.Texture == null && c.Bitmap == null)) continue;
                    if (!ReferenceEquals(c.Plane, plane)) UpdateChunkCorners(c, plane);
                    if (!ChunkNear(c, cE, cN, range + chunkMeters)) continue;

                    if (c.Texture == null)
                    {
                        // upload now (GL context is current), then the bitmap is no longer needed
                        c.Texture = new Texture2D(c.Bitmap);
                        c.Texture.Bind();
                        c.Bitmap.Dispose();
                        c.Bitmap = null;
                    }
                    c.LastUsed = now;
                    c.Texture.Bind();
                    GL.Begin(PrimitiveType.Quads);
                    GL.TexCoord2(0, 0); GL.Vertex3(c.CornersE[0], c.CornersN[0], zCoord);
                    GL.TexCoord2(1, 0); GL.Vertex3(c.CornersE[1], c.CornersN[1], zCoord);
                    GL.TexCoord2(1, 1); GL.Vertex3(c.CornersE[2], c.CornersN[2], zCoord);
                    GL.TexCoord2(0, 1); GL.Vertex3(c.CornersE[3], c.CornersN[3], zCoord);
                    GL.End();
                }
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Disable(EnableCap.Texture2D);

            // start downloads, nearest pieces first
            if (missing.Count > 0 && satLoadingCount < SatMaxParallel)
            {
                missing.Sort((a, b) => ChunkDist(a, pNW, pSE, chunkPx).CompareTo(ChunkDist(b, pNW, pSE, chunkPx)));
                foreach (SatChunk c in missing)
                {
                    if (satLoadingCount >= SatMaxParallel) break;
                    StartChunkDownload(c, provider);
                }
            }

            FreeOldChunks(now);
        }

        private static double ChunkDist(SatChunk c, GPoint pNW, GPoint pSE, long chunkPx)
        {
            double mx = (pNW.X + pSE.X) / 2.0 / chunkPx - 0.5, my = (pNW.Y + pSE.Y) / 2.0 / chunkPx - 0.5;
            return (c.X - mx) * (c.X - mx) + (c.Y - my) * (c.Y - my);
        }

        private static bool ChunkNear(SatChunk c, double e, double n, double range)
        {
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                minE = Math.Min(minE, c.CornersE[i]); maxE = Math.Max(maxE, c.CornersE[i]);
                minN = Math.Min(minN, c.CornersN[i]); maxN = Math.Max(maxN, c.CornersN[i]);
            }
            return maxE > e - range && minE < e + range && maxN > n - range && minN < n + range;
        }

        private static void UpdateChunkCorners(SatChunk c, LocalPlane plane)
        {
            for (int i = 0; i < 4; i++)
            {
                GeoCoord g = plane.ConvertWgs84ToGeoCoord(c.CornersWgs[i]);
                c.CornersE[i] = g.Easting;
                c.CornersN[i] = g.Northing;
            }
            c.Plane = plane;
        }

        private void StartChunkDownload(SatChunk c, GMapProvider provider)
        {
            c.Loading = true;
            satLoadingCount++;
            Task.Run(() => DownloadChunk(provider, c.Zoom, c.X, c.Y, out c.CornersWgs))
                .ContinueWith(task =>
                {
                    c.Loading = false;
                    satLoadingCount--;
                    if (task.IsFaulted || task.Result == null)
                    {
                        c.RetryAt = DateTime.Now.AddSeconds(30);
                        if (task.IsFaulted) Log.EventWriter("Field map satellite: " + task.Exception?.GetBaseException().Message);
                        return;
                    }
                    if (!satChunks.ContainsValue(c))
                    {
                        // freed while loading
                        task.Result.Dispose();
                        return;
                    }
                    c.Bitmap = task.Result;
                    c.Plane = null;
                    oglMain.Invalidate();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Runs on a background thread. Returns null if a tile could not be loaded.
        private static Bitmap DownloadChunk(GMapProvider provider, int zoom, long cx, long cy, out Wgs84[] corners)
        {
            GMapImageProxy.Enable();
            var projection = provider.Projection;
            long tile = projection.TileSize.Width;
            int size = (int)(tile * SatChunkTiles);
            long px0 = cx * size, py0 = cy * size;

            corners = new Wgs84[4];
            long[,] px = { { px0, py0 }, { px0 + size, py0 }, { px0 + size, py0 + size }, { px0, py0 + size } };
            for (int i = 0; i < 4; i++)
            {
                PointLatLng ll = projection.FromPixelToLatLng(new GPoint(px[i, 0], px[i, 1]), zoom);
                corners[i] = new Wgs84(ll.Lat, ll.Lng);
            }

            var bitmap = new Bitmap(size, size);
            int okTiles = 0;
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    for (int ty = 0; ty < SatChunkTiles; ty++)
                    {
                        for (int tx = 0; tx < SatChunkTiles; tx++)
                        {
                            PureImage img = GMaps.Instance.GetImageFrom(provider,
                                new GPoint(cx * SatChunkTiles + tx, cy * SatChunkTiles + ty), zoom, out Exception _);
                            using (img)
                            {
                                if (!(img is GMapImage gi) || gi.Img == null)
                                {
                                    // no image for this tile (e.g. not available at this zoom): grey square
                                    g.FillRectangle(Brushes.DimGray, (int)(tx * tile), (int)(ty * tile), (int)tile, (int)tile);
                                    continue;
                                }
                                okTiles++;
                                g.DrawImage(gi.Img, (int)(tx * tile), (int)(ty * tile), (int)tile, (int)tile);
                            }
                        }
                    }
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            if (okTiles == 0)
            {
                // offline or nothing available: try again later
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }

        private void FreeOldChunks(DateTime now)
        {
            // forget pieces that were never loaded and are no longer wanted
            var remove = new List<string>();
            foreach (var kv in satChunks)
            {
                SatChunk c = kv.Value;
                if (!c.Loading && c.Texture == null && c.Bitmap == null && (now - c.LastUsed).TotalSeconds > 5)
                    remove.Add(kv.Key);
            }
            foreach (string k in remove) satChunks.Remove(k);

            if (satChunks.Count <= SatMaxChunks) return;

            var loaded = new List<KeyValuePair<string, SatChunk>>();
            foreach (var kv in satChunks)
                if (!kv.Value.Loading) loaded.Add(kv);
            loaded.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

            for (int i = 0; i < loaded.Count && satChunks.Count > SatMaxChunks; i++)
            {
                SatChunk c = loaded[i].Value;
                if ((now - c.LastUsed).TotalSeconds < 2) break;   // still on screen
                c.Texture?.Dispose();
                c.Bitmap?.Dispose();
                satChunks.Remove(loaded[i].Key);
            }
        }

        private void FreeAllSatChunks()
        {
            foreach (SatChunk c in satChunks.Values)
            {
                c.Texture?.Dispose();
                c.Bitmap?.Dispose();
            }
            satChunks.Clear();
        }
    }
}
