"""
================================================================================
  HoloMed AI - Phase 2: Advanced Spatial Interaction Gesture Engine
  File: backend/gesture_engine.py
================================================================================
Detects MediaPipe Hands landmarks in real-time, calculates:
1. PINCH & DRAG: Tracks Thumb-Index distance and spatial delta (X, Y) -> emits "DRAG".
2. POINT & SPATIAL MEASUREMENT: Evaluates point vector -> emits "POINT" with distance/volume.
3. CONTINUOUS ROTATION (ROTATE_WRIST_CW / CCW)
4. RADIAL VOLUMETRIC ZOOM (ZOOM_SPREAD / CLOSE)
5. PALM_STOP & FIST_LOCK (Freeze / Lock)
6. Bimanual CROSS_SECTION_SPLIT & EMERGENCY_RESET

Streams rich JSON payloads over UDP Port 5053 to Unity.
"""

import argparse
import json
import math
import os
import sys
import time
import urllib.request
from collections import deque
from typing import Any, Dict, List, Optional, Tuple

import cv2
import mediapipe as mp
from mediapipe.tasks import python
from mediapipe.tasks.python import vision

# Ensure project root is in sys.path
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PARENT_DIR = os.path.dirname(BASE_DIR)
for p in (BASE_DIR, PARENT_DIR):
    if p not in sys.path:
        sys.path.insert(0, p)

from gestures.utils import (
    GestureDebouncer,
    GestureEvent,
    LandmarkIndex,
    LandmarkMath,
    LandmarkSmoother,
    Point3D,
    UDPBroadcaster,
)
from gestures.essential_gestures import EssentialGestureRecognizer

MODEL_URL = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task"
DEFAULT_MODEL_FILENAME = "hand_landmarker.task"

# Standard 21 MediaPipe Hand Landmark Connections
HAND_CONNECTIONS = [
    # Palm
    (0, 1), (1, 2), (2, 5), (5, 9), (9, 13), (13, 17), (17, 0), (0, 5),
    # Thumb
    (1, 2), (2, 3), (3, 4),
    # Index
    (5, 6), (6, 7), (7, 8),
    # Middle
    (9, 10), (10, 11), (11, 12),
    # Ring
    (13, 14), (14, 15), (15, 16),
    # Pinky
    (17, 18), (18, 19), (19, 20),
]


def ensure_model_exists(target_dir: Optional[str] = None) -> str:
    """Checks if hand_landmarker.task exists, auto-downloads if missing."""
    if target_dir is None:
        target_dir = os.path.join(BASE_DIR, "models")
    os.makedirs(target_dir, exist_ok=True)
    model_path = os.path.join(target_dir, DEFAULT_MODEL_FILENAME)

    if os.path.exists(model_path) and os.path.getsize(model_path) > 1000000:
        return model_path

    print(f"[HoloMed AI] Downloading HandLandmarker model from Google CDN...")

    def _progress_hook(block_num, block_size, total_size):
        downloaded = block_num * block_size
        if total_size > 0:
            percent = min(100.0, downloaded * 100.0 / total_size)
            mb = downloaded / (1024 * 1024)
            tot_mb = total_size / (1024 * 1024)
            sys.stdout.write(f"\r  -> Progress: {percent:5.1f}% ({mb:.1f}MB / {tot_mb:.1f}MB)")
            sys.stdout.flush()

    try:
        urllib.request.urlretrieve(MODEL_URL, model_path, reporthook=_progress_hook)
        print(f"\n[HoloMed AI] Successfully downloaded model to '{model_path}'.")
    except Exception as e:
        print(f"\n[ERROR] Failed to download model: {e}")
        alt_path = os.path.join(BASE_DIR, DEFAULT_MODEL_FILENAME)
        if os.path.exists(alt_path):
            return alt_path
        raise RuntimeError(f"Could not download or find hand_landmarker.task: {e}")

    return model_path


def put_outlined_text(
    img: cv2.Mat,
    text: str,
    pos: Tuple[int, int],
    font_face: int,
    font_scale: float,
    color: Tuple[int, int, int],
    thickness: int = 1,
    outline_color: Tuple[int, int, int] = (0, 0, 0),
    outline_thickness: int = 3,
) -> None:
    """Renders high-contrast text with a crisp dark outline."""
    x, y = pos
    cv2.putText(img, text, (x, y), font_face, font_scale, outline_color, outline_thickness, cv2.LINE_AA)
    cv2.putText(img, text, (x, y), font_face, font_scale, color, thickness, cv2.LINE_AA)


