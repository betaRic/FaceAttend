using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FaceAttend.Models.Dtos;
using FaceAttend.Services.Security;
using Newtonsoft.Json;

namespace FaceAttend.Services.Biometrics
{
    public static class EnrollmentCaptureService
    {
        public class CaptureBatchResult
        {
            public List<EnrollCandidate> Candidates { get; set; }
            public int ProcessedCount { get; set; }
            public int SubmittedCount { get; set; }
        }

        public static List<HttpPostedFileBase> CollectFiles(
            HttpRequestBase request,
            IEnumerable<HttpPostedFileBase> typedFiles)
        {
            var files = new List<HttpPostedFileBase>();

            if (typedFiles != null)
                files.AddRange(typedFiles.Where(f => f != null && f.ContentLength > 0));

            if (request != null && request.Files != null)
            {
                for (int i = 0; i < request.Files.Count; i++)
                {
                    var file = request.Files[i];
                    if (file != null && file.ContentLength > 0 && !files.Contains(file))
                        files.Add(file);
                }
            }

            return files;
        }

        public static CaptureBatchResult ExtractCandidates(
            IEnumerable<HttpPostedFileBase> files,
            bool isMobile,
            int maxBytes,
            int parallelism)
        {
            var fileList = (files ?? Enumerable.Empty<HttpPostedFileBase>())
                .Where(f => f != null && f.ContentLength > 0)
                .ToList();

            var candidates = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;
            var policy = BiometricPolicy.Current;
            var antiSpoofThreshold = policy.AntiSpoofClearThresholdFor(isMobile);
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };

