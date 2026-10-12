"""
HoloMed AI - Gesture Engine Utilities & Landmark Mathematics
File: gestures/utils.py

Provides:
- 3D Vector & Euclidean distance calculations between hand landmarks.
- Landmark smoothing (Exponential Moving Average / Low-Pass).
- Temporal hand velocity tracking for swipes, taps, and wave motions.
- Per-gesture debouncing, confidence thresholding, and cooldown management.
- Thread-safe UDP broadcast client sending structured JSON telemetry to Unity.
"""

import json
import math
import socket
import time
from collections import deque
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple


# MediaPipe Hand Landmark Index Constants
class LandmarkIndex:
    WRIST = 0
    THUMB_CMC = 1
    THUMB_MCP = 2
    THUMB_IP = 3
    THUMB_TIP = 4
    INDEX_FINGER_MCP = 5
    INDEX_FINGER_PIP = 6
    INDEX_FINGER_DIP = 7
    INDEX_FINGER_TIP = 8
    MIDDLE_FINGER_MCP = 9
    MIDDLE_FINGER_PIP = 10
    MIDDLE_FINGER_DIP = 11
    MIDDLE_FINGER_TIP = 12
    RING_FINGER_MCP = 13
    RING_FINGER_PIP = 14
    RING_FINGER_DIP = 15
    RING_FINGER_TIP = 16
    PINKY_MCP = 17
    PINKY_PIP = 18
    PINKY_DIP = 19
    PINKY_TIP = 20


@dataclass
class Point3D:
    x: float
    y: float
    z: float = 0.0


@dataclass
class GestureEvent:
    action: str
    confidence: float
    category: str = "CORE"
    parameters: Optional[Dict[str, Any]] = None
    timestamp: float = 0.0

    def to_json(self) -> str:
        payload = {
            "type": "GESTURE",
            "action": self.action,
            "confidence": round(self.confidence, 3),
            "category": self.category,
            "timestamp": round(self.timestamp if self.timestamp > 0 else time.time(), 3),
        }
        if self.parameters:
            payload["params"] = self.parameters
        return json.dumps(payload, separators=(",", ":"))


