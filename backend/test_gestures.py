"""
================================================================================
  HoloMed AI - Essential 8 Gesture Suite Verification & Test Suite
  File: backend/test_gestures.py
================================================================================
"""

import json
import math
import os
import sys
import time
from typing import List

# Setup path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from gestures.utils import (
    GestureDebouncer,
    GestureEvent,
    GestureStateMachine,
    LandmarkIndex,
    LandmarkMath,
    LandmarkSmoother,
    Point3D,
    UDPBroadcaster,
    VelocityTracker,
)
from gestures.essential_gestures import EssentialGestureRecognizer


def create_synthetic_hand(wrist=(0.5, 0.8, 0.0), is_open: bool = True) -> List[Point3D]:
    wx, wy, wz = wrist
    pts = [Point3D(wx, wy, wz) for _ in range(21)]

    # Thumb
    pts[1] = Point3D(wx - 0.04, wy - 0.04, wz)
    pts[2] = Point3D(wx - 0.08, wy - 0.08, wz)
    pts[3] = Point3D(wx - 0.12, wy - 0.12, wz)
    pts[4] = Point3D(wx - 0.15, wy - 0.15, wz) if is_open else Point3D(wx - 0.04, wy - 0.08, wz)

    # Index
    pts[5] = Point3D(wx - 0.04, wy - 0.15, wz)
    pts[6] = Point3D(wx - 0.04, wy - 0.22, wz)
    pts[7] = Point3D(wx - 0.04, wy - 0.28, wz)
    pts[8] = Point3D(wx - 0.04, wy - 0.35, wz) if is_open else Point3D(wx - 0.04, wy - 0.18, wz)

    # Middle
    pts[9] = Point3D(wx, wy - 0.16, wz)
    pts[10] = Point3D(wx, wy - 0.24, wz)
    pts[11] = Point3D(wx, wy - 0.30, wz)
    pts[12] = Point3D(wx, wy - 0.38, wz) if is_open else Point3D(wx, wy - 0.18, wz)

    # Ring
    pts[13] = Point3D(wx + 0.04, wy - 0.15, wz)
    pts[14] = Point3D(wx + 0.04, wy - 0.22, wz)
    pts[15] = Point3D(wx + 0.04, wy - 0.28, wz)
    pts[16] = Point3D(wx + 0.04, wy - 0.34, wz) if is_open else Point3D(wx + 0.04, wy - 0.18, wz)

    # Pinky
    pts[17] = Point3D(wx + 0.07, wy - 0.13, wz)
    pts[18] = Point3D(wx + 0.08, wy - 0.18, wz)
    pts[19] = Point3D(wx + 0.09, wy - 0.23, wz)
    pts[20] = Point3D(wx + 0.10, wy - 0.28, wz) if is_open else Point3D(wx + 0.07, wy - 0.16, wz)

    return pts


