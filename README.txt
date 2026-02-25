FaceAttend - Day 1 Patch (Server Core)
Date: 2026-02-23

What this patch does:
1) Web.config
   - Biometrics:Liveness:MultiCropScales -> "" (single-scale only)
   - Biometrics:PreprocessJpegQuality -> 88
   - Biometrics:FaceMatchTunerEnabled -> true
   - Adds Kiosk:UseNextGen and Kiosk:EnablePerfTimings feature flags

2) Global.asax.cs
   - Warms up Dlib and ONNX in Application_Start (Task.Run)
   - Keeps Application_End disposal (already present)

3) OnnxLiveness.cs
   - Adds public static WarmUp() to preload the ONNX session

4) DlibBiometrics.cs
   - Adds TryDetectSingleFaceFromFile(...) returning FaceBox + Location (one FaceLocations call)
   - Adds TryEncodeFromFileWithLocation(...) to encode WITHOUT running FaceLocations again

5) KioskController.cs
   - Adds POST /Kiosk/Attend (Rate-limited), behind feature flag Kiosk:UseNextGen
   - Refactors ScanAttendance into a wrapper that calls ScanAttendanceCore()
   - ScanAttendanceCore uses the new Dlib methods so ScanAttendance no longer runs FaceLocations twice
   - Adds optional perf timings (Kiosk:EnablePerfTimings=true)

How to test quickly:
- Deploy these files
- Set Kiosk:UseNextGen=true
- POST a frame to /Kiosk/Attend (same FormData as /Kiosk/ScanAttendance)
- Optional: set Kiosk:EnablePerfTimings=true to include timings in JSON

Notes:
- kiosk.js is NOT changed in Day 1. Existing kiosk flow stays working.
- Day 2 will switch the kiosk client to call /Kiosk/Attend (single-shot).
