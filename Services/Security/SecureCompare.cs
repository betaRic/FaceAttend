using System.Text;

namespace FaceAttend.Services.Security
{
    public static class SecureCompare
    {
        public static bool FixedEquals(string left, string right)
        {
            var a = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var b = Encoding.UTF8.GetBytes(right ?? string.Empty);
            return FixedEquals(a, b);
        }

        public static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            var diff = 0;
            for (var i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];

            return diff == 0;
        }
    }
}
