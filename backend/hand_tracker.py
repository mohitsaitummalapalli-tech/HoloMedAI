"""
HoloMed AI - Backend Module 1: MediaPipe Vision & Gesture Tracking
File: backend/hand_tracker.py

Features:
- Real-time hand landmark tracking via OpenCV and MediaPipe Tasks HandLandmarker.
- Landmark extraction (Thumb Tip, Index Tip, Palm Base, Wrist, etc.).
- Gesture calculations:
    * Single Hand Translation (X/Y delta) -> {"command": "ROTATE", "target": "HEART", "axis": "Y", "value": delta_x}
    * Pinch Gesture (Thumb-Index distance < threshold) -> {"command": "SELECT", "target": "NEAREST"}
    * Two-Hand Distance Delta -> {"command": "ZOOM", "value": zoom_factor}
- Continuous UDP streaming to 127.0.0.1:5052 with smoothing and rate limiting.
- Holographic Medical HUD overlay with FPS, gesture detection state, and tracking points.
"""

import cv2
import json
import math
import numpy as np
import socket
import sys
import time
from pathlib import Path
from typing import Dict, Any, Optional, Tuple, List

try:
    from backend.gestures.utils import LandmarkSmoother, LandmarkIndex, GestureStateMachine, Point3D
except ImportError:
    from gestures.utils import LandmarkSmoother, LandmarkIndex, GestureStateMachine, Point3D

try:
    import mediapipe as mp
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision
except ImportError:
    print("[ERROR] MediaPipe Tasks is not installed. Run: pip install mediapipe opencv-python")
    sys.exit(1)


def is_fist(hand_landmarks) -> bool:
    """
    Checks if fingers are folded into a fist (clutch mechanism).
    Heuristic: Checks if the tips of the Index (8), Middle (12), Ring (16),
    and Pinky (20) fingers are closer to the wrist (0) or palm base than their
    respective lower PIP joints (6, 10, 14, 18).
    """
    if not hand_landmarks or len(hand_landmarks) < 21:
        return False

    wrist = hand_landmarks[0]
    wx = getattr(wrist, "x", wrist[0] if isinstance(wrist, (list, tuple)) else 0.0)
    wy = getattr(wrist, "y", wrist[1] if isinstance(wrist, (list, tuple)) else 0.0)
    wz = getattr(wrist, "z", wrist[2] if isinstance(wrist, (list, tuple)) and len(wrist) > 2 else 0.0)

    # (tip_index, pip_index) for Index, Middle, Ring, Pinky
    finger_pairs = ((8, 6), (12, 10), (16, 14), (20, 18))

    for tip_idx, pip_idx in finger_pairs:
        tip = hand_landmarks[tip_idx]
        pip = hand_landmarks[pip_idx]

        tx = getattr(tip, "x", tip[0] if isinstance(tip, (list, tuple)) else 0.0)
        ty = getattr(tip, "y", tip[1] if isinstance(tip, (list, tuple)) else 0.0)
        tz = getattr(tip, "z", tip[2] if isinstance(tip, (list, tuple)) and len(tip) > 2 else 0.0)

        px = getattr(pip, "x", pip[0] if isinstance(pip, (list, tuple)) else 0.0)
        py = getattr(pip, "y", pip[1] if isinstance(pip, (list, tuple)) else 0.0)
        pz = getattr(pip, "z", pip[2] if isinstance(pip, (list, tuple)) and len(pip) > 2 else 0.0)

        dist_tip_sq = (tx - wx) ** 2 + (ty - wy) ** 2 + (tz - wz) ** 2
        dist_pip_sq = (px - wx) ** 2 + (py - wy) ** 2 + (pz - wz) ** 2

        # If tip distance to wrist is >= PIP distance to wrist, finger is not curled into a fist
        if dist_tip_sq >= dist_pip_sq:
            return False

    return True


DEFAULT_PINCH_THRESHOLD = 0.085


def is_pinching(hand_landmarks, threshold: float = DEFAULT_PINCH_THRESHOLD) -> bool:
    """
    Calculates the Euclidean distance between Thumb Tip (4) and Index Tip (8).
    Returns True if distance is within the pinch threshold.
    """
    if not hand_landmarks or len(hand_landmarks) < 9:
        return False

    thumb = hand_landmarks[4]
    index = hand_landmarks[8]

    tx = getattr(thumb, "x", thumb[0] if isinstance(thumb, (list, tuple)) else 0.0)
    ty = getattr(thumb, "y", thumb[1] if isinstance(thumb, (list, tuple)) else 0.0)
    tz = getattr(thumb, "z", thumb[2] if isinstance(thumb, (list, tuple)) and len(thumb) > 2 else 0.0)

    ix = getattr(index, "x", index[0] if isinstance(index, (list, tuple)) else 0.0)
    iy = getattr(index, "y", index[1] if isinstance(index, (list, tuple)) else 0.0)
    iz = getattr(index, "z", index[2] if isinstance(index, (list, tuple)) and len(index) > 2 else 0.0)

    dist = math.sqrt((tx - ix) ** 2 + (ty - iy) ** 2 + (tz - iz) ** 2)
    return dist < threshold


