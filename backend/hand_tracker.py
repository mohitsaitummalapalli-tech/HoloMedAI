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
    import mediapipe as mp
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision
except ImportError:
    print("[ERROR] MediaPipe Tasks is not installed. Run: pip install mediapipe opencv-python")
    sys.exit(1)


class HoloMedHandTracker:
    # Landmark indices defined by the MediaPipe Hand Landmarker task.
    WRIST = 0
    THUMB_TIP = 4
    INDEX_FINGER_MCP = 5
    INDEX_FINGER_TIP = 8
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
        self.pinch_active: bool = False
        self.pinch_cooldown: float = 0.0
        self.last_sent_time: float = 0.0
        self.send_interval: float = 0.033  # ~30 Hz UDP send rate
        self._last_timestamp_ms: int = 0

        # Sensitivity Parameters
        self.rotation_sensitivity: float = 2.5
        self.pinch_threshold: float = 0.05  # Normalized coordinate distance
        self.zoom_sensitivity: float = 3.0

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
        """Calculates rotation, pinch, and zoom gestures from hand landmarks."""
        current_time = time.time()
        status_info = {
            "gesture": "IDLE",
            "pinch": False,
            "two_hands": len(multi_hand_landmarks) >= 2,
            "rotation_delta": 0.0,
            "zoom_factor": 1.0,
        }

        # Case 1: Two Hands Detected -> Zoom / Scaling Control
        if len(multi_hand_landmarks) >= 2:
            if len(multi_hand_landmarks[0]) > self.WRIST and len(multi_hand_landmarks[1]) > self.WRIST:
                hand1_wrist = multi_hand_landmarks[0][self.WRIST]
                hand2_wrist = multi_hand_landmarks[1][self.WRIST]
                current_dist = self.calculate_distance(hand1_wrist, hand2_wrist)

                if self.prev_two_hand_dist is not None:
                    dist_delta = current_dist - self.prev_two_hand_dist
                    if abs(dist_delta) > 0.005:  # Deadband filter
                        zoom_val = round(dist_delta * self.zoom_sensitivity * 10, 3)
                        payload = {
                            "command": "ZOOM",
                            "target": "HEART",
                            "value": zoom_val,
                            "timestamp": current_time,
                        }
                        if current_time - self.last_sent_time >= self.send_interval:
                            self.send_command(payload)
                            self.last_sent_time = current_time

                        status_info["gesture"] = f"TWO-HAND ZOOM ({'+' if zoom_val > 0 else ''}{zoom_val:.2f})"
                        status_info["zoom_factor"] = zoom_val

                self.prev_two_hand_dist = current_dist
                self.prev_hand_pos = None  # Reset single-hand translation to avoid conflicts
                return status_info

        # Reset two-hand state if fewer than 2 hands
        self.prev_two_hand_dist = None

        # Case 2: Single Hand Tracking
        if len(multi_hand_landmarks) == 1:
            landmarks = multi_hand_landmarks[0]
            if len(landmarks) > max(self.THUMB_TIP, self.INDEX_FINGER_TIP, self.INDEX_FINGER_MCP):
                thumb_tip = landmarks[self.THUMB_TIP]
                index_tip = landmarks[self.INDEX_FINGER_TIP]
                index_mcp = landmarks[self.INDEX_FINGER_MCP]

                # 1. Pinch Detection (Thumb Tip to Index Tip)
                pinch_dist = self.calculate_distance(thumb_tip, index_tip)
                is_pinching = pinch_dist < self.pinch_threshold
                status_info["pinch"] = is_pinching

                if is_pinching:
                    if not self.pinch_active and (current_time - self.pinch_cooldown > 0.4):
                        self.pinch_active = True
                        self.pinch_cooldown = current_time
                        payload = {
                            "command": "SELECT",
                            "target": "NEAREST",
                            "organ": "HEART",
                            "timestamp": current_time,
                        }
                        self.send_command(payload)
                        print(f"[HoloMed Vision] >> Pinch Triggered: SELECT NEAREST")
                    status_info["gesture"] = "PINCH / SELECT"
                else:
                    self.pinch_active = False

                # 2. Hand Translation Delta -> Object Rotation
                current_x = index_mcp.x
                current_y = index_mcp.y

                if self.prev_hand_pos is not None:
                    dx = current_x - self.prev_hand_pos[0]
                    dy = current_y - self.prev_hand_pos[1]

                    # Threshold to suppress natural hand jitter
                    if abs(dx) > 0.003 or abs(dy) > 0.003:
                        rot_y = round(dx * self.rotation_sensitivity * 100, 2)
                        rot_x = round(-dy * self.rotation_sensitivity * 100, 2)

                        payload = {
                            "command": "ROTATE",
                            "target": "HEART",
                            "axis": "Y",
                            "value": rot_y,
                            "axis_x_value": rot_x,
                            "timestamp": current_time,
                        }

                        if current_time - self.last_sent_time >= self.send_interval:
                            self.send_command(payload)
                            self.last_sent_time = current_time

                        if not is_pinching:
                            status_info["gesture"] = f"ROTATE (dY: {rot_y:+.1f}, dX: {rot_x:+.1f})"
                        status_info["rotation_delta"] = rot_y

                self.prev_hand_pos = (current_x, current_y)

        else:
            self.prev_hand_pos = None

        return status_info

    def draw_landmarks(self, frame: cv2.Mat, landmarks: list) -> None:
        """Draw Task API landmarks with safe bounds checking."""
        height, width, _ = frame.shape
        points = [(int(point.x * width), int(point.y * height)) for point in landmarks]
        for start, end in self.HAND_CONNECTIONS:
            if start < len(points) and end < len(points):
                cv2.line(frame, points[start], points[end], (0, 210, 255), 2, cv2.LINE_AA)
        for point in points:
            cv2.circle(frame, point, 3, (255, 255, 255), -1, cv2.LINE_AA)

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
        gesture_label = f"GESTURE: {status.get('gesture', 'IDLE')}"
        pinch_color = (0, 255, 100) if status.get("pinch") else (180, 180, 180)
        cv2.putText(
            frame,
            gesture_label,
            (18, 60),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            pinch_color,
            2,
        )

        # UDP Status & Controls info
        udp_status = f"UDP Stream: {self.udp_ip}:{self.udp_port} [ACTIVE]"
        cv2.putText(
            frame,
            udp_status,
            (18, h - 18),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.45,
            (200, 220, 240),
            1,
        )

        ctrl_text = "Press 'Q' to Exit | 'R' Reset | 'H' Overlay"
        cv2.putText(
            frame,
            ctrl_text,
            (w - 380, h - 18),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.45,
            (160, 180, 200),
            1,
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

        # Periodic simulated rotation/pinch
        phase = int(t) % 6
        if phase in [0, 1]:
            gesture_name = f"ROTATING (+{abs(math.sin(t))*25:.1f} deg)"
            status = {"gesture": gesture_name, "pinch": False}
            if time.time() - self.last_sent_time >= 0.5:
                self.send_command({"command": "ROTATE", "target": "HEART", "axis": "Y", "value": 5.0})
                self.last_sent_time = time.time()
        elif phase in [2, 3]:
            gesture_name = "PINCH / SELECT (NEAREST)"
            status = {"gesture": gesture_name, "pinch": True}
            cv2.circle(frame, (cx, cy), 30, (0, 255, 100), 2, cv2.LINE_AA)
        else:
            status = {"gesture": "TRACKING ACTIVE", "pinch": False}

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
                    status: Dict[str, Any] = {"gesture": "SEARCHING HANDS", "pinch": False}
                    if results.hand_landmarks:
                        if self.show_overlay:
                            for hand_landmarks in results.hand_landmarks:
                                self.draw_landmarks(frame, hand_landmarks)

                        status = self.process_gestures(results.hand_landmarks, w, h)
                    else:
                        self.prev_hand_pos = None
                        self.prev_two_hand_dist = None

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
                cv2.imshow("HoloMed AI - Hand Tracking Preview", frame)

                key = cv2.waitKey(1) & 0xFF
                if key == ord("q"):
                    print("[HoloMed Vision] Exiting vision loop.")
                    break
                elif key == ord("h"):
                    self.show_overlay = not self.show_overlay
                    print(f"[HoloMed Vision] Presentation Overlay: {'ENABLED' if self.show_overlay else 'HIDDEN'}")
                elif key == ord("r"):
                    reset_payload = {"command": "RESET", "target": "HEART"}
                    self.send_command(reset_payload)
                    print("[HoloMed Vision] >> Manual Reset Triggered")

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