            Parallel.ForEach(
                fileList,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) },
                file =>
                {
                    try
                    {
                        if (file.ContentLength <= 0 || file.ContentLength > maxBytes)
                            return;

                        if (!FileSecurityService.IsValidImage(file.InputStream, allowedExtensions))
                            return;

                        var scan = FastScanPipeline.EnrollmentScanInMemory(
                            file,
                            null,
                            isMobile,
                            antiSpoofThreshold);

                        if (!scan.Ok || scan.FaceEncoding == null || scan.FaceBox == null)
                            return;

                        var antiSpoof = policy.EvaluateAntiSpoof(
                            scan.AntiSpoofModelOk,
                            scan.AntiSpoofScore,
                            isMobile);
                        if (antiSpoof.Decision != AntiSpoofDecision.Pass)
                            return;

                        if (scan.Sharpness < scan.SharpnessThreshold)
                            return;

                        var area = Math.Max(0, scan.FaceBox.Width) * Math.Max(0, scan.FaceBox.Height);
                        var imageArea = Math.Max(1, scan.ImageWidth * scan.ImageHeight);
                        var areaRatio = area / (double)imageArea;
                        var minAreaRatio = isMobile
                            ? policy.MobileEnrollmentMinFaceAreaRatio
                            : policy.EnrollmentMinFaceAreaRatio;
                        if (areaRatio < minAreaRatio)
                            return;

                        float poseYaw;
                        float posePitch;
                        if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                        {
                            var pose = FaceQualityAnalyzer.EstimatePoseFromLandmarks(scan.Landmarks5);
                            poseYaw = pose.yaw;
                            posePitch = pose.pitch;

                            if (isMobile)
                            {
                                poseYaw *= 0.8f;
                                posePitch *= 0.8f;
                            }
                        }
                        else
                        {
                            var pose = FaceQualityAnalyzer.EstimatePose(
                                scan.FaceBox,
                                scan.ImageWidth,
                                scan.ImageHeight);
                            poseYaw = pose.yaw;
                            posePitch = pose.pitch;
                        }

                        var maxYaw = (float)ConfigurationService.GetDouble("Biometrics:Enroll:MaxPoseYaw", 35);
                        var maxPitch = (float)ConfigurationService.GetDouble("Biometrics:Enroll:MaxPosePitch", 40);
                        if (Math.Abs(poseYaw) > maxYaw || Math.Abs(posePitch) > maxPitch)
                            return;

                        var qualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                            scan.AntiSpoofScore,
                            scan.Sharpness,
                            area,
                            poseYaw,
                            posePitch);

                        candidates.Add(new EnrollCandidate
                        {
                            Vec = scan.FaceEncoding,
                            AntiSpoof = scan.AntiSpoofScore,
                            Area = area,
                            Sharpness = scan.Sharpness,
                            PoseYaw = poseYaw,
                            PosePitch = posePitch,
                            QualityScore = qualityScore
                        });

                        Interlocked.Increment(ref processedCount);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("[EnrollmentCapture] Frame rejected: {0}", ex.Message);
                    }
                });

            return new CaptureBatchResult
            {
                Candidates = candidates.ToList(),
                ProcessedCount = processedCount,
                SubmittedCount = fileList.Count
            };
        }

        public static string FindDuplicateEmployeeId(
            FaceAttendDBEntities db,
            IEnumerable<EnrollCandidate> candidates,
            string excludeEmployeeId,
            double strictTolerance)
        {
            if (db == null || candidates == null)
                return null;

            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Vec == null)
                    continue;

                var duplicateId = DuplicateCheckHelper.FindDuplicate(
                    db,
                    candidate.Vec,
                    excludeEmployeeId,
                    strictTolerance);

                if (!string.IsNullOrEmpty(duplicateId))
                    return duplicateId;
            }

            return null;
        }

        public static DuplicateCheckHelper.ClosestFaceResult FindClosestEmployee(
            FaceAttendDBEntities db,
            IEnumerable<EnrollCandidate> candidates,
            string excludeEmployeeId)
        {
            if (db == null || candidates == null)
                return null;

            DuplicateCheckHelper.ClosestFaceResult closest = null;
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Vec == null)
                    continue;

                var current = DuplicateCheckHelper.FindClosest(db, candidate.Vec, excludeEmployeeId);
                if (current != null && (closest == null || current.Distance < closest.Distance))
                    closest = current;
            }

            return closest;
        }

        public static void ApplyStoredVectors(Employee employee, IList<EnrollCandidate> selected)
        {
            if (employee == null)
                throw new ArgumentNullException(nameof(employee));
            if (selected == null || selected.Count == 0)
                throw new ArgumentException("At least one selected candidate is required.", nameof(selected));

            employee.FaceEncodingBase64 = BiometricCrypto.ProtectBase64Bytes(
                FaceVectorCodec.EncodeToBytes(selected[0].Vec));

            employee.FaceEncodingsJson = BiometricCrypto.ProtectString(
                JsonConvert.SerializeObject(
                    selected.Select(c => BiometricCrypto.ProtectBase64Bytes(
                        FaceVectorCodec.EncodeToBytes(c.Vec)))
                    .ToList()));
        }

        public static List<EnrollCandidate> SelectDiverseByEmbedding(
            List<EnrollCandidate> candidates,
            int targetCount)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<EnrollCandidate>();

            if (candidates.Count <= targetCount)
                return candidates.OrderByDescending(c => c.QualityScore).ToList();

            var sorted = candidates.OrderByDescending(c => c.QualityScore).ToList();
            var selected = new List<EnrollCandidate>(targetCount) { sorted[0] };
            var remaining = sorted.Skip(1).ToList();

            while (selected.Count < targetCount && remaining.Count > 0)
            {
                double bestMinDist = -1;
                int bestIdx = 0;

                for (int i = 0; i < remaining.Count; i++)
                {
                    double minDist = double.MaxValue;
                    foreach (var current in selected)
                    {
                        var distance = FaceVectorCodec.Distance(remaining[i].Vec, current.Vec);
                        if (distance < minDist)
                            minDist = distance;
                    }

                    if (minDist > bestMinDist)
                    {
                        bestMinDist = minDist;
                        bestIdx = i;
                    }
                }

                selected.Add(remaining[bestIdx]);
                remaining.RemoveAt(bestIdx);
            }

            return selected.OrderByDescending(c => c.QualityScore).ToList();
        }
    }
}
