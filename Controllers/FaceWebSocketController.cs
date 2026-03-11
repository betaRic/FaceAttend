using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.WebSockets;
using FaceAttend.Services.Biometrics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaceAttend.Controllers
{
    /// <summary>
    /// WebSocket endpoint for REAL-TIME face recognition.
    /// 
    /// FLOW:
    /// 1. Client connects WebSocket
    /// 2. Client sends face image (base64)
    /// 3. Server instantly matches against pre-loaded faces
    /// 4. Server sends back: employee info or "unknown"
    /// 5. Total time: 100-300ms (vs 1000ms+ with HTTP)
    /// 
    /// USAGE:
    /// var ws = new WebSocket('ws://server/FaceWebSocket/Recognize');
    /// ws.onmessage = (e) => {
    ///   var result = JSON.parse(e.data);
    ///   if (result.isMatch) console.log('Hello ' + result.employeeName);
    /// };
    /// ws.send(JSON.stringify({ imageBase64: '...', lat: 12.3, lon: 45.6 }));
    /// </summary>
    public class FaceWebSocketController : Controller
    {
        // Active WebSocket connections count
        private static int _activeConnections = 0;
        private const int MaxConnections = 100;

        /// <summary>
        /// HTTP GET endpoint that upgrades to WebSocket.
        /// </summary>
        [HttpGet]
        public ActionResult Recognize()
        {
            if (!HttpContext.IsWebSocketRequest)
            {
                return Json(new { ok = false, error = "WebSocket required" }, JsonRequestBehavior.AllowGet);
            }

            if (Interlocked.Increment(ref _activeConnections) > MaxConnections)
            {
                Interlocked.Decrement(ref _activeConnections);
                return new HttpStatusCodeResult(503, "Too many connections");
            }

            HttpContext.AcceptWebSocketRequest(ProcessRecognition);
            return new EmptyResult();
        }

        /// <summary>
        /// Main WebSocket processing loop.
        /// </summary>
        private async Task ProcessRecognition(AspNetWebSocketContext context)
        {
            var socket = context.WebSocket;
            var buffer = new ArraySegment<byte>(new byte[64 * 1024]); // 64KB buffer
            var session = new RecognitionSession();

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                        var response = await ProcessMessage(message, session);
                        
                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        await socket.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash
                System.Diagnostics.Debug.WriteLine($"WebSocket error: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
                socket?.Dispose();
            }
        }

        /// <summary>
        /// Process a single recognition request.
        /// </summary>
        private async Task<string> ProcessMessage(string message, RecognitionSession session)
        {
            var sw = Stopwatch.StartNew();
            
            try
            {
                var json = JObject.Parse(message);
                var action = json["action"]?.ToString() ?? "recognize";

                // PING action for keeping connection alive
                if (action == "ping")
                {
                    return JsonConvert.SerializeObject(new { type = "pong", ms = sw.ElapsedMilliseconds });
                }

                // RECOGNIZE action - the main one
                if (action == "recognize")
                {
                    return await DoRecognition(json, session, sw);
                }

                return JsonConvert.SerializeObject(new { error = "Unknown action", ms = sw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, ms = sw.ElapsedMilliseconds });
            }
        }

        private async Task<string> DoRecognition(JObject json, RecognitionSession session, Stopwatch sw)
        {
            var imageBase64 = json["imageBase64"]?.ToString();
            var faceX = json["faceX"]?.Value<int?>();
            var faceY = json["faceY"]?.Value<int?>();
            var faceW = json["faceW"]?.Value<int?>();
            var faceH = json["faceH"]?.Value<int?>();

            if (string.IsNullOrEmpty(imageBase64))
            {
                return JsonConvert.SerializeObject(new { error = "No image", ms = sw.ElapsedMilliseconds });
            }

            // Decode base64 image
            byte[] imageBytes;
            try
            {
                // Remove data:image/jpeg;base64, prefix if present
                if (imageBase64.Contains(","))
                    imageBase64 = imageBase64.Substring(imageBase64.IndexOf(",") + 1);
                imageBytes = Convert.FromBase64String(imageBase64);
            }
            catch
            {
                return JsonConvert.SerializeObject(new { error = "Invalid image", ms = sw.ElapsedMilliseconds });
            }

            // Save to temp file for processing (required by Dlib)
            string tempPath = null;
            try
            {
                tempPath = Path.Combine(Path.GetTempPath(), $"ws_face_{Guid.NewGuid():N}.jpg");
                System.IO.File.WriteAllBytes(tempPath, imageBytes);

                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string faceErr;
                bool usedClientBox = false;

                // Use client-provided face box if available
                if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue 
                    && faceW.Value > 20 && faceH.Value > 20)
                {
                    faceBox = new DlibBiometrics.FaceBox
                    {
                        Left = faceX.Value,
                        Top = faceY.Value,
                        Width = faceW.Value,
                        Height = faceH.Value
                    };
                    faceLoc = new FaceRecognitionDotNet.Location(
                        faceBox.Left, faceBox.Top, 
                        faceBox.Left + faceBox.Width, faceBox.Top + faceBox.Height);
                    usedClientBox = true;
                }
                else
                {
                    // Server-side detection fallback
                    if (!dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out faceErr))
                    {
                        return JsonConvert.SerializeObject(new 
                        { 
                            recognized = false, 
                            reason = faceErr ?? "No face detected",
                            ms = sw.ElapsedMilliseconds 
                        });
                    }
                }

                // Liveness check
                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(tempPath, faceBox);
                if (!scored.Ok || (scored.Probability ?? 0) < 0.75)
                {
                    return JsonConvert.SerializeObject(new 
                    { 
                        recognized = false, 
                        reason = "Liveness check failed",
                        liveness = scored.Probability,
                        ms = sw.ElapsedMilliseconds 
                    });
                }

                // Encode face to vector
                double[] vec;
                string encErr;
                if (!dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) || vec == null)
                {
                    return JsonConvert.SerializeObject(new 
                    { 
                        recognized = false, 
                        reason = "Encoding failed",
                        ms = sw.ElapsedMilliseconds 
                    });
                }

                // ULTRA-FAST MATCH using pre-loaded RAM cache!
                var matchResult = FastFaceMatcher.FindBestMatch(vec, tolerance: 0.60);
                var matchMs = sw.ElapsedMilliseconds;

                if (matchResult.IsMatch)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        recognized = true,
                        employeeId = matchResult.Employee.EmployeeId,
                        employeeName = matchResult.Employee.DisplayName,
                        department = matchResult.Employee.Department,
                        confidence = matchResult.Confidence,
                        distance = matchResult.Distance,
                        liveness = scored.Probability,
                        usedClientDetection = usedClientBox,
                        ms = matchMs
                    });
                }
                else
                {
                    return JsonConvert.SerializeObject(new
                    {
                        recognized = false,
                        reason = "Unknown face",
                        liveness = scored.Probability,
                        usedClientDetection = usedClientBox,
                        ms = matchMs
                    });
                }
            }
            finally
            {
                // Cleanup temp file
                if (tempPath != null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                }
            }
        }

        /// <summary>
        /// Session state per WebSocket connection.
        /// </summary>
        private class RecognitionSession
        {
            public string SessionId { get; } = Guid.NewGuid().ToString("N");
            public DateTime ConnectedAt { get; } = DateTime.UtcNow;
            public int RecognitionCount { get; set; } = 0;
        }

        /// <summary>
        /// HTTP endpoint to check WebSocket status.
        /// </summary>
        [HttpGet]
        public ActionResult Status()
        {
            return Json(new
            {
                activeConnections = _activeConnections,
                maxConnections = MaxConnections,
                fastMatcherStats = FastFaceMatcher.GetStats()
            }, JsonRequestBehavior.AllowGet);
        }
    }
}
