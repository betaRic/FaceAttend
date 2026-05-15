using System;
using System.Collections.Generic;
using FaceAttend.Services.Recognition;

namespace FaceAttend.Services.Biometrics
{
    public sealed class BiometricAnalysisResponse
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Mode { get; set; }
        public string ModelVersion { get; set; }
        public string DetectorVersion { get; set; }
        public string RecognizerVersion { get; set; }
        public string AntiSpoofVersion { get; set; }
        public int FaceCount { get; set; }
        public BiometricFaceBox SelectedFaceBox { get; set; }
        public IEnumerable<BiometricLandmarkPoint> Landmarks { get; set; }
        public double[] Embedding { get; set; }
        public IEnumerable<BiometricRecognitionCandidate> RecognitionCandidates { get; set; }
        public BiometricAntiSpoofResult AntiSpoof { get; set; }
        public BiometricQualityResult Quality { get; set; }
        public RecognitionDecisionDto Decision { get; set; }
    }

    public sealed class BiometricLandmarkPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    public sealed class BiometricRecognitionCandidate
    {
        public string EmployeeId { get; set; }
        public double Distance { get; set; }
        public double Confidence { get; set; }
        public string Tier { get; set; }
    }

    public sealed class BiometricAntiSpoofResult
    {
        public float Score { get; set; }
        public float ClearThreshold { get; set; }
        public float ReviewThreshold { get; set; }
        public float BlockThreshold { get; set; }
        public string Decision { get; set; }
        public string Policy { get; set; }
        public bool ModelOk { get; set; }
    }

    public sealed class BiometricQualityResult
    {
        public float Sharpness { get; set; }
        public float SharpnessThreshold { get; set; }
        public double? FaceAreaRatio { get; set; }
        public double Score { get; set; }
    }

    public sealed class BiometricFaceBox
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class BiometricEngineStatus
    {
        public bool Enabled { get; set; }
        public bool Healthy { get; set; }
        public bool Ready { get; set; }
        public bool AnalyzeSupported { get; set; }
        public string Runtime { get; set; }
        public string Status { get; set; }
        public string ErrorCode { get; set; }
        public string ModelsDir { get; set; }
        public string ModelVersion { get; set; }
        public long DurationMs { get; set; }
        public DateTime CheckedUtc { get; set; }
        public IList<BiometricModelStatus> Models { get; set; } = new List<BiometricModelStatus>();
    }

    public sealed class BiometricModelStatus
    {
        public string Slot { get; set; }
        public string ModelName { get; set; }
        public string Path { get; set; }
        public bool Exists { get; set; }
        public bool ExtensionOk { get; set; }
        public bool Loaded { get; set; }
        public string Error { get; set; }
        public long LoadMs { get; set; }
        public IList<string> Inputs { get; set; } = new List<string>();
        public IList<string> Outputs { get; set; } = new List<string>();
    }

    public sealed class BiometricEngineBenchmark
    {
        public bool Ok { get; set; }
        public string Runtime { get; set; }
        public string Status { get; set; }
        public long TotalMs { get; set; }
        public long ManagedMemoryBeforeMb { get; set; }
        public long ManagedMemoryAfterMb { get; set; }
        public BiometricEngineStatus Engine { get; set; }
        public string NextRequiredStep { get; set; }
    }
}
