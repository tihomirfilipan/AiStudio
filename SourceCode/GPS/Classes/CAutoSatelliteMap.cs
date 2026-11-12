using AgLibrary.Logging;
using AgOpenGPS.Core.Models;
using AgOpenGPS.Core.Streamers;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace AgOpenGPS
{
    /// <summary>
    /// Automatically downloads a satellite image that covers the outer field boundary
    /// plus a margin around it, and uses it as the field background map (BackPic).
    /// </summary>
    public class CAutoSatelliteMap
    {
        // Extra area around the field boundary, in meters
        public const double MarginMeters = 50.0;

        // Largest bitmap side in pixels (keeps the OpenGL texture and memory use reasonable)
        private const int MaxBitmapSide = 4096;

        private const int MaxZoom = 19;
        private const int MinZoom = 12;

        private readonly FormGPS mf;
        private bool isBusy;

        public CAutoSatelliteMap(FormGPS _f)
        {
            mf = _f;
        }

        /// <summary>
        /// Starts a background download when the active field has an outer boundary and
        /// the current background map does not cover the boundary plus margin.
        /// Safe to call often; it does nothing when no update is needed.
        /// </summary>
        public void UpdateIfNeeded()
        {
            if (!Properties.Settings.Default.setMap_isAutoSatellite) return;
            if (isBusy || !mf.isJobStarted) return;
            if (mf.bnd.bndList.Count == 0 || mf.bnd.bndList[0].fenceLine.Count < 3) return;

            GeoBoundingBox wanted = GetBoundaryBoxWithMargin();
            if (mf.worldGrid.HasBingMap && Covers(mf.worldGrid.BingMap.GeoBoundingBox, wanted)) return;

            LocalPlane localPlane = mf.AppModel.LocalPlane;
            if (localPlane == null) return;

            // Corners of the wanted area in WGS84
            Wgs84 northWest = localPlane.ConvertGeoCoordToWgs84(new GeoCoord(wanted.MaxNorthing, wanted.MinEasting));
            Wgs84 southEast = localPlane.ConvertGeoCoordToWgs84(new GeoCoord(wanted.MinNorthing, wanted.MaxEasting));

            string fieldDir = mf.currentFieldDirectory;
            isBusy = true;
            Log.EventWriter("Auto satellite map: download started for " + fieldDir);

            Task.Run(() => DownloadBitmap(northWest, southEast))
                .ContinueWith(task =>
                {
                    try
                    {
                        if (task.IsFaulted || task.Result == null)
                        {
                            Log.EventWriter("Auto satellite map: download failed "
                                + (task.Exception?.GetBaseException().Message ?? ""));
                            return;
                        }

                        // The field may have been closed or changed while downloading
                        if (!mf.isJobStarted || mf.currentFieldDirectory != fieldDir)
                        {
                            task.Result.Bitmap.Dispose();
                            return;
                        }

                        ApplyAndSave(task.Result, localPlane);
                    }
                    catch (Exception ex)
                    {
                        Log.EventWriter("Auto satellite map: " + ex.Message);
                    }
                    finally
                    {
                        isBusy = false;
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private GeoBoundingBox GetBoundaryBoxWithMargin()
        {
            GeoBoundingBox box = GeoBoundingBox.CreateEmpty();
            foreach (vec3 pt in mf.bnd.bndList[0].fenceLine)
            {
                box.Include(new GeoCoord(pt.northing - MarginMeters, pt.easting - MarginMeters));
                box.Include(new GeoCoord(pt.northing + MarginMeters, pt.easting + MarginMeters));
            }
            return box;
        }

        private static bool Covers(GeoBoundingBox outer, GeoBoundingBox inner)
        {
            // 1 m tolerance for rounding between map pixels and local coordinates
            const double tol = 1.0;
            return outer.MinNorthing <= inner.MinNorthing + tol
                && outer.MaxNorthing >= inner.MaxNorthing - tol
                && outer.MinEasting <= inner.MinEasting + tol
                && outer.MaxEasting >= inner.MaxEasting - tol;
        }

        private void ApplyAndSave(DownloadResult result, LocalPlane localPlane)
        {
            GeoBoundingBox box = GeoBoundingBox.CreateEmpty();
            box.Include(localPlane.ConvertWgs84ToGeoCoord(result.NorthWest));
            box.Include(localPlane.ConvertWgs84ToGeoCoord(result.SouthEast));

            BingMap bingMap = new BingMap(box, result.Bitmap);
            mf.worldGrid.BingMap = bingMap;

            new BingMapStreamer().TryWrite(bingMap, new DirectoryInfo(Path.Combine(RegistrySettings.fieldsDirectory, mf.currentFieldDirectory)));
            Log.EventWriter($"Auto satellite map: saved {result.Bitmap.Width}x{result.Bitmap.Height} px, zoom {result.Zoom}");
        }

        internal class DownloadResult
        {
            public Bitmap Bitmap;
            public Wgs84 NorthWest;
            public Wgs84 SouthEast;
            public int Zoom;
        }

        // Runs on a background thread. Returns null if any tile could not be downloaded.
        internal static DownloadResult DownloadBitmap(Wgs84 northWest, Wgs84 southEast)
        {
            // GMapControl normally sets this up; set it here in case the map form was never opened
            GMapImageProxy.Enable();

            GMapProvider provider = GMapProviders.BingSatelliteMap;
            var projection = provider.Projection;

            // Pick the highest zoom level at which the area still fits in the bitmap size limit
            int zoom = MaxZoom;
            GPoint pxNW, pxSE;
            while (true)
            {
                pxNW = projection.FromLatLngToPixel(northWest.Latitude, northWest.Longitude, zoom);
                pxSE = projection.FromLatLngToPixel(southEast.Latitude, southEast.Longitude, zoom);
                if ((pxSE.X - pxNW.X <= MaxBitmapSide && pxSE.Y - pxNW.Y <= MaxBitmapSide) || zoom <= MinZoom) break;
                zoom--;
            }

            int width = (int)(pxSE.X - pxNW.X);
            int height = (int)(pxSE.Y - pxNW.Y);
            if (width <= 0 || height <= 0 || width > MaxBitmapSide || height > MaxBitmapSide) return null;

            long tileW = projection.TileSize.Width;
            long tileH = projection.TileSize.Height;

            Bitmap bitmap = new Bitmap(width, height);
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    for (long ty = pxNW.Y / tileH; ty <= (pxSE.Y - 1) / tileH; ty++)
                    {
                        for (long tx = pxNW.X / tileW; tx <= (pxSE.X - 1) / tileW; tx++)
                        {
                            PureImage tile = GMaps.Instance.GetImageFrom(provider, new GPoint(tx, ty), zoom, out Exception error);
                            using (tile)
                            {
                                if (!(tile is GMapImage gmapImage) || gmapImage.Img == null)
                                {
                                    Log.EventWriter("Auto satellite map: tile failed " + (error?.Message ?? ""));
                                    bitmap.Dispose();
                                    return null;
                                }
                                g.DrawImage(gmapImage.Img,
                                    (int)(tx * tileW - pxNW.X), (int)(ty * tileH - pxNW.Y),
                                    (int)tileW, (int)tileH);
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

            // Geographic corners of the exact pixel area that was drawn
            PointLatLng nw = projection.FromPixelToLatLng(pxNW, zoom);
            PointLatLng se = projection.FromPixelToLatLng(pxSE, zoom);

            return new DownloadResult
            {
                Bitmap = bitmap,
                NorthWest = new Wgs84(nw.Lat, nw.Lng),
                SouthEast = new Wgs84(se.Lat, se.Lng),
                Zoom = zoom
            };
        }
    }
}
