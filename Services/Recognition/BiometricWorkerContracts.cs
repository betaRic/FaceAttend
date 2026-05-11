using System.Collections.Generic;

namespace FaceAttend.Services.Recognition
{
    public sealed class WorkerAnalyzeFaceRequest
    {
        public string ImageBase64 { get; set; }
        public string Mode { get; set; }
    }

    public sealed class WorkerAnalyzeFaceResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Mode { get; set; }
        public string ModelVersion { get; set; }
        public string DetectorVersion { get; set; }
        public string RecognizerVersion { get; set; }
        public string AntiSpoofVersion { get; set; }
        public int FaceCount { get; set; }
        public WorkerFaceBox SelectedFaceBox { get; set; }
        public IEnumerable<WorkerLandmarkPoint> Landmarks { get; set; }
        public double[] Embedding { get; set; }
        public IEnumerable<WorkerRecognitionCandidate> RecognitionCandidates { get; set; }
        public WorkerAntiSpoofResult AntiSpoof { get; set; }
        public WorkerQualityResult Quality { get; set; }
        public RecognitionDecisionDto Decision { get; set; }
    }

    public sealed class WorkerLandmarkPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    public sealed class WorkerRecognitionCandidate
    {
        public string EmployeeId { get; set; }
        public double Distance { get; set; }
        public double Confidence { get; set; }
        public string Tier { get; set; }
    }

    public sealed class WorkerAntiSpoofResult
    {
        public float Score { get; set; }
        public float ClearThreshold { get; set; }
        public float ReviewThreshold { get; set; }
        public float BlockThreshold { get; set; }
        public string Decision { get; set; }
        public string Policy { get; set; }
        public bool ModelOk { get; set; }
    }

    public sealed class WorkerQualityResult
    {
        public float Sharpness { get; set; }
        public float SharpnessThreshold { get; set; }
        public double? FaceAreaRatio { get; set; }
        public double Score { get; set; }
    }

    public sealed class WorkerFaceBox
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