def get_pinch_point(hand_landmarks) -> Tuple[float, float, float]:
    """Calculates the 3D midpoint between Thumb Tip (4) and Index Tip (8)."""
    thumb = hand_landmarks[4]
    index = hand_landmarks[8]

    tx = getattr(thumb, "x", thumb[0] if isinstance(thumb, (list, tuple)) else 0.0)
    ty = getattr(thumb, "y", thumb[1] if isinstance(thumb, (list, tuple)) else 0.0)
    tz = getattr(thumb, "z", thumb[2] if isinstance(thumb, (list, tuple)) and len(thumb) > 2 else 0.0)

    ix = getattr(index, "x", index[0] if isinstance(index, (list, tuple)) else 0.0)
    iy = getattr(index, "y", index[1] if isinstance(index, (list, tuple)) else 0.0)
    iz = getattr(index, "z", index[2] if isinstance(index, (list, tuple)) and len(index) > 2 else 0.0)

    return ((tx + ix) * 0.5, (ty + iy) * 0.5, (tz + iz) * 0.5)


class HoloMedHandTracker:
    is_fist = staticmethod(is_fist)
    is_pinching = staticmethod(is_pinching)
    get_pinch_point = staticmethod(get_pinch_point)
    # Landmark indices defined by the MediaPipe Hand Landmarker task.
    WRIST = 0
    THUMB_TIP = 4
    INDEX_FINGER_MCP = 5
    INDEX_FINGER_TIP = 8
    MIDDLE_FINGER_MCP = 9
    HAND_CONNECTIONS = (
        (0, 1), (1, 2), (2, 3), (3, 4),
        (0, 5), (5, 6), (6, 7), (7, 8),
        (5, 9), (9, 10), (10, 11), (11, 12),
        (9, 13), (13, 14), (14, 15), (15, 16),
        (13, 17), (0, 17), (17, 18), (18, 19), (19, 20),
    )

    def __init__(
        self,
        udp_ip: str = "127.0.0.1",
        udp_port: int = 5052,
        camera_index: int = 0,
        max_num_hands: int = 2,
        min_detection_confidence: float = 0.7,
        min_tracking_confidence: float = 0.6,
        model_path: Optional[str] = None,
    ):
        self.udp_ip = udp_ip
        self.udp_port = udp_port
        self.camera_index = camera_index

        # MediaPipe Tasks setup with automatic model download if missing
        default_model_path = Path(__file__).resolve().parent / "models" / "hand_landmarker.task"
        self.model_path = Path(model_path) if model_path else default_model_path
        if not self.model_path.is_file():
            self._download_model()

        options = vision.HandLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(self.model_path)),
            running_mode=vision.RunningMode.VIDEO,
            num_hands=max_num_hands,
            min_hand_detection_confidence=min_detection_confidence,
            min_tracking_confidence=min_tracking_confidence,
        )
        self.hand_landmarker = vision.HandLandmarker.create_from_options(options)

        # Network setup
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        print(f"[HoloMed Vision] UDP Stream configured for {self.udp_ip}:{self.udp_port}")

        # Display & Presentation Settings
        self.show_overlay: bool = True

        # Gesture Tracking State
        self.prev_hand_pos: Optional[Tuple[float, float]] = None
        self.prev_two_hand_dist: Optional[float] = None
        self.prev_pinch_depth: Optional[float] = None  # Depth proxy for single-hand pinch slice
        self.pinch_active: bool = False
        self.pinch_cooldown: float = 0.0
        self.last_sent_time: float = 0.0
        self.send_interval: float = 0.033  # ~30 Hz UDP send rate
        self._last_timestamp_ms: int = 0

        # Sensitivity, Dead-Zone & LERP Parameters
        self.rotation_sensitivity: float = 2.5
        self.rotation_deadzone: float = 0.0035  # Soft-knee Euclidean dead-zone for micro-tremor suppression
        self.rot_lerp_alpha: float = 0.45       # Responsive EMA/LERP smoothing coefficient during active motion
        self.rot_decay_factor: float = 0.75     # Exponential deceleration decay factor when hand stops
        self.max_rot_delta: float = 0.08        # Maximum per-frame displacement clamp against tracking spikes
        self.smooth_dx: float = 0.0             # LERP-smoothed X displacement
        self.smooth_dy: float = 0.0             # LERP-smoothed Y displacement

        self.prev_palm_pos: Optional[Tuple[float, float, float]] = None
        self.prev_palm_roll: Optional[float] = None

        self.pinch_threshold: float = 0.085    # Normalized coordinate distance (thumb-index pinch entry threshold)
        self.pinch_release_threshold: float = 0.12  # Hysteresis release threshold to prevent accidental pinch dropout
        self.zoom_sensitivity: float = 3.0
        self.slice_sensitivity: float = 0.05   # Sensitivity for single-hand pinch slice depth gesture
        self.zoom_deadzone: float = 0.006      # Zoom distance delta dead-zone
        self.slice_deadzone: float = 0.003      # Slice depth delta dead-zone

        # Landmark Smoothing (Enforces EMA alpha=0.65 for Landmark 0 [wrist], Landmark 4 [thumb tip], & Landmark 8 [index tip])
        self.smoothers = [
            LandmarkSmoother(alpha=0.60, landmark_alphas={LandmarkIndex.WRIST: 0.65, LandmarkIndex.THUMB_TIP: 0.65, LandmarkIndex.INDEX_FINGER_TIP: 0.65}),
            LandmarkSmoother(alpha=0.60, landmark_alphas={LandmarkIndex.WRIST: 0.65, LandmarkIndex.THUMB_TIP: 0.65, LandmarkIndex.INDEX_FINGER_TIP: 0.65}),
        ]

        # Temporal Stability Hysteresis State Machine: 2-frame fast engagement for ROTATE, 6-frame release buffer
        self.state_machine = GestureStateMachine(
            stability_threshold=5,
            initial_state="IDLE",
            state_thresholds={"ROTATE": 2, "SLICING": 2},
            release_thresholds={"ROTATE": 6},
        )

        # Performance Monitoring
        self.fps: float = 0.0
        self.prev_frame_time: float = time.time()

    def _download_model(self) -> None:
        """Downloads the official Google MediaPipe Hand Landmarker model if not present."""
        self.model_path.parent.mkdir(parents=True, exist_ok=True)
        url = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task"
        print(f"[HoloMed Vision] Downloading Hand Landmarker model to {self.model_path}...")
        try:
            import urllib.request
            urllib.request.urlretrieve(url, str(self.model_path))
            print(f"[HoloMed Vision] Download completed ({self.model_path.stat().st_size} bytes).")
        except Exception as e:
            raise FileNotFoundError(
                f"Could not download hand_landmarker.task: {e}. "
                f"Please manually place the file at {self.model_path}"
            )

    def send_command(self, payload: Dict[str, Any]) -> None:
        """Helper to serialize and transmit JSON payload over UDP."""
        try:
            message = json.dumps(payload).encode("utf-8")
            self.sock.sendto(message, (self.udp_ip, self.udp_port))
        except Exception as e:
            print(f"[HoloMed Vision] UDP Send Error: {e}")

    @staticmethod
    def calculate_distance(p1: Any, p2: Any) -> float:
        """Euclidean distance between two normalized 3D/2D landmarks."""
        dx = p1.x - p2.x
        dy = p1.y - p2.y
        dz = getattr(p1, "z", 0.0) - getattr(p2, "z", 0.0)
        return math.sqrt(dx * dx + dy * dy + dz * dz)

    def process_gestures(self, multi_hand_landmarks: list, frame_w: int, frame_h: int) -> Dict[str, Any]:
        """Calculates rotation, pinch, and zoom gestures with fist clutch pause & 5-frame stability hysteresis."""
        current_time = time.time()
        status_info = {
            "gesture": "IDLE",
            "pinch": False,
            "two_hands": len(multi_hand_landmarks) >= 2,
            "rotation_delta": 0.0,
            "zoom_factor": 1.0,
            "paused": False,
        }

        # 1. Apply EMA Landmark Smoothing (alpha=0.65 for Landmark 0 [wrist] & Landmark 8 [index tip])
        smoothed_hands = []
        for i, hand_lms in enumerate(multi_hand_landmarks):
            sm = self.smoothers[i % len(self.smoothers)]
            smoothed_hands.append(sm.smooth(hand_lms))

        # 2. Check Fist (clutch/pause) state for each hand (check both raw and smoothed for instant response)
        hand_fist_states = [
            is_fist(multi_hand_landmarks[i]) or is_fist(smoothed_hands[i])
            for i in range(len(multi_hand_landmarks))
        ]
        status_info["hand_fist_states"] = hand_fist_states

        for i, is_closed in enumerate(hand_fist_states):
            if is_closed and i < len(self.smoothers):
                self.smoothers[i].reset()

        active_hands = [h for i, h in enumerate(smoothed_hands) if not hand_fist_states[i]]
        paused_hands = [h for i, h in enumerate(smoothed_hands) if hand_fist_states[i]]
        status_info["two_hands"] = len(active_hands) >= 2

        # Case A: All visible hands are fists -> Tag state as PAUSED, ignore coordinates, emit IDLE payload
        if len(smoothed_hands) > 0 and len(active_hands) == 0:
            self.prev_two_hand_dist = None
            self.prev_hand_pos = None
            self.prev_palm_pos = None
            self.prev_palm_roll = None
            self.smooth_dx = 0.0
            self.smooth_dy = 0.0

            if self.pinch_active:
                self.send_command({
                    "action": "sys.input.interact",
                    "command": "sys.input.interact",
                    "state": "released",
                    "target": "Heart",
                    "dx": 0.0,
                    "dy": 0.0,
                    "timestamp": current_time,
                })
                self.pinch_active = False

            idle_payload = {
                "action": "IDLE",
                "command": "IDLE",
                "state": "idle",
                "target": "HEART",
                "dx": 0.0,
                "dy": 0.0,
                "timestamp": current_time,
            }
            if current_time - self.last_sent_time >= self.send_interval:
                self.send_command(idle_payload)
                self.last_sent_time = current_time

            self.state_machine.reset("PAUSED (FIST)")
            status_info["gesture"] = "PAUSED (FIST)"
            status_info["paused"] = True
            return status_info

        raw_candidate = "IDLE"
        zoom_val = 0.0
        rot_x = 0.0
        rot_y = 0.0
        single_pinching = False

        # Case 1: Two Active Hands Detected -> Two-Hand Pinch and Pull Zoom Control
        if len(active_hands) >= 2:
            hand1 = active_hands[0]
            hand2 = active_hands[1]
            hand1_pinching = is_pinching(hand1, self.pinch_threshold)
            hand2_pinching = is_pinching(hand2, self.pinch_threshold)
            both_pinching = hand1_pinching and hand2_pinching
            status_info["two_hand_pinch"] = both_pinching

            # To trigger ZOOM, BOTH visible hands must be in a pinching state
            if both_pinching:
                # Calculate distance between the two pinch points (midpoint of thumb tip and index tip)
                p1 = get_pinch_point(hand1)
                p2 = get_pinch_point(hand2)
                current_dist = math.sqrt((p1[0] - p2[0]) ** 2 + (p1[1] - p2[1]) ** 2 + (p1[2] - p2[2]) ** 2)

                raw_candidate = "ZOOM"
                if self.prev_two_hand_dist is not None:
                    dist_delta = current_dist - self.prev_two_hand_dist
                    # If gap increases -> positive zoom delta (zoom in); if decreases -> negative zoom delta (zoom out)
                    if abs(dist_delta) > self.zoom_deadzone:
                        zoom_val = round(dist_delta * self.zoom_sensitivity * 10, 3)

                self.prev_two_hand_dist = current_dist
            else:
                self.prev_two_hand_dist = None
            self.prev_hand_pos = None
            self.prev_palm_pos = None
            self.prev_palm_roll = None
            self.smooth_dx = 0.0
            self.smooth_dy = 0.0

        # Case 2: Exactly One Active Hand Tracking (Fist hand explicitly ignored)
        elif len(active_hands) == 1:
            self.prev_two_hand_dist = None
            landmarks = active_hands[0]
            if len(landmarks) > max(self.THUMB_TIP, self.INDEX_FINGER_TIP, self.INDEX_FINGER_MCP, self.MIDDLE_FINGER_MCP):
                thumb_tip = landmarks[self.THUMB_TIP]
                index_tip = landmarks[self.INDEX_FINGER_TIP]
                index_mcp = landmarks[self.INDEX_FINGER_MCP]

                # Pinch Detection with Hysteresis Lock:
                # If tracker was already in the slicing state during the previous frame,
                # allow a wider release threshold (0.12) before dropping the pinch and reverting to rotation.
                current_pinch_threshold = self.pinch_release_threshold if self.prev_pinch_depth is not None else self.pinch_threshold
                single_pinching = is_pinching(landmarks, current_pinch_threshold)
                status_info["pinch"] = single_pinching

                if single_pinching:
                    # Reset rotation smoother when entering slicing
                    self.smooth_dx = 0.0
                    self.smooth_dy = 0.0
                    self.prev_palm_pos = None
                    self.prev_palm_roll = None

                    # --- Single-Hand Pinch Slice Depth Gesture ---
                    # Use wrist(0)-to-middle_finger_MCP(9) 2D pixel distance as depth proxy.
                    # As the hand moves closer to the camera, this distance increases.
                    wrist_lm = landmarks[self.WRIST]
                    mcp9_lm = landmarks[self.MIDDLE_FINGER_MCP]
                    wx = getattr(wrist_lm, "x", 0.0)
                    wy = getattr(wrist_lm, "y", 0.0)
                    mx = getattr(mcp9_lm, "x", 0.0)
                    my = getattr(mcp9_lm, "y", 0.0)
                    current_depth_proxy = math.sqrt((mx - wx) ** 2 + (my - wy) ** 2)

                    if self.prev_pinch_depth is not None:
                        depth_delta = current_depth_proxy - self.prev_pinch_depth
                        if abs(depth_delta) > self.slice_deadzone:
                            slice_value = round(depth_delta * self.slice_sensitivity, 5)
                            status_info["slice_delta"] = slice_value
                        else:
                            slice_value = 0.0
                    else:
                        slice_value = 0.0

                    self.prev_pinch_depth = current_depth_proxy
                    raw_candidate = "SLICING"
                else:
                    # Hand stopped pinching -> reset depth tracker
                    self.prev_pinch_depth = None

                    # --- ANATOMICAL PALM CENTROID & WRIST ORIENTATION TRACKING ---
                    wrist = landmarks[self.WRIST]
                    index_mcp = landmarks[self.INDEX_FINGER_MCP]
                    middle_mcp = landmarks[self.MIDDLE_FINGER_MCP]
                    pinky_mcp = landmarks[17] if len(landmarks) > 17 else index_mcp

                    wx = getattr(wrist, "x", 0.0)
                    wy = getattr(wrist, "y", 0.0)
                    wz = getattr(wrist, "z", 0.0)

                    imx = getattr(index_mcp, "x", 0.0)
                    imy = getattr(index_mcp, "y", 0.0)
                    imz = getattr(index_mcp, "z", 0.0)

                    mmx = getattr(middle_mcp, "x", 0.0)
                    mmy = getattr(middle_mcp, "y", 0.0)
                    mmz = getattr(middle_mcp, "z", 0.0)

                    pmx = getattr(pinky_mcp, "x", 0.0)
                    pmy = getattr(pinky_mcp, "y", 0.0)
                    pmz = getattr(pinky_mcp, "z", 0.0)

                    # 1. Anatomical palm centroid (stable anchor combining wrist and MCP knuckles)
                    current_palm_x = (wx + imx + mmx + pmx) * 0.25
                    current_palm_y = (wy + imy + mmy + pmy) * 0.25
                    current_palm_z = (wz + imz + mmz + pmz) * 0.25

                    # 2. Palm orientation: roll angle across knuckles (pinky MCP to index MCP)
                    dx_knuckles = imx - pmx
                    dy_knuckles = imy - pmy
                    current_palm_roll = math.atan2(dy_knuckles, dx_knuckles)

                    target_dx = 0.0
                    target_dy = 0.0

                    if self.prev_palm_pos is not None and self.prev_palm_roll is not None:
                        # Translational movement of palm centroid
                        d_pos_x = current_palm_x - self.prev_palm_pos[0]
                        d_pos_y = current_palm_y - self.prev_palm_pos[1]

                        # Angular orientation delta (wrist roll / palm tilt)
                        d_roll = current_palm_roll - self.prev_palm_roll
                        if d_roll > math.pi:
                            d_roll -= 2.0 * math.pi
                        elif d_roll < -math.pi:
                            d_roll += 2.0 * math.pi

                        # Integrate translation with subtle wrist tilt roll (adds intuitive wrist rotation)
                        raw_dx = d_pos_x + (d_roll * 0.04)
                        raw_dy = d_pos_y

                        # Clamp extreme single-frame outlier jumps
                        raw_dx = max(-self.max_rot_delta, min(self.max_rot_delta, raw_dx))
                        raw_dy = max(-self.max_rot_delta, min(self.max_rot_delta, raw_dy))

                        # Soft-knee Euclidean dead-zone filter: removes tremor without step jump
                        euclidean_dist = math.hypot(raw_dx, raw_dy)
                        if euclidean_dist > self.rotation_deadzone:
                            ramp = (euclidean_dist - self.rotation_deadzone) / euclidean_dist
                            target_dx = raw_dx * ramp
                            target_dy = raw_dy * ramp
                        else:
                            target_dx = 0.0
                            target_dy = 0.0

                    self.prev_palm_pos = (current_palm_x, current_palm_y, current_palm_z)
                    self.prev_palm_roll = current_palm_roll
                    self.prev_hand_pos = (current_palm_x, current_palm_y)

                    # --- LERP / EMA SMOOTHING FILTER ---
                    if abs(target_dx) > 0.0 or abs(target_dy) > 0.0:
                        # Active motion: responsive EMA/LERP smoothing
                        self.smooth_dx = (1.0 - self.rot_lerp_alpha) * self.smooth_dx + self.rot_lerp_alpha * target_dx
                        self.smooth_dy = (1.0 - self.rot_lerp_alpha) * self.smooth_dy + self.rot_lerp_alpha * target_dy
                    else:
                        # Stationary / decelerating: smooth exponential decay towards zero
                        self.smooth_dx *= self.rot_decay_factor
                        self.smooth_dy *= self.rot_decay_factor
                        if abs(self.smooth_dx) < 1e-4:
                            self.smooth_dx = 0.0
                        if abs(self.smooth_dy) < 1e-4:
                            self.smooth_dy = 0.0

                    rot_y = round(self.smooth_dx * self.rotation_sensitivity * 100, 2)
                    rot_x = round(-self.smooth_dy * self.rotation_sensitivity * 100, 2)

                    if abs(rot_y) > 0.005 or abs(rot_x) > 0.005:
                        raw_candidate = "ROTATE"

        else:
            self.prev_two_hand_dist = None
            self.prev_hand_pos = None
            self.prev_palm_pos = None
            self.prev_palm_roll = None
            self.prev_pinch_depth = None
            self.smooth_dx = 0.0
            self.smooth_dy = 0.0

        # 3. Enforce temporal stability threshold with ROTATE 2-frame fast activation & release buffer
        confirmed_gesture = self.state_machine.update(raw_candidate)

        # 4. Dispatch confirmed gesture commands
        if confirmed_gesture == "ZOOM":
            if abs(zoom_val) > 0.001:
                payload = {
                    "command": "ZOOM",
                    "target": "HEART",
                    "value": zoom_val,
                    "timestamp": current_time,
                }
                if current_time - self.last_sent_time >= self.send_interval:
                    self.send_command(payload)
                    self.last_sent_time = current_time

            status_info["gesture"] = "TWO-HAND PINCH ZOOM"
            if abs(zoom_val) > 0.001:
                status_info["gesture"] = f"TWO-HAND PINCH ZOOM ({'+' if zoom_val > 0 else ''}{zoom_val:.2f})"
            status_info["zoom_factor"] = zoom_val

        elif confirmed_gesture == "SLICING":
            # Single-hand pinch slice depth gesture -> emit SLICE command
            s_delta = status_info.get("slice_delta", 0.0)
            if abs(s_delta) > 0.0001:
                payload = {
                    "command": "SLICE",
                    "target": "HEART",
                    "value": s_delta,
                    "timestamp": current_time,
                }
                if current_time - self.last_sent_time >= self.send_interval:
                    self.send_command(payload)
                    self.last_sent_time = current_time
                    if not self.pinch_active:
                        self.pinch_active = True
                        print(f"[HoloMed Vision] >> Slice Depth: {s_delta:+.5f}")

            status_info["gesture"] = f"SLICING (DEPTH {s_delta:+.4f})"
            status_info["rotation_delta"] = 0.0

        elif confirmed_gesture == "ROTATE" and (abs(rot_y) > 0.001 or abs(rot_x) > 0.001):
            if self.pinch_active:
                self.pinch_active = False

            payload = {
                "action": "ROTATE",
                "command": "ROTATE",
                "state": "rotating",
                "target": "HEART",
                "dx": rot_y,
                "dy": rot_x,
                "value": rot_y,
                "delta": rot_y,
                "timestamp": current_time,
            }
            if current_time - self.last_sent_time >= self.send_interval:
                self.send_command(payload)
                self.last_sent_time = current_time

            status_info["gesture"] = f"ROTATE (dY: {rot_y:+.1f}, dX: {rot_x:+.1f})"
            status_info["rotation_delta"] = rot_y

        else:
            if confirmed_gesture != "SLICING" and self.pinch_active:
                self.pinch_active = False
            status_info["gesture"] = confirmed_gesture

        return status_info

    def draw_landmarks(self, frame: cv2.Mat, landmarks: list, is_paused: bool = False) -> None:
        """Draw Task API landmarks with safe bounds checking and pause/fist styling."""
        height, width, _ = frame.shape
        points = [(int(point.x * width), int(point.y * height)) for point in landmarks]
        line_color = (0, 255, 255)  # Pure Yellow in BGR
        point_color = (0, 230, 255)  # Bright Yellow in BGR
        for start, end in self.HAND_CONNECTIONS:
            if start < len(points) and end < len(points):
                cv2.line(frame, points[start], points[end], line_color, 2, cv2.LINE_AA)
        for point in points:
            cv2.circle(frame, point, 4, point_color, -1, cv2.LINE_AA)
        if is_paused and len(points) > 0:
            wrist_pt = points[0]
            cv2.putText(
                frame,
                "PAUSED (FIST)",
                (max(10, wrist_pt[0] - 50), max(25, wrist_pt[1] - 15)),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.55,
                (0, 255, 255),
                2,
                cv2.LINE_AA,
            )

    def draw_hud(self, frame: cv2.Mat, status: Dict[str, Any], sim_mode: bool = False) -> None:
        """Renders a sleek, holographic medical HUD on the OpenCV preview window."""
        h, w, _ = frame.shape

        # Top Bar Background (Medical Cyan Glassmorphism style)
        overlay = frame.copy()
        cv2.rectangle(overlay, (0, 0), (w, 75), (20, 30, 40), -1)
        cv2.rectangle(overlay, (0, h - 45), (w, h), (20, 30, 40), -1)
        cv2.addWeighted(overlay, 0.75, frame, 0.25, 0, frame)

        # HoloMed AI Title & Status
        title_suffix = " [SIMULATION / NO CAMERA]" if sim_mode else ""
        cv2.putText(
            frame,
            f"HOLOMED AI | NEURAL HAND TRACKER{title_suffix}",
            (18, 30),
            cv2.FONT_HERSHEY_DUPLEX,
            0.65,
            (0, 230, 255),
            2,
        )

        fps_text = f"FPS: {int(self.fps)}"
        cv2.putText(
            frame,
            fps_text,
            (w - 130, 30),
            cv2.FONT_HERSHEY_DUPLEX,
            0.65,
            (0, 255, 170),
            2,
        )

        # Active Gesture Status
        gesture = status.get("gesture", "SEARCHING HANDS")
        is_paused = status.get("paused", False) or "PAUSED" in gesture

        if is_paused:
            gesture_label = "PAUSED (FIST)"
            status_color = (0, 255, 255)  # Bright Yellow (BGR: 0, 255, 255)
        elif "TWO-HAND PINCH ZOOM" in gesture or status.get("two_hand_pinch") or gesture == "ZOOM":
            zoom_val = status.get("zoom_factor", 0.0)
            if abs(zoom_val) > 0.001:
                gesture_label = f"GESTURE: TWO-HAND PINCH ZOOM ({'+' if zoom_val > 0 else ''}{zoom_val:.2f})"
            else:
                gesture_label = "GESTURE: TWO-HAND PINCH ZOOM"
            status_color = (0, 255, 255)  # Cyan HUD highlight
        elif "SLICING" in gesture:
            s_delta = status.get("slice_delta", 0.0)
            if abs(s_delta) > 0.0001:
                gesture_label = f"GESTURE: SLICING (Depth {s_delta:+.4f})"
            else:
                gesture_label = "GESTURE: SLICING (Depth)"
            status_color = (0, 255, 255)
        elif gesture.startswith("ROTATE"):
            gesture_label = f"GESTURE: {gesture}"
            status_color = (0, 255, 100)
        elif gesture == "SEARCHING HANDS":
            gesture_label = "GESTURE: SEARCHING HANDS"
            status_color = (160, 180, 200)
        elif gesture == "IDLE":
            gesture_label = "GESTURE: IDLE"
            status_color = (180, 180, 180)
        else:
            gesture_label = f"GESTURE: {gesture}"
            status_color = (180, 180, 180)

        cv2.putText(
            frame,
            gesture_label,
            (18, 60),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            status_color,
            2,
            cv2.LINE_AA,
        )

        # Bottom Status Bar: UDP Stream: 127.0.0.1:5052 [ACTIVE] | Press 'Q' to Quit | 'H' HUD Overlay
        bottom_status = f"UDP Stream: {self.udp_ip}:{self.udp_port} [ACTIVE] | Press 'Q' to Quit | 'H' HUD Overlay"
        cv2.putText(
            frame,
            bottom_status,
            (18, h - 16),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.45,
            (200, 230, 255),
            1,
            cv2.LINE_AA,
        )

    def _generate_simulation_frame(self, t: float) -> Tuple[cv2.Mat, Dict[str, Any]]:
        """Generates a dynamic medical simulation animation when no physical webcam is attached."""
        w, h = 640, 480
        frame = np.zeros((h, w, 3), dtype=np.uint8)

        # Background grid effect
        for x in range(0, w, 40):
            cv2.line(frame, (x, 0), (x, h), (15, 25, 35), 1)
        for y in range(0, h, 40):
            cv2.line(frame, (0, y), (w, y), (15, 25, 35), 1)

        # Animated holographic hand path
        cx = int(w / 2 + math.sin(t * 1.5) * 120)
        cy = int(h / 2 + math.cos(t * 2.0) * 80)

        # Simulate hand landmarks and pulses
        cv2.circle(frame, (cx, cy), 18, (0, 220, 255), 2, cv2.LINE_AA)
        cv2.circle(frame, (cx, cy), 6, (0, 255, 180), -1, cv2.LINE_AA)
        
        # Fingers
        finger_angles = [-0.6, -0.3, 0.0, 0.3, 0.6]
        for angle in finger_angles:
            fx = int(cx + math.sin(angle + t * 0.5) * 50)
            fy = int(cy - math.cos(angle) * 60)
            cv2.line(frame, (cx, cy), (fx, fy), (0, 180, 240), 2, cv2.LINE_AA)
            cv2.circle(frame, (fx, fy), 4, (255, 255, 255), -1, cv2.LINE_AA)

        # Periodic simulated rotation/pinch/fist
        phase = int(t) % 8
        if phase in [0, 1]:
            gesture_name = f"ROTATING (+{abs(math.sin(t))*25:.1f} deg)"
            status = {"gesture": gesture_name, "pinch": False, "paused": False}
            if time.time() - self.last_sent_time >= 0.5:
                self.send_command({"command": "ROTATE", "target": "HEART", "axis": "Y", "value": 5.0})
                self.last_sent_time = time.time()
        elif phase in [2, 3]:
            gesture_name = "PINCH / SELECT (NEAREST)"
            status = {"gesture": gesture_name, "pinch": True, "paused": False}
            cv2.circle(frame, (cx, cy), 30, (0, 255, 100), 2, cv2.LINE_AA)
        elif phase in [4, 5]:
            gesture_name = "PAUSED (FIST)"
            status = {"gesture": gesture_name, "pinch": False, "paused": True}
            cv2.circle(frame, (cx, cy), 25, (0, 255, 255), 2, cv2.LINE_AA)
            cv2.putText(frame, "PAUSED (FIST)", (cx - 50, cy - 35), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
            if time.time() - self.last_sent_time >= 0.5:
                self.send_command({"action": "IDLE", "command": "IDLE", "state": "idle", "target": "HEART", "dx": 0.0, "dy": 0.0, "timestamp": time.time()})
                self.last_sent_time = time.time()
        else:
            status = {"gesture": "TRACKING ACTIVE", "pinch": False, "paused": False}

        return frame, status

    def run(self) -> None:
        """Main capture and vision processing loop."""
        cap = cv2.VideoCapture(self.camera_index)
        has_camera = cap.isOpened()
        
        if not has_camera:
            print(f"[HoloMed Vision] Note: Physical camera index {self.camera_index} not accessible.")
            print("[HoloMed Vision] Launching Holographic Vision Engine in Simulation / Visual Preview Mode.")
        else:
            # Test reading a test frame
            ret, test_frame = cap.read()
            if not ret or test_frame is None:
                has_camera = False
                cap.release()
                print("[HoloMed Vision] Physical camera did not return frames. Falling back to Visual Simulation Mode.")

        print("[HoloMed Vision] Hand Tracking initialized. Opening GUI preview window...")

        start_sim_time = time.time()

        try:
            while True:
                if has_camera:
                    success, frame = cap.read()
                    if not success or frame is None:
                        time.sleep(0.02)
                        continue

                    # Mirror frame for natural interactive movement
                    frame = cv2.flip(frame, 1)
                    h, w, _ = frame.shape

                    # Convert BGR to SRGB for MediaPipe VIDEO mode
                    rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                    mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb_frame)
                    
                    # Ensure strictly monotonically increasing timestamp
                    timestamp_ms = int(time.monotonic() * 1000)
                    if timestamp_ms <= self._last_timestamp_ms:
                        timestamp_ms = self._last_timestamp_ms + 1
                    self._last_timestamp_ms = timestamp_ms

                    results = self.hand_landmarker.detect_for_video(mp_image, timestamp_ms)

                    # Process Landmarks & Gestures
                    status: Dict[str, Any] = {"gesture": "SEARCHING HANDS", "pinch": False, "paused": False}
                    if results.hand_landmarks:
                        status = self.process_gestures(results.hand_landmarks, w, h)
                        if self.show_overlay:
                            fist_states = status.get("hand_fist_states", [])
                            for i, hand_landmarks in enumerate(results.hand_landmarks):
                                hand_is_paused = fist_states[i] if i < len(fist_states) else False
                                self.draw_landmarks(frame, hand_landmarks, is_paused=hand_is_paused)
                    else:
                        self.prev_hand_pos = None
                        self.prev_palm_pos = None
                        self.prev_palm_roll = None
                        self.prev_two_hand_dist = None
                        self.smooth_dx = 0.0
                        self.smooth_dy = 0.0
                        if self.pinch_active:
                            self.send_command({
                                "action": "sys.input.interact",
                                "command": "sys.input.interact",
                                "state": "released",
                                "target": "Heart",
                                "dx": 0.0,
                                "dy": 0.0,
                                "timestamp": time.time(),
                            })
                            self.pinch_active = False

                    # Calculate Framerate
                    curr_time = time.time()
                    self.fps = 1.0 / max(curr_time - self.prev_frame_time, 0.0001)
                    self.prev_frame_time = curr_time

                    # Draw Medical HUD
                    self.draw_hud(frame, status, sim_mode=False)

                else:
                    # Simulation mode with interactive graphic HUD
                    sim_elapsed = time.time() - start_sim_time
                    frame, status = self._generate_simulation_frame(sim_elapsed)
                    
                    curr_time = time.time()
                    self.fps = 1.0 / max(curr_time - self.prev_frame_time, 0.0001)
                    self.prev_frame_time = curr_time

                    self.draw_hud(frame, status, sim_mode=True)
                    time.sleep(0.03)

                # Show preview window
                cv2.imshow("HOLOMED AI | NEURAL HAND TRACKER", frame)

                key = cv2.waitKey(1) & 0xFF
                if key in (ord("q"), ord("Q"), 27):  # 'q', 'Q' or ESC (27)
                    print("[HoloMed Vision] Exiting vision loop (clean shutdown).")
                    break
                elif key == ord("h"):
                    self.show_overlay = not self.show_overlay
                    print(f"[HoloMed Vision] Presentation Overlay: {'ENABLED' if self.show_overlay else 'HIDDEN'}")
                elif key in (ord("r"), ord("R")):
                    reset_payload = {
                        "action": "RESET",
                        "command": "RESET",
                        "target": "HEART",
                        "timestamp": time.time(),
                    }
                    self.send_command(reset_payload)
                    print("[HoloMed Vision] >> Manual Reset Triggered ('r' key)")

        except KeyboardInterrupt:
            print("\n[HoloMed Vision] Stopped by user.")
        finally:
            if has_camera and cap.isOpened():
                cap.release()
            cv2.destroyAllWindows()
            try:
                self.sock.close()
            except Exception:
                pass
            try:
                self.hand_landmarker.close()
            except Exception:
                pass
            print("[HoloMed Vision] Cleaned up vision resources.")


if __name__ == "__main__":
    tracker = HoloMedHandTracker()
    tracker.run()
