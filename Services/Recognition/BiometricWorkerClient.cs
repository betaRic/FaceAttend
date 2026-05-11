using System;
using System.IO;
using System.Net;
using System.Text;
using FaceAttend.Services.Biometrics;
using Newtonsoft.Json;

namespace FaceAttend.Services.Recognition
{
    public static class BiometricWorkerClient
    {
        public sealed class WorkerHealth
        {
            public bool Enabled { get; set; }
            public bool Healthy { get; set; }
            public string Status { get; set; }
            public string BaseUrl { get; set; }
            public long DurationMs { get; set; }
        }

        public static WorkerHealth CheckHealth()
        {
            var enabled = ConfigurationService.GetBool("Biometrics:Worker:Enabled", false);
            var baseUrl = ConfigurationService.GetString("Biometrics:Worker:BaseUrl", "http://127.0.0.1:5077");
            if (!enabled)
            {
                return new WorkerHealth
                {
                    Enabled = false,
                    Healthy = true,
                    Status = "disabled",
                    BaseUrl = baseUrl
                };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Exception lastError = null;
            foreach (var path in new[] { "/health", "/Health" })
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + path);
                    request.Method = "GET";
                    request.Timeout = ConfigurationService.GetInt("Biometrics:Worker:HealthTimeoutMs", 750);
                    request.ReadWriteTimeout = request.Timeout;

                    var secret = ConfigurationService.GetString("Biometrics:Worker:SharedSecret", "");
                    if (!string.IsNullOrWhiteSpace(secret))
                        request.Headers["X-FaceAttend-Worker-Secret"] = secret;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var body = new StreamReader(response.GetResponseStream()))
                    {
                        var text = body.ReadToEnd();
                        return new WorkerHealth
                        {
                            Enabled = true,
                            Healthy = response.StatusCode == HttpStatusCode.OK,
                            Status = string.IsNullOrWhiteSpace(text) ? response.StatusCode.ToString() : text,
                            BaseUrl = baseUrl,
                            DurationMs = sw.ElapsedMilliseconds
                        };
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            var message = lastError == null ? "Worker health check failed" : lastError.GetBaseException().Message;
            return new WorkerHealth
            {
                Enabled = true,
                Healthy = false,
                Status = message,
                BaseUrl = baseUrl,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        public static WorkerAnalyzeFaceResponse AnalyzeFace(
            byte[] imageBytes,
            BiometricScanMode mode,
            OpenVinoBiometrics.FaceBox faceBoxHint = null)
        {
            var enabled = ConfigurationService.GetBool("Biometrics:Worker:Enabled", false);
            if (!enabled)
            {
                return new WorkerAnalyzeFaceResponse
                {
                    Ok = false,
                    Error = "OPENVINO_WORKER_DISABLED",
                    Mode = mode.ToString().ToUpperInvariant()
                };
            }

            var baseUrl = ConfigurationService.GetString("Biometrics:Worker:BaseUrl", "http://127.0.0.1:5077");
            var timeoutMs = ConfigurationService.GetInt("Biometrics:Worker:AnalyzeTimeoutMs", 5000);
            var requestBody = new WorkerAnalyzeFaceRequest
            {
                ImageBase64 = Convert.ToBase64String(imageBytes ?? Array.Empty<byte>()),
                Mode = mode.ToString().ToUpperInvariant(),
                FaceBoxHint = faceBoxHint == null ? null : new FaceBoxHint
                {
                    X = faceBoxHint.Left,
                    Y = faceBoxHint.Top,
                    Width = faceBoxHint.Width,
                    Height = faceBoxHint.Height
                }
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var payload = Encoding.UTF8.GetBytes(json);

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/analyze-face");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;

                var secret = ConfigurationService.GetString("Biometrics:Worker:SharedSecret", "");
                if (!string.IsNullOrWhiteSpace(secret))
                    request.Headers["X-FaceAttend-Worker-Secret"] = secret;

                using (var stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var body = new StreamReader(response.GetResponseStream()))
                {
                    var text = body.ReadToEnd();
                    var parsed = JsonConvert.DeserializeObject<WorkerAnalyzeFaceResponse>(text);
                    if (parsed != null)
                        return parsed;

                    return new WorkerAnalyzeFaceResponse { Ok = false, Error = "OPENVINO_WORKER_BAD_JSON" };
                }
            }
            catch (WebException ex)
            {
                var responseText = ReadErrorBody(ex);
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    try
                    {
                        var parsed = JsonConvert.DeserializeObject<WorkerAnalyzeFaceResponse>(responseText);
                        if (parsed != null)
                            return parsed;
                    }
                    catch
                    {
                    }
                }

                return new WorkerAnalyzeFaceResponse
                {
                    Ok = false,
                    Error = ex.GetBaseException().Message,
                    Mode = mode.ToString().ToUpperInvariant()
                };
            }
            catch (Exception ex)
            {
                return new WorkerAnalyzeFaceResponse
                {
                    Ok = false,
                    Error = ex.GetBaseException().Message,
                    Mode = mode.ToString().ToUpperInvariant()
                };
            }
        }

        private static string ReadErrorBody(WebException ex)
        {
            try
            {
                using (var response = ex.Response)
                using (var stream = response?.GetResponseStream())
                using (var reader = stream == null ? null : new StreamReader(stream))
                    return reader?.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }
    }
}
