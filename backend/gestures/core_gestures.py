"""
HoloMed AI - Core Single-Hand Navigation & Manipulation Gestures
File: gestures/core_gestures.py

Strict 21-landmark geometric rules for single-hand interaction:
1. POINT_SELECT: Index extended, others curled (selection raycast).
2. PINCH_GRAB: Thumb tip (4) and Index tip (8) touching (object select/anchor).
3. PINCH_DRAG: Pinch sustained while translating position (continuous 3D drag).
4. ROTATE_WRIST_CW: Clockwise wrist rotation (+angle).
5. ROTATE_WRIST_CCW: Counter-clockwise wrist rotation (-angle).
6. ZOOM_SPREAD: 5 fingers expanding outward from palm center (zoom in).
7. ZOOM_CLOSE: 5 fingers contracting inward toward palm center (zoom out).
8. PALM_STOP: Flat open hand perpendicular to camera, low velocity (freeze/halt).
9. FIST_LOCK: All fingers curled tight into fist (lock transformation).
10. WAVE: Rapid oscillatory left/right hand motion (attention/clear).
11. SWIPE_LEFT: Fast negative horizontal hand velocity (step rotate +45°).
12. SWIPE_RIGHT: Fast positive horizontal hand velocity (step rotate -45°).
13. SWIPE_UP: Fast upward hand velocity (step pan up).
14. SWIPE_DOWN: Fast downward hand velocity (step pan down).
15. AIR_TAP: Quick forward/downward index fingertip poke and release (button tap).
16. THUMBS_UP: Thumb vertically up, 4 fingers curled (confirm / level up).
17. THUMBS_DOWN: Thumb vertically down, 4 fingers curled (cancel / level down).
18. PEACE_V: Index and middle fingers extended in 'V' shape (mode select).
19. HORNS_INSPECT: Index + Pinky extended, Middle/Ring curled (X-ray / inspection mode).
20. SHAKA_AI: Thumb + Pinky extended, 3 middle fingers curled (invoke Ultron AI copilot).
"""

import math
import time
from collections import deque
from typing import Any, Dict, List, Optional

from .utils import (
    GestureDebouncer,
    GestureEvent,
    LandmarkIndex,
    LandmarkMath,
    Point3D,
    VelocityTracker,
)


