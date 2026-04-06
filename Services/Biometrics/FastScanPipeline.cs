using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using FaceRecognitionDotNet;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    public static class FastScanPipeline
    {
        public class ScanResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public double[] FaceEncoding { get; set; }
            public float LivenessScore { get; set; }
            public bool LivenessOk { get; set; }
            public DlibBiometrics.FaceBox FaceBox { get; set; }
            public float Sharpness { get; set; }
            public int ImageWidth { get; set; }
            public long TimingMs { get; set; }
            public Dictionary<string, long> Timings { get; set; }
        }

        public class EnrollmentScanResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public string Base64Encoding { get; set; }
            public double[] FaceEncoding { get; set; }
            public float[] Landmarks5 { get; set; }
            public float LivenessScore { get; set; }
            public bool LivenessOk { get; set; }
            public float Sharpness { get; set; }
            public float SharpnessThreshold { get; set; }
            public DlibBiometrics.FaceBox FaceBox { get; set; }
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
            public long TimingMs { get; set; }
        }

        private class CoreResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public double[] Encoding { get; set; }
            public float[] Landmarks5 { get; set; }
            public float LivenessScore { get; set; }
            public bool LivenessOk { get; set; }
            public float Sharpness { get; set; }
            public float SharpnessThreshold { get; set; }
            public DlibBiometrics.FaceBox FaceBox { get; set; }
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
        }

        public static EnrollmentScanResult EnrollmentScanInMemory(
            HttpPostedFileBase image,
            DlibBiometrics.FaceBox clientFaceBox = null,
            bool isMobile = false)
        {
            var sw = Stopwatch.StartNew();

            Bitmap bitmap;
            int imageWidth, imageHeight;
            string loadErr;
            if (!LoadBitmap(image.InputStream, out bitmap, out imageWidth, out imageHeight, out loadErr))
                return new EnrollmentScanResult { Ok = false, Error = loadErr, TimingMs = sw.ElapsedMilliseconds };

            using (bitmap)
            {
                var core = RunCore(bitmap, imageWidth, imageHeight, clientFaceBox, needLandmarks: true, isMobile: isMobile);

                if (!core.Ok)
                    return new EnrollmentScanResult { Ok = false, Error = core.Error, TimingMs = sw.ElapsedMilliseconds };

                return new EnrollmentScanResult
                {
                    Ok                 = true,
                    Base64Encoding     = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(core.Encoding)),
                    FaceEncoding       = core.Encoding,
                    Landmarks5         = core.Landmarks5,
                    LivenessScore      = core.LivenessScore,
                    LivenessOk         = core.LivenessOk,
                    Sharpness          = core.Sharpness,
                    SharpnessThreshold = core.SharpnessThreshold,
                    FaceBox            = core.FaceBox,
                    ImageWidth         = core.ImageWidth,
                    ImageHeight        = core.ImageHeight,
                    TimingMs           = sw.ElapsedMilliseconds
                };
            }
        }

        public static ScanResult ScanInMemory(HttpPostedFileBase image, bool includeTimings = false)
        {
            var sw = Stopwatch.StartNew();
            var timings = includeTimings ? new Dictionary<string, long>() : null;

            Bitmap bitmap;
            int imageWidth, imageHeight;
            string loadErr;
            if (!LoadBitmap(image.InputStream, out bitmap, out imageWidth, out imageHeight, out loadErr))
                return new ScanResult { Ok = false, Error = loadErr, Timings = timings, TimingMs = sw.ElapsedMilliseconds };

            using (bitmap)
            {
                var core = RunCore(bitmap, imageWidth, imageHeight, null, needLandmarks: false, isMobile: false);
                if (timings != null) timings["scan"] = sw.ElapsedMilliseconds;

                if (!core.Ok)
                    return new ScanResult { Ok = false, Error = core.Error, Timings = timings, TimingMs = sw.ElapsedMilliseconds };

                var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(false);
                if (core.Sharpness < sharpTh * 0.75f)
                    return new ScanResult { Ok = false, Error = "LOW_QUALITY", Timings = timings, TimingMs = sw.ElapsedMilliseconds };

                return new ScanResult
                {
                    Ok            = true,
                    FaceEncoding  = core.Encoding,
                    LivenessScore = core.LivenessScore,
                    LivenessOk    = core.LivenessOk,
                    FaceBox       = core.FaceBox,
                    Sharpness     = core.Sharpness,
                    ImageWidth    = core.ImageWidth,
                    Timings       = timings,
                    TimingMs      = sw.ElapsedMilliseconds
                };
            }
        }

        private static bool LoadBitmap(Stream stream, out Bitmap bitmap, out int width, out int height, out string error)
        {
            bitmap = null; width = 0; height = 0; error = null;
            try
            {
                stream.Position = 0;
                byte[] raw;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    raw = ms.ToArray();
                }
                using (var ms = new MemoryStream(raw))
                using (var tmp = new Bitmap(ms))
                {
                    width  = tmp.Width;
                    height = tmp.Height;
                    bitmap = tmp.Clone(
                        new Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceError("[Scan] Load failed: " + ex.Message);
                error = "IMAGE_LOAD_FAIL";
                return false;
            }
        }

        private static CoreResult RunCore(
            Bitmap bitmap, int imageWidth, int imageHeight,
            DlibBiometrics.FaceBox clientFaceBox,
            bool needLandmarks, bool isMobile)
        {
            var dlib = new DlibBiometrics();
            DlibBiometrics.FaceBox faceBox, reportedFaceBox;
            Location faceLocation;

            if (clientFaceBox != null && clientFaceBox.Width > 20 && clientFaceBox.Height > 20)
            {
                reportedFaceBox = clientFaceBox;
                var padX = Math.Max(6, (int)(clientFaceBox.Width  * 0.10));
                var padY = Math.Max(6, (int)(clientFaceBox.Height * 0.12));
                var left = Math.Max(0, clientFaceBox.Left - padX);
                var top  = Math.Max(0, clientFaceBox.Top  - padY);
                faceBox = new DlibBiometrics.FaceBox
                {
                    Left   = left,
                    Top    = top,
                    Width  = Math.Min(imageWidth  - left, clientFaceBox.Width  + padX * 2),
                    Height = Math.Min(imageHeight - top,  clientFaceBox.Height + padY * 2)
                };
                faceLocation = new Location(
                    faceBox.Left, faceBox.Top,
                    faceBox.Left + faceBox.Width,
                    faceBox.Top  + faceBox.Height);
            }
            else
            {
                string detectErr;
                if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLocation, out detectErr))
                    return new CoreResult { Ok = false, Error = detectErr ?? "NO_FACE" };
                reportedFaceBox = faceBox;
            }

            var sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bitmap, faceBox);
            var sharpTh   = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);

            byte[] rgbData;
            try   { rgbData = DlibBiometrics.ExtractRgbData(bitmap); }
            catch (Exception ex)
            {
                Trace.TraceError("[FastScanPipeline] ExtractRgbData failed: " + ex.Message);
                return new CoreResult { Ok = false, Error = "BITMAP_CONVERT_FAIL" };
            }

            double[] encoding  = null;
            float[]  landmarks = null;
            string   encErr    = null;
            bool     liveOk    = false;
            float?   liveProb  = null;

            Parallel.Invoke(
                () =>
                {
                    var live = new OnnxLiveness();
                    var r    = live.ScoreFromBitmap(bitmap, faceBox);
                    liveOk   = r.Ok;
                    liveProb = r.Probability;
                },
                () =>
                {
                    if (needLandmarks)
                    {
                        dlib.TryEncodeWithLandmarksFromRgbData(
                            rgbData, imageWidth, imageHeight, faceLocation,
                            out encoding, out landmarks, out encErr);
                    }
                    else
                    {
                        dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLocation, out encoding, out encErr);
                    }
                });

            if (encoding == null)
                return new CoreResult { Ok = false, Error = encErr ?? "ENCODING_FAIL" };

            var liveTh    = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var liveScore = liveProb ?? 0f;

            return new CoreResult
            {
                Ok                 = true,
                Encoding           = encoding,
                Landmarks5         = landmarks,
                LivenessScore      = liveScore,
                LivenessOk         = liveOk && liveScore >= liveTh,
                Sharpness          = sharpness,
                SharpnessThreshold = sharpTh,
                FaceBox            = reportedFaceBox,
                ImageWidth         = imageWidth,
                ImageHeight        = imageHeight
            };
        }
    }
}