class HoloMedGestureEngine:
    """
    Main runner for camera capture, MediaPipe Tasks pipeline,
    Pinch-Drag & Point-Measurement calculations, HUD, and UDP telemetry.
    """

    def __init__(
        self,
        camera_index: int = 0,
        udp_host: str = "127.0.0.1",
        udp_port: int = 5053,
        rate_limit_hz: float = 60.0,
        mirror: bool = True,
        show_hands: bool = True,
        show_telemetry: bool = True,
        model_path: Optional[str] = None,
    ):
        self.camera_index = camera_index
        self.udp_host = udp_host
        self.udp_port = udp_port
        self.rate_limit_hz = rate_limit_hz
        self.mirror = mirror

        # Keyboard Toggles
        self.show_hands = show_hands            # Key 'H'
        self.show_telemetry = show_telemetry    # Key 'T'
        self.show_all = True                    # Key 'A' (Master Toggle)

        # 1. Model Resolution & Tasks API Initializer
        if model_path is None or not os.path.exists(model_path):
            model_path = ensure_model_exists()

        self.model_path = model_path
        print(f"[HoloMed AI] Loading MediaPipe Tasks HandLandmarker from '{self.model_path}'...")

        base_options = python.BaseOptions(model_asset_path=self.model_path)
        options = vision.HandLandmarkerOptions(
            base_options=base_options,
            running_mode=vision.RunningMode.IMAGE,
            num_hands=2,
            min_hand_detection_confidence=0.65,
            min_hand_presence_confidence=0.65,
            min_tracking_confidence=0.65,
        )
        self.landmarker = vision.HandLandmarker.create_from_options(options)

        # 2. Gesture Recognizer & UDP Broadcaster
        self.debouncer = GestureDebouncer(default_cooldown=0.15)
        self.broadcaster = UDPBroadcaster(host=self.udp_host, port=self.udp_port, rate_limit_hz=self.rate_limit_hz)
        self.recognizer = EssentialGestureRecognizer(debouncer=self.debouncer)

        self.smoothers = {
            "Right": LandmarkSmoother(alpha=0.60, landmark_alphas={LandmarkIndex.WRIST: 0.65, LandmarkIndex.INDEX_FINGER_TIP: 0.65}),
            "Left": LandmarkSmoother(alpha=0.60, landmark_alphas={LandmarkIndex.WRIST: 0.65, LandmarkIndex.INDEX_FINGER_TIP: 0.65}),
        }

        # 3. State & Telemetry Metrics
        self.fps: float = 0.0
        self.prev_frame_time: float = time.time()
        self.recent_events: deque = deque(maxlen=8)
        self.active_gestures_current_frame: List[GestureEvent] = []
        self.udp_packets_sent: int = 0
        self.last_sent_timestamp: float = 0.0

    def _process_hand_landmarks(
        self,
        raw_landmarks: List[Any],
        handedness: str,
        timestamp: float,
    ) -> Tuple[List[Point3D], List[GestureEvent]]:
        smoother = self.smoothers.get(handedness, self.smoothers["Right"])
        pts = [LandmarkMath.to_point(lm) for lm in raw_landmarks]
        smoothed_pts = smoother.smooth(pts)
        detected = self.recognizer.process_single_hand(smoothed_pts, handedness=handedness, timestamp=timestamp)
        return smoothed_pts, detected

    def _process_multi_hand(
        self,
        hand1_pts: List[Point3D],
        hand2_pts: List[Point3D],
        timestamp: float,
    ) -> List[GestureEvent]:
        return self.recognizer.process_multi_hand(hand1_pts, hand2_pts, timestamp=timestamp)

    def _broadcast_events(self, events: List[GestureEvent]) -> None:
        for event in events:
            sent = self.broadcaster.send_gesture(event)
            if sent:
                self.udp_packets_sent += 1
                self.last_sent_timestamp = time.time()
                self.recent_events.appendleft((time.time(), event))
                print(f"[UDP -> Unity {self.udp_port}] Action: {event.action:<16} Params: {event.parameters}")

    def _draw_hand_skeleton(self, frame: cv2.Mat, landmarks: List[Any], handedness: str = "Right") -> None:
        """Draws glowing joints, skeleton connection lines, and spatial interaction reticles."""
        h, w, _ = frame.shape
        coords: List[Tuple[int, int]] = []

        for lm in landmarks:
            cx, cy = int(lm.x * w), int(lm.y * h)
            coords.append((cx, cy))

        line_color = (0, 220, 255) if handedness == "Right" else (255, 90, 180)
        node_color = (0, 255, 140) if handedness == "Right" else (255, 200, 80)
        tip_color = (255, 180, 0) if handedness == "Right" else (0, 255, 255)

        for start_idx, end_idx in HAND_CONNECTIONS:
            if start_idx < len(coords) and end_idx < len(coords):
                cv2.line(frame, coords[start_idx], coords[end_idx], line_color, 2, cv2.LINE_AA)

        tip_indices = {4, 8, 12, 16, 20}
        for idx, (cx, cy) in enumerate(coords):
            if idx in tip_indices:
                cv2.circle(frame, (cx, cy), 7, tip_color, -1, cv2.LINE_AA)
                cv2.circle(frame, (cx, cy), 10, line_color, 1, cv2.LINE_AA)
            elif idx == 0:
                cv2.circle(frame, (cx, cy), 8, (255, 255, 255), -1, cv2.LINE_AA)
            else:
                cv2.circle(frame, (cx, cy), 4, node_color, -1, cv2.LINE_AA)

        # Spatial Interaction Visualizations (Pinch line & Point Measurement Reticle)
        for ev in self.active_gestures_current_frame:
            if ev.action == "DRAG" and len(coords) > 8:
                # Draw pinch connecting laser
                cv2.line(frame, coords[4], coords[8], (0, 255, 0), 3, cv2.LINE_AA)
                cv2.circle(frame, coords[8], 14, (0, 255, 255), 2, cv2.LINE_AA)
                put_outlined_text(frame, "PINCH DRAG", (coords[8][0] + 15, coords[8][1] - 10), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 255, 255), 1)

            elif ev.action == "POINT" and len(coords) > 8:
                # Draw AI Caliper Measurement Reticle on index fingertip
                ix, iy = coords[8]
                cv2.drawMarker(frame, (ix, iy), (0, 240, 255), cv2.MARKER_CROSS, 24, 2, cv2.LINE_AA)
                cv2.circle(frame, (ix, iy), 18, (0, 240, 255), 1, cv2.LINE_AA)
                label = ev.parameters.get("measurement_label", "AI Caliper")
                put_outlined_text(frame, f"[POINT] {label}", (ix + 20, iy - 15), cv2.FONT_HERSHEY_SIMPLEX, 0.44, (0, 240, 255), 1)

        if coords:
            wx, wy = coords[0]
            label_text = f"[{handedness.upper()}]"
            put_outlined_text(frame, label_text, (wx - 25, wy + 25), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (255, 255, 255), 1, (0, 0, 0), 3)

    def _render_hud(self, frame: cv2.Mat, hand_count: int) -> None:
        """Renders high-contrast telemetry dashboard over a dark alpha box."""
        h, w, _ = frame.shape
        overlay = frame.copy()

        # 1. Top Status Banner
        cv2.rectangle(overlay, (0, 0), (w, 58), (10, 14, 22), -1)
        cv2.addWeighted(overlay, 0.88, frame, 0.12, 0, frame)
        cv2.line(frame, (0, 58), (w, 58), (0, 180, 220), 1)

        put_outlined_text(frame, "HOLOMED AI // ADVANCED SPATIAL ENGINE", (20, 26), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (0, 240, 255), 2, (0, 0, 0), 4)
        put_outlined_text(frame, f"UDP: {self.udp_host}:{self.udp_port}  |  SKELETON [H]: {'ON' if self.show_hands else 'OFF'}  |  TELEMETRY [T]: {'ON' if self.show_telemetry else 'OFF'}", (20, 48), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (200, 230, 255), 1, (0, 0, 0), 3)

        fps_text = f"FPS: {self.fps:04.1f}"
        put_outlined_text(frame, fps_text, (w - 230, 26), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (0, 255, 170), 2, (0, 0, 0), 4)
        hands_text = f"HANDS: {hand_count}/2 DETECTED"
        put_outlined_text(frame, hands_text, (w - 230, 48), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (220, 240, 255), 1, (0, 0, 0), 3)

        tx_color = (0, 255, 128) if (time.time() - self.last_sent_timestamp) < 0.25 else (50, 60, 70)
        cv2.circle(frame, (w - 250, 32), 7, (0, 0, 0), -1)
        cv2.circle(frame, (w - 250, 32), 6, tx_color, -1)

        # 2. Left Telemetry Box
        panel_w = 380
        panel_h = min(h - 90, 390)
        panel_overlay = frame.copy()
        cv2.rectangle(panel_overlay, (15, 70), (15 + panel_w, 70 + panel_h), (8, 12, 18), -1)
        cv2.addWeighted(panel_overlay, 0.90, frame, 0.10, 0, frame)

        cv2.rectangle(frame, (15, 70), (15 + panel_w, 70 + panel_h), (0, 180, 220), 1)
        put_outlined_text(frame, "SPATIAL INTERACTION STREAM", (25, 94), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (0, 240, 255), 1, (0, 0, 0), 3)
        cv2.line(frame, (25, 104), (15 + panel_w - 10, 104), (0, 140, 180), 1)

        y_cursor = 130
        if self.active_gestures_current_frame:
            put_outlined_text(frame, "ACTIVE SPATIAL GESTURES:", (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.44, (180, 210, 240), 1, (0, 0, 0), 3)
            y_cursor += 24
            for g in self.active_gestures_current_frame[:2]:
                put_outlined_text(frame, f"{g.action}", (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.54, (255, 255, 255), 2, (0, 0, 0), 4)
                y_cursor += 20

                # Parameter details
                if g.action == "DRAG":
                    param_text = f"dx: {g.parameters.get('delta_x', 0):+.3f}, dy: {g.parameters.get('delta_y', 0):+.3f}"
                    put_outlined_text(frame, param_text, (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (0, 255, 180), 1)
                elif g.action == "POINT":
                    param_text = f"Dist: {g.parameters.get('estimated_distance_mm', 0)} mm | Vol: {g.parameters.get('estimated_volume_ml', 0)} mL"
                    put_outlined_text(frame, param_text, (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (0, 240, 255), 1)
                else:
                    put_outlined_text(frame, f"Confidence: {int(g.confidence * 100)}%", (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (0, 255, 180), 1)
                y_cursor += 24
        else:
            put_outlined_text(frame, "STATUS: TRACKING / IDLE", (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (120, 160, 190), 1, (0, 0, 0), 3)
            y_cursor += 32

        # Recent Event History Log
        cv2.line(frame, (25, y_cursor), (15 + panel_w - 10, y_cursor), (40, 70, 90), 1)
        y_cursor += 20
        put_outlined_text(frame, "RECENT DISPATCH LOG (UDP -> Unity):", (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (160, 190, 220), 1, (0, 0, 0), 3)
        y_cursor += 22

        for ts, ev in list(self.recent_events)[:5]:
            age = max(0.0, time.time() - ts)
            alpha_col = (200, 240, 255) if age < 1.0 else ((140, 180, 210) if age < 3.0 else (90, 120, 140))
            log_line = f"- {ev.action[:16]:<16} ({ev.confidence:.2f}) {age:.1f}s"
            put_outlined_text(frame, log_line, (25, y_cursor), cv2.FONT_HERSHEY_SIMPLEX, 0.40, alpha_col, 1, (0, 0, 0), 3)
            y_cursor += 20

        # 3. Bottom Hotkey Help Bar
        bot_overlay = frame.copy()
        cv2.rectangle(bot_overlay, (0, h - 34), (w, h), (10, 14, 20), -1)
        cv2.addWeighted(bot_overlay, 0.92, frame, 0.08, 0, frame)
        cv2.line(frame, (0, h - 34), (w, h - 34), (0, 180, 220), 1)
        help_text = "[H] Hand Skeleton | [T] Telemetry Box | [A] All Overlays | [M] Mirror | [Q] Quit"
        put_outlined_text(frame, help_text, (20, h - 11), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (190, 220, 240), 1, (0, 0, 0), 3)

    def run(self) -> None:
        """Runs the real-time webcam capture and gesture recognition loop."""
        print(f"\n[HoloMed AI] Opening camera device index {self.camera_index}...")
        cap = cv2.VideoCapture(self.camera_index, cv2.CAP_DSHOW if os.name == "nt" else cv2.CAP_ANY)

        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
        cap.set(cv2.CAP_PROP_FPS, 30)

        if not cap.isOpened():
            print(f"[ERROR] Could not open camera device index {self.camera_index}.")
            return

        window_name = "HoloMed AI - Phase 2 Spatial Engine (UDP 5053)"
        cv2.namedWindow(window_name, cv2.WINDOW_NORMAL)
        cv2.resizeWindow(window_name, 1280, 720)

        print(f"[HoloMed AI] Engine active. Streaming UDP telemetry to {self.udp_host}:{self.udp_port}")
        print("[HoloMed AI] Hotkeys: [H] Hand Skeleton, [T] Telemetry Box, [A] All Overlays, [M] Mirror, [Q] Quit\n")

        try:
            while cap.isOpened():
                success, frame = cap.read()
                if not success or frame is None:
                    time.sleep(0.01)
                    continue

                if self.mirror:
                    frame = cv2.flip(frame, 1)

                now = time.time()
                dt = max(0.0001, now - self.prev_frame_time)
                self.fps = 0.9 * self.fps + 0.1 * (1.0 / dt)
                self.prev_frame_time = now

                # MediaPipe Tasks API
                rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb_frame)
                detection_result = self.landmarker.detect(mp_image)

                self.active_gestures_current_frame.clear()
                hand_count = 0
                processed_hands: List[Tuple[str, List[Point3D]]] = []

                if detection_result.hand_landmarks:
                    hand_count = len(detection_result.hand_landmarks)

                    for hand_idx, raw_lms in enumerate(detection_result.hand_landmarks):
                        label = "Right"
                        if hand_idx < len(detection_result.handedness) and len(detection_result.handedness[hand_idx]) > 0:
                            category = detection_result.handedness[hand_idx][0]
                            label = category.category_name or category.display_name or "Right"

                        smoothed_pts, single_events = self._process_hand_landmarks(raw_lms, label, timestamp=now)
                        processed_hands.append((label, smoothed_pts))
                        self.active_gestures_current_frame.extend(single_events)

                        # Draw hand skeleton & spatial interaction reticles
                        if self.show_all and self.show_hands:
                            self._draw_hand_skeleton(frame, raw_lms, handedness=label)

                    # Bimanual processing
                    if len(processed_hands) >= 2:
                        multi_events = self._process_multi_hand(
                            processed_hands[0][1],
                            processed_hands[1][1],
                            timestamp=now,
                        )
                        self.active_gestures_current_frame.extend(multi_events)

                # Broadcast detected events over UDP
                if self.active_gestures_current_frame:
                    self._broadcast_events(self.active_gestures_current_frame)

                # Render Telemetry Box
                if self.show_all and self.show_telemetry:
                    self._render_hud(frame, hand_count)

                cv2.imshow(window_name, frame)

                # Keyboard controls
                key = cv2.waitKey(1) & 0xFF
                if key in (ord('q'), ord('Q'), 27):  # 'Q' or ESC
                    print("\n[HoloMed AI] Exit requested by user.")
                    break
                elif key in (ord('h'), ord('H')):
                    self.show_hands = not self.show_hands
                    print(f"[HoloMed AI] Hand Skeleton: {'ENABLED' if self.show_hands else 'DISABLED'}")
                elif key in (ord('t'), ord('T')):
                    self.show_telemetry = not self.show_telemetry
                    print(f"[HoloMed AI] Telemetry Box: {'ENABLED' if self.show_telemetry else 'DISABLED'}")
                elif key in (ord('a'), ord('A')):
                    self.show_all = not self.show_all
                    print(f"[HoloMed AI] All Overlays: {'ENABLED' if self.show_all else 'DISABLED (Clean Video)'}")
                elif key in (ord('m'), ord('M')):
                    self.mirror = not self.mirror
                    print(f"[HoloMed AI] Mirror Mode: {'ENABLED' if self.mirror else 'DISABLED'}")

        except KeyboardInterrupt:
            print("\n[HoloMed AI] Engine terminated via keyboard interrupt.")
        finally:
            cap.release()
            cv2.destroyAllWindows()
            self.landmarker.close()
            self.broadcaster.close()
            print("[HoloMed AI] Gesture Engine shutdown cleanly.")


def main():
    parser = argparse.ArgumentParser(description="HoloMed AI - Phase 2 Spatial Interaction Engine (Tasks API + UDP 5053)")
    parser.add_argument("--camera", type=int, default=0, help="Camera device index (default: 0)")
    parser.add_argument("--host", type=str, default="127.0.0.1", help="UDP broadcast target IP (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=5053, help="UDP transmission port for Unity (default: 5053)")
    parser.add_argument("--rate-limit", type=float, default=60.0, help="Max UDP transmission rate in Hz (default: 60)")
    parser.add_argument("--model", type=str, default=None, help="Path to hand_landmarker.task model file")
    parser.add_argument("--no-mirror", action="store_true", help="Disable horizontal webcam flip")
    parser.add_argument("--hide-hands", action="store_true", help="Start with hand skeleton hidden")
    parser.add_argument("--hide-telemetry", action="store_true", help="Start with telemetry box hidden")

    args = parser.parse_args()
    engine = HoloMedGestureEngine(
        camera_index=args.camera,
        udp_host=args.host,
        udp_port=args.port,
        rate_limit_hz=args.rate_limit,
        mirror=not args.no_mirror,
        show_hands=not args.hide_hands,
        show_telemetry=not args.hide_telemetry,
        model_path=args.model,
    )
    engine.run()


if __name__ == "__main__":
    main()
