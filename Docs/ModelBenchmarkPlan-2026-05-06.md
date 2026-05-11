# FaceAttend Model Benchmark Plan

Date: 2026-05-06

## Blunt Standard

Do not claim 99% accuracy from vendor papers or public datasets. For this system, accuracy means local employees, local cameras, local lighting, local network, and actual attendance behavior.

The model selection gate is:

- 0 confirmed wrong-employee records in pilot.
- False accept rate is prioritized over false reject rate.
- p95 scan latency under 5 seconds on the target server.
- All accepted scans store model version, detector, recognizer, anti-spoof model, score, threshold, second-best identity, ambiguity gap, and review reason.

## Candidate Stack

| Layer | Baseline | OpenVINO / ONNX candidates | Selection rule |
|---|---|---|---|
| Detection | No MVC-hosted detector | OpenVINO `face-detection-retail-0004` or stronger worker detector | Reject multi-face public/enrollment frames; kiosk only accepts clearly dominant face. |
| Landmarks | No MVC-hosted landmark model | OpenVINO `landmarks-regression-retail-0009` | Stable alignment across kiosk and phone captures. |
| Recognition | No MVC-hosted recognizer | OpenVINO `face-reidentification-retail-0095`, ArcFace ResNet100 ONNX | Lowest false accept first; ArcFace can be enrollment/risky-pair audit if too slow for every scan. |
| Anti-spoof | No MVC-hosted anti-spoof model | OpenVINO `anti-spoof-mn3` and worker-swappable anti-spoof candidates | Treat as risk signal until calibrated; do not treat it as proof of real presence. |

## Dataset Buckets

Use fresh enrollment after the final model is locked. Before that, collect benchmark-only captures:

- Employee enrollment-quality frontal captures.
- Employee normal public/mobile scans.
- Employee kiosk camera scans.
- Poor lighting and backlit captures.
- Low-end phone camera captures.
- Similar-looking employee pairs.
- Printed-photo spoof attempts.
- Phone-screen photo spoof attempts.
- Replay/video spoof attempts where feasible.
- Multi-face scenes with a dominant subject and unclear scenes.

## CSV Schema

Every benchmark row should contain:

```csv
capture_id,employee_id,expected_employee_id,source,device,camera_model,lighting,spoof_type,is_spoof,face_count,selected_face_area_ratio,detector,landmarker,recognizer,anti_spoof_model,model_version,distance,best_employee_id,second_employee_id,second_distance,ambiguity_gap,anti_spoof_score,anti_spoof_decision,sharpness,latency_ms,accepted,needs_review,review_codes,error
```

## Metrics

- False accept rate: any accepted wrong employee.
- False reject rate: enrolled employee rejected.
- Ambiguous-pair rate: close second-best match below policy gap.
- Anti-spoof false reject: real person blocked or retried repeatedly.
- Spoof pass rate: spoof accepted without review/block.
- p50/p95/p99 latency per source and per model.
- Model failure rate and circuit-open events.

## Decision Rule

1. Reject any model stack with confirmed wrong-employee accepts in controlled pilot.
2. Prefer lower false accepts over smoother UX.
3. If fast re-id is weaker than ArcFace, use ArcFace for enrollment and risky-pair audit, then only use fast re-id for live scans if pilot data proves the gap is safe.
4. Keep the selected OpenVINO worker stack as baseline until another stack beats it locally.
5. Lock model/version before production enrollment. No mixed embedding versions for active attendance.
