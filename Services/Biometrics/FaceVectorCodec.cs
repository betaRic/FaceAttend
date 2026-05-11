using System;

namespace FaceAttend.Services.Biometrics
{
    public static class FaceVectorCodec
    {
        public static bool IsValidVector(double[] vector)
        {
            var expectedDim = BiometricPolicy.Current.EmbeddingDim;
            return vector != null && vector.Length == expectedDim;
        }

        public static double Distance(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return double.PositiveInfinity;

            double sum = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        public static byte[] EncodeToBytes(double[] vector)
        {
            if (!IsValidVector(vector))
                return null;

            var bytes = new byte[vector.Length * sizeof(double)];
            for (var i = 0; i < vector.Length; i++)
            {
                var item = BitConverter.GetBytes(vector[i]);
                Buffer.BlockCopy(item, 0, bytes, i * sizeof(double), sizeof(double));
            }
            return bytes;
        }

        public static double[] DecodeFromBytes(byte[] bytes)
        {
            var expectedDim = BiometricPolicy.Current.EmbeddingDim;
            var expectedBytes = expectedDim * sizeof(double);
            if (bytes == null || bytes.Length != expectedBytes)
                return null;

            var vector = new double[expectedDim];
            for (var i = 0; i < expectedDim; i++)
                vector[i] = BitConverter.ToDouble(bytes, i * sizeof(double));
            return vector;
        }
    }
}
