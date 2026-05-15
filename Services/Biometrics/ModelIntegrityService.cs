using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Hosting;

namespace FaceAttend.Services.Biometrics
{
    public static class ModelIntegrityService
    {
        public sealed class ModelIntegritySnapshot
        {
            public bool Ok { get; set; }
            public bool ExpectedHashesConfigured { get; set; }
            public bool RequireReadOnlyAcl { get; set; }
            public bool AclOk { get; set; }
            public string Error { get; set; }
            public IList<ModelFileHash> Files { get; set; } = new List<ModelFileHash>();
        }

        public sealed class ModelFileHash
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public bool Exists { get; set; }
            public string Sha256 { get; set; }
            public string ExpectedSha256 { get; set; }
            public bool Match { get; set; }
            public string CurrentIdentity { get; set; }
            public bool CurrentIdentityCanWriteFile { get; set; }
            public bool CurrentIdentityCanWriteDirectory { get; set; }
            public bool AclLocked { get; set; }
            public string AclError { get; set; }
        }

        public static ModelIntegritySnapshot Check()
        {
            var snapshot = new ModelIntegritySnapshot
            {
                Ok = true,
                AclOk = true,
                RequireReadOnlyAcl = ConfigurationService.GetBool("Biometrics:RequireModelReadOnlyAcl", false)
            };
            try
            {
                var expected = ParseExpectedHashes(GetConfiguredModelHashes());
                snapshot.ExpectedHashesConfigured = expected.Count > 0;

                var paths = ResolveConfiguredModelPaths(expected.Keys).ToList();
                if (paths.Count == 0 && ConfigurationService.GetBool("Biometrics:Engine:Enabled", true))
                {
                    snapshot.Ok = false;
                    snapshot.Error = "No ONNX model files found in Biometrics:ModelDir/Biometrics:OnnxModelsDir.";
                }

                foreach (var path in paths)
                {
                    var file = new ModelFileHash
                    {
                        Name = System.IO.Path.GetFileName(path),
                        Path = path,
                        Exists = File.Exists(path)
                    };

                    if (!file.Exists)
                    {
                        file.Match = false;
                        snapshot.Ok = false;
                        snapshot.Files.Add(file);
                        continue;
                    }

                    file.Sha256 = ComputeSha256(path);
                    string expectedHash;
                    if (expected.TryGetValue(file.Name, out expectedHash))
                    {
                        file.ExpectedSha256 = expectedHash;
                        file.Match = string.Equals(file.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase);
                        if (!file.Match) snapshot.Ok = false;
                    }
                    else
                    {
                        file.Match = true;
                    }

                    FillAclState(file);
                    if (!file.AclLocked)
                        snapshot.AclOk = false;

                    snapshot.Files.Add(file);
                }

                if (snapshot.RequireReadOnlyAcl && !snapshot.AclOk)
                    snapshot.Ok = false;
            }
            catch (Exception ex)
            {
                snapshot.Ok = false;
                snapshot.AclOk = false;
                snapshot.Error = ex.GetBaseException().Message;
            }

            return snapshot;
        }

        public static IEnumerable<string> ResolveConfiguredModelPaths()
        {
            return ResolveConfiguredModelPaths(Enumerable.Empty<string>());
        }

        public static string BuildCurrentModelHashes()
        {
            var entries = new List<string>();
            foreach (var path in ResolveConfiguredModelPaths().Where(File.Exists).OrderBy(System.IO.Path.GetFileName))
            {
                entries.Add(System.IO.Path.GetFileName(path) + "=" + ComputeSha256(path));
            }

            return string.Join(";", entries);
        }

        private static IEnumerable<string> ResolveConfiguredModelPaths(IEnumerable<string> expectedFileNames)
        {
            var paths = new List<string>();

            var modelDir = MapPath(ConfigurationService.GetString(
                "Biometrics:ModelDir",
                ConfigurationService.GetString("Biometrics:OnnxModelsDir", "~/App_Data/models/onnx")));
            if (!string.IsNullOrWhiteSpace(modelDir) && Directory.Exists(modelDir))
            {
                paths.AddRange(Directory.GetFiles(modelDir, "*.onnx").OrderBy(x => x));

                foreach (var fileName in expectedFileNames ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(fileName))
                        paths.Add(System.IO.Path.Combine(modelDir, fileName.Trim()));
                }
            }

            paths.AddRange(BiometricEngine.GetConfiguredModelPaths());

            return paths.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void FillAclState(ModelFileHash file)
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                file.CurrentIdentity = identity == null ? null : identity.Name;
                file.CurrentIdentityCanWriteFile = CanCurrentIdentityWriteFile(file.Path);

                var dir = System.IO.Path.GetDirectoryName(file.Path);
                file.CurrentIdentityCanWriteDirectory =
                    !string.IsNullOrWhiteSpace(dir) && CanCurrentIdentityWriteDirectory(dir);

                file.AclLocked =
                    !file.CurrentIdentityCanWriteFile &&
                    !file.CurrentIdentityCanWriteDirectory;
            }
            catch (Exception ex)
            {
                file.AclLocked = false;
                file.AclError = ex.GetBaseException().Message;
            }
        }

        private static bool CanCurrentIdentityWriteFile(string path)
        {
            var security = File.GetAccessControl(path);
            return HasWriteAccess(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }

        private static bool CanCurrentIdentityWriteDirectory(string path)
        {
            var security = Directory.GetAccessControl(path);
            return HasWriteAccess(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }

        private static bool HasWriteAccess(AuthorizationRuleCollection rules)
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity == null)
                return false;

            var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (identity.User != null)
                principals.Add(identity.User.Value);
            if (identity.Groups != null)
            {
                foreach (var group in identity.Groups)
                    principals.Add(group.Value);
            }

            var allowed = false;
            var denied = false;

            foreach (FileSystemAccessRule rule in rules)
            {
                if (!principals.Contains(rule.IdentityReference.Value))
                    continue;

                if (!ContainsWriteRight(rule.FileSystemRights))
                    continue;

                if (rule.AccessControlType == AccessControlType.Deny)
                    denied = true;
                else if (rule.AccessControlType == AccessControlType.Allow)
                    allowed = true;
            }

            return allowed && !denied;
        }

        private static bool ContainsWriteRight(FileSystemRights rights)
        {
            const FileSystemRights writeRights =
                FileSystemRights.Write |
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.CreateFiles |
                FileSystemRights.CreateDirectories |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership |
                FileSystemRights.Modify |
                FileSystemRights.FullControl;

            return (rights & writeRights) != 0;
        }

        private static string MapPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            if (path.StartsWith("~/", StringComparison.Ordinal))
                return HostingEnvironment.MapPath(path);
            return path;
        }

        private static Dictionary<string, string> ParseExpectedHashes(string raw)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var item in raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                var name = parts[0].Trim();
                var hash = parts[1].Trim();
                if (name.Length > 0 && hash.Length > 0)
                    result[name] = hash;
            }

            return result;
        }

        private static string GetConfiguredModelHashes()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var value = db.SystemConfigurations
                        .Where(x => x.Key == "Biometrics:ModelHashes")
                        .Select(x => x.Value)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch
            {
            }

            return ConfigurationService.GetString("Biometrics:ModelHashes", "");
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
