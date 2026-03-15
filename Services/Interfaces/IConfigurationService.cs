namespace FaceAttend.Services.Interfaces
{
    /// <summary>
    /// Interface for configuration/settings service
    /// </summary>
    public interface IConfigurationService
    {
        string GetString(FaceAttendDBEntities db, string key, string defaultValue = null);
        int GetInt(FaceAttendDBEntities db, string key, int defaultValue = 0);
        double GetDouble(FaceAttendDBEntities db, string key, double defaultValue = 0);
        bool GetBool(FaceAttendDBEntities db, string key, bool defaultValue = false);
        void Set(FaceAttendDBEntities db, string key, string value);
    }
}