class LandmarkMath:
    """Mathematical utility functions for 3D hand landmarks."""

    @staticmethod
    def to_point(lm: Any) -> Point3D:
        """Convert MediaPipe landmark or dict/list to Point3D."""
        if hasattr(lm, "x") and hasattr(lm, "y"):
            z = getattr(lm, "z", 0.0)
            return Point3D(float(lm.x), float(lm.y), float(z))
        elif isinstance(lm, (list, tuple)):
            z = lm[2] if len(lm) > 2 else 0.0
            return Point3D(float(lm[0]), float(lm[1]), float(z))
        elif isinstance(lm, dict):
            return Point3D(float(lm.get("x", 0.0)), float(lm.get("y", 0.0)), float(lm.get("z", 0.0)))
        return Point3D(0.0, 0.0, 0.0)

    @staticmethod
    def dist_3d(p1: Any, p2: Any) -> float:
        """Calculate 3D Euclidean distance between two points."""
        pt1 = LandmarkMath.to_point(p1)
        pt2 = LandmarkMath.to_point(p2)
        return math.sqrt((pt1.x - pt2.x) ** 2 + (pt1.y - pt2.y) ** 2 + (pt1.z - pt2.z) ** 2)

    @staticmethod
    def dist_2d(p1: Any, p2: Any) -> float:
        """Calculate 2D Euclidean distance (X, Y) between two points."""
        pt1 = LandmarkMath.to_point(p1)
        pt2 = LandmarkMath.to_point(p2)
        return math.sqrt((pt1.x - pt2.x) ** 2 + (pt1.y - pt2.y) ** 2)

    @staticmethod
    def angle_3d(p1: Any, p2: Any, p3: Any) -> float:
        """Calculate angle at p2 formed by lines p2-p1 and p2-p3 in degrees."""
        a = LandmarkMath.to_point(p1)
        b = LandmarkMath.to_point(p2)
        c = LandmarkMath.to_point(p3)

        v1 = (a.x - b.x, a.y - b.y, a.z - b.z)
        v2 = (c.x - b.x, c.y - b.y, c.z - b.z)

        dot = v1[0] * v2[0] + v1[1] * v2[1] + v1[2] * v2[2]
        mag1 = math.sqrt(v1[0] ** 2 + v1[1] ** 2 + v1[2] ** 2)
        mag2 = math.sqrt(v2[0] ** 2 + v2[1] ** 2 + v2[2] ** 2)

        if mag1 * mag2 == 0:
            return 0.0
        cos_angle = max(-1.0, min(1.0, dot / (mag1 * mag2)))
        return math.degrees(math.acos(cos_angle))

    @staticmethod
    def palm_center(landmarks: List[Any]) -> Point3D:
        """Compute the centroid of the palm (Wrist, Index MCP, Middle MCP, Ring MCP, Pinky MCP)."""
        indices = [
            LandmarkIndex.WRIST,
            LandmarkIndex.INDEX_FINGER_MCP,
            LandmarkIndex.MIDDLE_FINGER_MCP,
            LandmarkIndex.RING_FINGER_MCP,
            LandmarkIndex.PINKY_MCP,
        ]
        pts = [LandmarkMath.to_point(landmarks[i]) for i in indices]
        return Point3D(
            sum(p.x for p in pts) / len(pts),
            sum(p.y for p in pts) / len(pts),
            sum(p.z for p in pts) / len(pts),
        )

    @staticmethod
    def palm_scale(landmarks: List[Any]) -> float:
        """Compute hand size metric based on Wrist to Middle MCP distance for normalization."""
        wrist = landmarks[LandmarkIndex.WRIST]
        middle_mcp = landmarks[LandmarkIndex.MIDDLE_FINGER_MCP]
        return max(0.01, LandmarkMath.dist_3d(wrist, middle_mcp))

    @staticmethod
    def normalized_dist(landmarks: List[Any], idx1: int, idx2: int) -> float:
        """Compute scale-invariant distance normalized by hand size."""
        raw_dist = LandmarkMath.dist_3d(landmarks[idx1], landmarks[idx2])
        scale = LandmarkMath.palm_scale(landmarks)
        return raw_dist / scale

    @staticmethod
    def is_finger_extended(landmarks: List[Any], finger: str) -> bool:
        """
        Determine if a finger is extended by checking tip distance from wrist relative to PIP/MCP.
        finger: 'THUMB', 'INDEX', 'MIDDLE', 'RING', 'PINKY'
        """
        wrist = landmarks[LandmarkIndex.WRIST]
        scale = LandmarkMath.palm_scale(landmarks)

        if finger == "THUMB":
            tip = landmarks[LandmarkIndex.THUMB_TIP]
            ip = landmarks[LandmarkIndex.THUMB_IP]
            mcp = landmarks[LandmarkIndex.THUMB_MCP]
            # Thumb tip is further from pinky MCP and wrist than IP
            pinky_mcp = landmarks[LandmarkIndex.PINKY_MCP]
            return (
                LandmarkMath.dist_3d(tip, pinky_mcp) > LandmarkMath.dist_3d(ip, pinky_mcp)
                and LandmarkMath.dist_3d(tip, wrist) > LandmarkMath.dist_3d(mcp, wrist) * 1.05
            )
        elif finger == "INDEX":
            tip = landmarks[LandmarkIndex.INDEX_FINGER_TIP]
            pip = landmarks[LandmarkIndex.INDEX_FINGER_PIP]
            mcp = landmarks[LandmarkIndex.INDEX_FINGER_MCP]
            return LandmarkMath.dist_3d(tip, wrist) > LandmarkMath.dist_3d(pip, wrist) and LandmarkMath.dist_3d(tip, mcp) > scale * 0.7
        elif finger == "MIDDLE":
            tip = landmarks[LandmarkIndex.MIDDLE_FINGER_TIP]
            pip = landmarks[LandmarkIndex.MIDDLE_FINGER_PIP]
            mcp = landmarks[LandmarkIndex.MIDDLE_FINGER_MCP]
            return LandmarkMath.dist_3d(tip, wrist) > LandmarkMath.dist_3d(pip, wrist) and LandmarkMath.dist_3d(tip, mcp) > scale * 0.7
        elif finger == "RING":
            tip = landmarks[LandmarkIndex.RING_FINGER_TIP]
            pip = landmarks[LandmarkIndex.RING_FINGER_PIP]
            mcp = landmarks[LandmarkIndex.RING_FINGER_MCP]
            return LandmarkMath.dist_3d(tip, wrist) > LandmarkMath.dist_3d(pip, wrist) and LandmarkMath.dist_3d(tip, mcp) > scale * 0.7
        elif finger == "PINKY":
            tip = landmarks[LandmarkIndex.PINKY_TIP]
            pip = landmarks[LandmarkIndex.PINKY_PIP]
            mcp = landmarks[LandmarkIndex.PINKY_MCP]
            return LandmarkMath.dist_3d(tip, wrist) > LandmarkMath.dist_3d(pip, wrist) and LandmarkMath.dist_3d(tip, mcp) > scale * 0.7
        return False

    @staticmethod
    def get_finger_state(landmarks: List[Any]) -> Dict[str, bool]:
        """Returns extension state dictionary for all 5 fingers."""
        return {
            "THUMB": LandmarkMath.is_finger_extended(landmarks, "THUMB"),
            "INDEX": LandmarkMath.is_finger_extended(landmarks, "INDEX"),
            "MIDDLE": LandmarkMath.is_finger_extended(landmarks, "MIDDLE"),
            "RING": LandmarkMath.is_finger_extended(landmarks, "RING"),
            "PINKY": LandmarkMath.is_finger_extended(landmarks, "PINKY"),
        }


