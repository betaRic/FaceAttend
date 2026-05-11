using System;
using System.Configuration;
using System.Data.Entity.Core.EntityClient;

namespace FaceAttend.Services.Data
{
    public static class FaceAttendConnectionFactory
    {
        private const string EntityMetadata =
            "res://*/FaceAttendDBEntities.csdl|res://*/FaceAttendDBEntities.ssdl|res://*/FaceAttendDBEntities.msl";

        public static string GetEntityConnectionString()
        {
            var fullEntity = ReadEnv("FACEATTEND_ENTITY_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(fullEntity))
                return fullEntity.Trim();

            var provider = ReadEnv("FACEATTEND_DB_PROVIDER_CONNECTION_STRING")
                ?? ReadEnv("FACEATTEND_DB_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(provider))
            {
                var builder = new EntityConnectionStringBuilder
                {
                    Metadata = EntityMetadata,
                    Provider = "System.Data.SqlClient",
                    ProviderConnectionString = provider.Trim()
                };
                return builder.ToString();
            }

            var configured = ConfigurationManager.ConnectionStrings["FaceAttendDBEntities"];
            return configured != null && !string.IsNullOrWhiteSpace(configured.ConnectionString)
                ? "name=FaceAttendDBEntities"
                : "name=FaceAttendDBEntities";
        }

        private static string ReadEnv(string key)
        {
            return Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);
        }
    }
}
