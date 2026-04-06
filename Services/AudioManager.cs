namespace FaceAttend.Services
{
    /// <summary>
    /// Virtual paths for the two shared audio files.
    /// The client-side audio behavior is handled by Scripts/audio-manager.js.
    /// </summary>
    public static class AudioManager
    {
        public static string SuccessSoundPath      => "~/Content/audio/success.mp3";
        public static string NotificationSoundPath => "~/Content/audio/notif.mp3";
    }
}