class LandmarkSmoother:
    """Exponential Moving Average (EMA) smoother for jitter reduction."""

    def __init__(
        self,
        alpha: float = 0.60,
        landmark_alphas: Optional[Dict[int, float]] = None,
    ):
        self.alpha = alpha
        # Specifically enforce alpha = 0.65 for Landmark 0 (wrist) and Landmark 8 (index tip)
        self.landmark_alphas: Dict[int, float] = (
            landmark_alphas
            if landmark_alphas is not None
            else {
                LandmarkIndex.WRIST: 0.65,             # Landmark 0: Wrist
                LandmarkIndex.INDEX_FINGER_TIP: 0.65,  # Landmark 8: Index Tip
            }
        )
        self.smoothed_landmarks: Optional[List[Point3D]] = None

    def smooth(self, raw_landmarks: List[Any]) -> List[Point3D]:
        current_pts = [LandmarkMath.to_point(lm) for lm in raw_landmarks]
        if self.smoothed_landmarks is None or len(self.smoothed_landmarks) != len(current_pts):
            self.smoothed_landmarks = current_pts
            return self.smoothed_landmarks

        smoothed = []
        for idx, (curr, prev) in enumerate(zip(current_pts, self.smoothed_landmarks)):
            # Enforce landmark-specific alpha (e.g. 0.65 for wrist/index tip), else default alpha
            a = self.landmark_alphas.get(idx, self.alpha)
            sx = a * curr.x + (1.0 - a) * prev.x
            sy = a * curr.y + (1.0 - a) * prev.y
            sz = a * curr.z + (1.0 - a) * prev.z
            smoothed.append(Point3D(sx, sy, sz))
        self.smoothed_landmarks = smoothed
        return self.smoothed_landmarks

    def reset(self):
        self.smoothed_landmarks = None


class VelocityTracker:
    """Tracks hand motion dynamics (velocity dx/dt, dy/dt, speed) for swipes and taps."""

    def __init__(self, max_samples: int = 15, max_history_sec: float = 0.4):
        self.history: deque = deque(maxlen=max_samples)
        self.max_history_sec = max_history_sec

    def update(self, point: Point3D, timestamp: Optional[float] = None) -> None:
        ts = timestamp if timestamp is not None else time.time()
        self.history.append((ts, point))
        # Evict old samples
        while len(self.history) > 1 and (ts - self.history[0][0]) > self.max_history_sec:
            self.history.popleft()

    def get_velocity(self) -> Tuple[float, float, float]:
        """Compute displacement / dt vector (vx, vy, vz)."""
        if len(self.history) < 2:
            return (0.0, 0.0, 0.0)
        t_start, p_start = self.history[0]
        t_end, p_end = self.history[-1]
        dt = max(0.001, t_end - t_start)
        vx = (p_end.x - p_start.x) / dt
        vy = (p_end.y - p_start.y) / dt
        vz = (p_end.z - p_start.z) / dt
        return (vx, vy, vz)

    def get_speed(self) -> float:
        vx, vy, vz = self.get_velocity()
        return math.sqrt(vx ** 2 + vy ** 2 + vz ** 2)

    def detect_swipe(self, min_speed: float = 1.0) -> Optional[str]:
        """Detect SWIPE_LEFT, SWIPE_RIGHT, SWIPE_UP, SWIPE_DOWN."""
        vx, vy, _ = self.get_velocity()
        speed = math.sqrt(vx ** 2 + vy ** 2)
        if speed < min_speed:
            return None

        # Determine dominant axis
        if abs(vx) > abs(vy) * 1.3:
            return "SWIPE_RIGHT" if vx > 0 else "SWIPE_LEFT"
        elif abs(vy) > abs(vx) * 1.3:
            return "SWIPE_DOWN" if vy > 0 else "SWIPE_UP"
        return None

    def reset(self):
        self.history.clear()


