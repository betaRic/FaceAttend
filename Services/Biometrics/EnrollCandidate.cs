namespace FaceAttend.Services.Biometrics
{
    public class EnrollCandidate
    {
        public double[] Vec { get; set; }
        public float Liveness { get; set; }
        public int Area { get; set; }
        public float Sharpness { get; set; }
        public float PoseYaw { get; set; }
        public float PosePitch { get; set; }
        public float QualityScore { get; set; }
    }
}