class CoreGestureRecognizer:
    """Detects primary single-hand manipulation and navigation gestures."""

    def __init__(self, debouncer: Optional[GestureDebouncer] = None):
        self.debouncer = debouncer or GestureDebouncer()
        self.velocity_tracker = VelocityTracker()
        self.tap_history: deque = deque(maxlen=4)
        self.wave_history: deque = deque(maxlen=20)
        self.last_pinch_pos: Optional[Point3D] = None
        self.prev_wrist_angle: Optional[float] = None
        self.prev_spread_dist: Optional[float] = None

    def process(
        self,
        landmarks: List[Any],
        handedness: str = "Right",
        timestamp: Optional[float] = None,
    ) -> List[GestureEvent]:
        events: List[GestureEvent] = []
        now = timestamp if timestamp is not None else time.time()

        palm = LandmarkMath.palm_center(landmarks)
        scale = LandmarkMath.palm_scale(landmarks)
        self.velocity_tracker.update(palm, now)
        f_state = LandmarkMath.get_finger_state(landmarks)

        wrist = LandmarkMath.to_point(landmarks[LandmarkIndex.WRIST])
        thumb_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.THUMB_TIP])
        thumb_mcp = LandmarkMath.to_point(landmarks[LandmarkIndex.THUMB_MCP])
        index_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_TIP])
        index_pip = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_PIP])
        index_mcp = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_MCP])
        middle_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.MIDDLE_FINGER_TIP])
        ring_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.RING_FINGER_TIP])
        pinky_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.PINKY_TIP])

        pinch_dist = LandmarkMath.dist_3d(thumb_tip, index_tip) / scale
        is_pinching = pinch_dist < 0.28
        extended_count = sum(1 for ext in f_state.values() if ext)

        # -------------------------------------------------------------
        # 1. PINCH_GRAB & PINCH_DRAG
        # -------------------------------------------------------------
        pinch_hold_time = self.debouncer.update_hold("PINCH", is_pinching)
        if is_pinching:
            pinch_conf = max(0.0, min(1.0, 1.0 - (pinch_dist / 0.28)))
            if self.last_pinch_pos is not None and pinch_hold_time > 0.10:
                drag_dist = LandmarkMath.dist_3d(index_tip, self.last_pinch_pos)
                if drag_dist > scale * 0.10:
                    dx = index_tip.x - self.last_pinch_pos.x
                    dy = index_tip.y - self.last_pinch_pos.y
                    events.append(
                        GestureEvent(
                            action="PINCH_DRAG",
                            confidence=pinch_conf,
                            category="CORE",
                            parameters={"delta_x": round(dx, 4), "delta_y": round(dy, 4)},
                            timestamp=now,
                        )
                    )
            else:
                if self.debouncer.check_debounce("PINCH_GRAB", pinch_conf, min_confidence=0.75, cooldown=0.3):
                    events.append(GestureEvent(action="PINCH_GRAB", confidence=pinch_conf, category="CORE", timestamp=now))
            self.last_pinch_pos = index_tip
        else:
            self.last_pinch_pos = None

        # -------------------------------------------------------------
        # 2. POINT_SELECT
        # -------------------------------------------------------------
        if f_state["INDEX"] and not f_state["MIDDLE"] and not f_state["RING"] and not f_state["PINKY"] and not is_pinching:
            point_dir_x = index_tip.x - index_mcp.x
            point_dir_y = index_tip.y - index_mcp.y
            if self.debouncer.check_debounce("POINT_SELECT", 0.90, min_confidence=0.8, cooldown=0.25):
                events.append(
                    GestureEvent(
                        action="POINT_SELECT",
                        confidence=0.92,
                        category="CORE",
                        parameters={"dir_x": round(point_dir_x, 3), "dir_y": round(point_dir_y, 3)},
                        timestamp=now,
                    )
                )

        # -------------------------------------------------------------
        # 3. PEACE_V
        # -------------------------------------------------------------
        if f_state["INDEX"] and f_state["MIDDLE"] and not f_state["RING"] and not f_state["PINKY"]:
            v_spread = LandmarkMath.dist_3d(index_tip, middle_tip) / scale
            if v_spread > 0.35:
                if self.debouncer.check_debounce("PEACE_V", 0.88, min_confidence=0.8, cooldown=0.45):
                    events.append(GestureEvent(action="PEACE_V", confidence=0.90, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 4. HORNS_INSPECT (Index + Pinky extended)
        # -------------------------------------------------------------
        if f_state["INDEX"] and f_state["PINKY"] and not f_state["MIDDLE"] and not f_state["RING"]:
            horn_spread = LandmarkMath.dist_3d(index_tip, pinky_tip) / scale
            if horn_spread > 0.75:
                if self.debouncer.check_debounce("HORNS_INSPECT", 0.90, min_confidence=0.85, cooldown=0.5):
                    events.append(GestureEvent(action="HORNS_INSPECT", confidence=0.92, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 5. SHAKA_AI (Thumb + Pinky extended)
        # -------------------------------------------------------------
        if f_state["THUMB"] and f_state["PINKY"] and not f_state["INDEX"] and not f_state["MIDDLE"] and not f_state["RING"]:
            shaka_spread = LandmarkMath.dist_3d(thumb_tip, pinky_tip) / scale
            if shaka_spread > 1.15:
                if self.debouncer.check_debounce("SHAKA_AI", 0.90, min_confidence=0.85, cooldown=0.5):
                    events.append(GestureEvent(action="SHAKA_AI", confidence=0.94, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 6. THUMBS_UP / THUMBS_DOWN
        # -------------------------------------------------------------
        if f_state["THUMB"] and not f_state["INDEX"] and not f_state["MIDDLE"] and not f_state["RING"] and not f_state["PINKY"]:
            dy = thumb_tip.y - thumb_mcp.y
            if dy < -scale * 0.45:  # Upward
                if self.debouncer.check_debounce("THUMBS_UP", 0.92, cooldown=0.5):
                    events.append(GestureEvent(action="THUMBS_UP", confidence=0.95, category="CORE", timestamp=now))
            elif dy > scale * 0.45:  # Downward
                if self.debouncer.check_debounce("THUMBS_DOWN", 0.92, cooldown=0.5):
                    events.append(GestureEvent(action="THUMBS_DOWN", confidence=0.95, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 7. FIST_LOCK vs PALM_STOP
        # -------------------------------------------------------------
        if extended_count == 0:
            if self.debouncer.check_debounce("FIST_LOCK", 0.90, min_confidence=0.85, cooldown=0.45):
                events.append(GestureEvent(action="FIST_LOCK", confidence=0.92, category="CORE", timestamp=now))
        elif extended_count >= 4:
            speed = self.velocity_tracker.get_speed()
            if speed < 0.35:
                if self.debouncer.check_debounce("PALM_STOP", 0.85, min_confidence=0.8, cooldown=0.45):
                    events.append(GestureEvent(action="PALM_STOP", confidence=0.88, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 8. ZOOM_SPREAD & ZOOM_CLOSE
        # -------------------------------------------------------------
        tips = [thumb_tip, index_tip, middle_tip, ring_tip, pinky_tip]
        avg_tip_dist = sum(LandmarkMath.dist_3d(t, palm) for t in tips) / (5.0 * scale)
        if self.prev_spread_dist is not None and extended_count >= 3:
            dist_delta = avg_tip_dist - self.prev_spread_dist
            if dist_delta > 0.07:
                events.append(
                    GestureEvent(
                        action="ZOOM_SPREAD",
                        confidence=0.86,
                        category="CORE",
                        parameters={"delta": round(dist_delta, 3)},
                        timestamp=now,
                    )
                )
            elif dist_delta < -0.07:
                events.append(
                    GestureEvent(
                        action="ZOOM_CLOSE",
                        confidence=0.86,
                        category="CORE",
                        parameters={"delta": round(dist_delta, 3)},
                        timestamp=now,
                    )
                )
        self.prev_spread_dist = avg_tip_dist

        # -------------------------------------------------------------
        # 9. ROTATE_WRIST_CW & ROTATE_WRIST_CCW
        # -------------------------------------------------------------
        wrist_vec_x = index_mcp.x - wrist.x
        wrist_vec_y = index_mcp.y - wrist.y
        current_angle = math.degrees(math.atan2(wrist_vec_y, wrist_vec_x))
        if self.prev_wrist_angle is not None:
            angle_delta = current_angle - self.prev_wrist_angle
            if angle_delta > 180:
                angle_delta -= 360
            elif angle_delta < -180:
                angle_delta += 360

            if abs(angle_delta) > 10.0:
                action = "ROTATE_WRIST_CW" if angle_delta > 0 else "ROTATE_WRIST_CCW"
                if self.debouncer.check_debounce(action, 0.85, cooldown=0.18):
                    events.append(
                        GestureEvent(
                            action=action,
                            confidence=0.88,
                            category="CORE",
                            parameters={"angle_delta": round(angle_delta, 2)},
                            timestamp=now,
                        )
                    )
        self.prev_wrist_angle = current_angle

        # -------------------------------------------------------------
        # 10. SWIPES: LEFT, RIGHT, UP, DOWN
        # -------------------------------------------------------------
        swipe_action = self.velocity_tracker.detect_swipe(min_speed=1.1)
        if swipe_action and self.debouncer.check_debounce(swipe_action, 0.88, min_confidence=0.8, cooldown=0.40):
            vx, vy, _ = self.velocity_tracker.get_velocity()
            events.append(
                GestureEvent(
                    action=swipe_action,
                    confidence=0.90,
                    category="CORE",
                    parameters={"velocity_x": round(vx, 2), "velocity_y": round(vy, 2)},
                    timestamp=now,
                )
            )

        # -------------------------------------------------------------
        # 11. AIR_TAP
        # -------------------------------------------------------------
        if f_state["INDEX"] and not f_state["RING"] and not f_state["PINKY"]:
            tip_dip_angle = LandmarkMath.angle_3d(index_mcp, index_pip, index_tip)
            if tip_dip_angle < 140.0:
                if self.debouncer.check_debounce("AIR_TAP", 0.85, cooldown=0.30):
                    events.append(GestureEvent(action="AIR_TAP", confidence=0.88, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 12. WAVE
        # -------------------------------------------------------------
        vx, _, _ = self.velocity_tracker.get_velocity()
        self.wave_history.append((now, vx))
        if len(self.wave_history) >= 12 and extended_count >= 4:
            zero_crossings = 0
            for i in range(1, len(self.wave_history)):
                if (self.wave_history[i - 1][1] * self.wave_history[i][1]) < -0.1:
                    zero_crossings += 1
            if zero_crossings >= 3:
                if self.debouncer.check_debounce("WAVE", 0.88, cooldown=0.6):
                    events.append(GestureEvent(action="WAVE", confidence=0.90, category="CORE", timestamp=now))
                self.wave_history.clear()

        return events