class GestureDebouncer:
    """Manages cooldowns, consecutive activation frames, and state transitions."""

    def __init__(self, default_cooldown: float = 0.45):
        self.default_cooldown = default_cooldown
        self.last_trigger_times: Dict[str, float] = {}
        self.active_holds: Dict[str, float] = {}
        self.frame_counters: Dict[str, int] = {}

    def is_ready(self, action: str, cooldown: Optional[float] = None) -> bool:
        cd = cooldown if cooldown is not None else self.default_cooldown
        last_t = self.last_trigger_times.get(action, 0.0)
        return (time.time() - last_t) >= cd

    def trigger(self, action: str) -> None:
        self.last_trigger_times[action] = time.time()

    def update_hold(self, action: str, is_active: bool) -> float:
        """Track how many seconds a continuous gesture state is held."""
        now = time.time()
        if is_active:
            if action not in self.active_holds:
                self.active_holds[action] = now
            return now - self.active_holds[action]
        else:
            self.active_holds.pop(action, None)
            return 0.0

    def check_debounce(
        self,
        action: str,
        confidence: float,
        min_confidence: float = 0.7,
        cooldown: Optional[float] = None,
        min_frames: int = 5,
    ) -> bool:
        """Returns True if gesture meets confidence, frame persistence, and cooldown."""
        if confidence < min_confidence:
            self.frame_counters[action] = 0
            return False

        self.frame_counters[action] = self.frame_counters.get(action, 0) + 1
        if self.frame_counters[action] < min_frames:
            return False

        if self.is_ready(action, cooldown):
            self.trigger(action)
            return True
        return False


class GestureStateMachine:
    """
    Temporal Hysteresis Gesture State Machine.
    Enforces a minimum 5-frame temporal stability threshold before confirming
    any gesture transition (e.g. between PINCH_DRAG, ROTATE, and ZOOM).
    Eliminates tracking flicker, transitional jitter, and erratic mode switching.
    """

    def __init__(self, stability_threshold: int = 5, initial_state: str = "IDLE"):
        self.stability_threshold = stability_threshold
        self.current_state: str = initial_state
        self.candidate_state: str = initial_state
        self.candidate_frame_count: int = 0
        self.transition_history: deque = deque(maxlen=20)

    def update(self, detected_state: str) -> str:
        """
        Receives the raw candidate gesture detected in the current frame.
        Transitions current_state to candidate_state only after candidate_state
        persists for at least stability_threshold consecutive frames.
        Returns the confirmed active gesture state.
        """
        if detected_state == self.current_state:
            self.candidate_state = detected_state
            self.candidate_frame_count = 0
            return self.current_state

        if detected_state == self.candidate_state:
            self.candidate_frame_count += 1
            if self.candidate_frame_count >= self.stability_threshold:
                prev = self.current_state
                self.current_state = self.candidate_state
                self.candidate_frame_count = 0
                self.transition_history.append((time.time(), prev, self.current_state))
        else:
            self.candidate_state = detected_state
            self.candidate_frame_count = 1

        return self.current_state

    def reset(self, state: str = "IDLE"):
        self.current_state = state
        self.candidate_state = state
        self.candidate_frame_count = 0


class UDPBroadcaster:
    """High-performance UDP client broadcasting gesture telemetry to Unity."""

    def __init__(self, host: str = "127.0.0.1", port: int = 5053, rate_limit_hz: float = 60.0):
        self.host = host
        self.port = port
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.last_send_time = 0.0
        self.min_interval = 1.0 / max(1.0, rate_limit_hz)

    def send_gesture(self, event: GestureEvent) -> bool:
        now = time.time()
        if (now - self.last_send_time) < self.min_interval:
            return False

        payload_bytes = event.to_json().encode("utf-8")
        try:
            self.socket.sendto(payload_bytes, (self.host, self.port))
            self.last_send_time = now
            return True
        except OSError as e:
            print(f"[UDP BROADCAST ERROR] {e}")
            return False

    def close(self):
        try:
            self.socket.close()
        except Exception:
            pass