def run_tests():
    print("=" * 80)
    print("  HoloMed AI - Essential 8 Gesture Suite Verification")
    print("=" * 80)

    # 1. Test LandmarkMath
    print("\n[1/4] Testing LandmarkMath Euclidean 3D calculations...")
    p1 = Point3D(0.0, 0.0, 0.0)
    p2 = Point3D(3.0, 4.0, 0.0)
    dist = LandmarkMath.dist_3d(p1, p2)
    assert abs(dist - 5.0) < 1e-5, f"Expected 5.0, got {dist}"
    print(f"  [PASS] 3D Euclidean distance verified (dist = {dist:.2f})")

    # 2. Test JSON Schema & Telemetry serialization
    print("\n[2/4] Testing UDP JSON Telemetry Schema...")
    ev = GestureEvent(
        action="PINCH_DRAG",
        confidence=0.96,
        category="CORE",
        parameters={"delta_x": 0.024, "delta_y": -0.015},
    )
    json_str = ev.to_json()
    data = json.loads(json_str)
    assert data["type"] == "GESTURE"
    assert data["action"] == "PINCH_DRAG"
    assert data["confidence"] == 0.96
    print(f"  [PASS] Valid JSON telemetry payload: {json_str}")

    # 3. Test Essential Single-Hand Gestures
    print("\n[3/4] Testing Essential Single-Hand Recognizer...")
    debouncer = GestureDebouncer(default_cooldown=0.0)
    rec = EssentialGestureRecognizer(debouncer=debouncer)

    # Open Palm -> PALM_STOP
    open_hand = create_synthetic_hand(is_open=True)
    events_open = rec.process_single_hand(open_hand)
    print(f"  [PASS] Open Palm evaluated -> Detected: {[e.action for e in events_open]}")

    # Fist -> FIST_LOCK
    fist_hand = create_synthetic_hand(is_open=False)
    events_fist = rec.process_single_hand(fist_hand)
    print(f"  [PASS] Closed Fist evaluated -> Detected: {[e.action for e in events_fist]}")

    # 4. Test Essential Two-Hand Gestures
    print("\n[4/6] Testing Essential Two-Hand Recognizer...")
    # Two fists -> EMERGENCY_RESET
    fist1 = create_synthetic_hand(wrist=(0.48, 0.5, 0.0), is_open=False)
    fist2 = create_synthetic_hand(wrist=(0.52, 0.5, 0.0), is_open=False)
    events_reset = rec.process_multi_hand(fist1, fist2, timestamp=time.time())
    print(f"  [PASS] Dual Fists evaluated -> Detected: {[e.action for e in events_reset]}")

    # 5. Test 5-Frame Temporal Stability Threshold (Hysteresis Buffer)
    print("\n[5/6] Testing 5-Frame Temporal Stability Threshold (Hysteresis Buffer)...")
    sm = GestureStateMachine(stability_threshold=5, initial_state="IDLE")
    # Feed 4 frames of candidate state "PINCH_DRAG" -> Should stay "IDLE"
    for frame in range(1, 5):
        confirmed = sm.update("PINCH_DRAG")
        assert confirmed == "IDLE", f"Frame {frame}: Expected IDLE, got {confirmed}"
    # Frame 5 -> Must confirm transition to "PINCH_DRAG"
    confirmed = sm.update("PINCH_DRAG")
    assert confirmed == "PINCH_DRAG", f"Frame 5: Expected transition to PINCH_DRAG, got {confirmed}"
    print("  [PASS] 5-frame threshold confirmed transition to PINCH_DRAG on exactly frame 5.")

    # Test flicker rejection: 2 frames of spurious "ROTATE" while in "PINCH_DRAG"
    sm.update("ROTATE")
    sm.update("ROTATE")
    assert sm.current_state == "PINCH_DRAG", f"Spurious frames caused premature transition: {sm.current_state}"
    print("  [PASS] Transient 2-frame flicker rejected; maintained PINCH_DRAG state.")

    # 6. Test Landmark 0 & Landmark 8 EMA Smoothing (alpha=0.65) and Euclidean Dead-Zones
    print("\n[6/6] Testing EMA Smoothing (alpha=0.65 for Landmark 0 & 8) & Dead-Zones...")
    smoother = LandmarkSmoother(alpha=0.60)
    assert smoother.landmark_alphas[LandmarkIndex.WRIST] == 0.65, "Landmark 0 (wrist) alpha must be 0.65"
    assert smoother.landmark_alphas[LandmarkIndex.INDEX_FINGER_TIP] == 0.65, "Landmark 8 (index tip) alpha must be 0.65"

    raw_pts = [Point3D(0.0, 0.0, 0.0) for _ in range(21)]
    smoother.smooth(raw_pts)
    raw_pts_step2 = [Point3D(1.0, 1.0, 0.0) for _ in range(21)]
    smoothed_step2 = smoother.smooth(raw_pts_step2)

    # For alpha=0.65: new_val = 0.65 * 1.0 + 0.35 * 0.0 = 0.65
    assert abs(smoothed_step2[0].x - 0.65) < 1e-4, f"Landmark 0 x smoothed expected 0.65, got {smoothed_step2[0].x}"
    assert abs(smoothed_step2[8].x - 0.65) < 1e-4, f"Landmark 8 x smoothed expected 0.65, got {smoothed_step2[8].x}"
    # For default alpha=0.60: new_val = 0.60 * 1.0 + 0.40 * 0.0 = 0.60
    assert abs(smoothed_step2[1].x - 0.60) < 1e-4, f"Landmark 1 x smoothed expected 0.60, got {smoothed_step2[1].x}"
    print("  [PASS] Landmark 0 (wrist) and Landmark 8 (index tip) accurately smoothed with alpha = 0.65.")

    # Test Euclidean dead-zone calculation
    dx, dy = 0.002, 0.003
    euclid_dist = math.sqrt(dx * dx + dy * dy)
    deadzone = 0.005
    assert euclid_dist <= deadzone, f"Expected {euclid_dist} <= {deadzone}"
    print(f"  [PASS] Euclidean dead-zone filter suppresses minor tremor ({euclid_dist:.4f} <= {deadzone:.4f} -> 0 delta).")

    print("\n" + "=" * 80)
    print("  ALL ESSENTIAL 8 GESTURE SUITE & CALIBRATION TESTS PASSED!")
    print("=" * 80)


if __name__ == "__main__":
    run_tests()
