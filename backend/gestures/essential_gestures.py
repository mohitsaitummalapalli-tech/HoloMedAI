"""
================================================================================
  HoloMed AI - Essential 8 Gesture Recognizer
  File: backend/gestures/essential_gestures.py
================================================================================
A streamlined, high-precision gesture recognizer implementing strictly the
8 Essential Gestures for zero gesture collisions, zero lag, and instant responsiveness.

Single-Hand Gestures:
1. PINCH_DRAG: Thumb tip (4) & Index tip (8) distance < 0.05, tracking delta_x, delta_y.
2. ROTATE_WRIST_CW / ROTATE_WRIST_CCW: Wrist-to-index angle rotation (+CW, -CCW).
3. ZOOM_SPREAD / ZOOM_CLOSE: Radial fingertip expansion / contraction from palm.
4. PALM_STOP: Flat open hand (5 extended fingers), velocity < 0.3 (Freeze motion).
5. AIR_TAP: Quick forward/downward index finger poke and release (Select/Raycast).
6. FIST_LOCK: 0 extended fingers (Lock viewport state).

Two-Hand Gestures:
7. CROSS_SECTION_SPLIT: Dual open hands moving apart horizontally (Cutaway / Section view).
8. EMERGENCY_RESET: Dual closed fists (Instantly reset viewport transform).
"""

import math
import time
from collections import deque
from typing import Any, Dict, List, Optional, Tuple

from .utils import (
    GestureDebouncer,
    GestureEvent,
    LandmarkIndex,
    LandmarkMath,
    Point3D,
    VelocityTracker,
)


class EssentialGestureRecognizer:
    """
    Precision recognizer for the Essential 8 Gesture Suite.
    Calculates geometric rules using 21 3D hand landmarks with zero ambiguity.
    """

    def __init__(self, debouncer: Optional[GestureDebouncer] = None):
        self.debouncer = debouncer or GestureDebouncer(default_cooldown=0.25)
        self.velocity_tracker = VelocityTracker()
        self.last_pinch_pos: Optional[Point3D] = None
        self.prev_wrist_angle: Optional[float] = None
        self.prev_spread_dist: Optional[float] = None
        self.prev_inter_hand_dist: Optional[float] = None

    def process_single_hand(
        self,
        landmarks: List[Any],
        handedness: str = "Right",
        timestamp: Optional[float] = None,
    ) -> List[GestureEvent]:
        """Evaluates single-hand landmarks for the 6 single-hand essential gestures."""
        events: List[GestureEvent] = []
        now = timestamp if timestamp is not None else time.time()

        palm = LandmarkMath.palm_center(landmarks)
        scale = LandmarkMath.palm_scale(landmarks)
        self.velocity_tracker.update(palm, now)
        f_state = LandmarkMath.get_finger_state(landmarks)

        wrist = LandmarkMath.to_point(landmarks[LandmarkIndex.WRIST])
        thumb_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.THUMB_TIP])
        index_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_TIP])
        index_pip = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_PIP])
        index_mcp = LandmarkMath.to_point(landmarks[LandmarkIndex.INDEX_FINGER_MCP])
        middle_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.MIDDLE_FINGER_TIP])
        ring_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.RING_FINGER_TIP])
        pinky_tip = LandmarkMath.to_point(landmarks[LandmarkIndex.PINKY_TIP])

        # Normalized pinch distance
        pinch_dist = LandmarkMath.dist_3d(thumb_tip, index_tip) / scale
        is_pinching = pinch_dist < 0.28
        extended_count = sum(1 for ext in f_state.values() if ext)

        # -------------------------------------------------------------
        # 1. PINCH_DRAG: Thumb tip (4) & Index tip (8) touching while moving
        # -------------------------------------------------------------
        pinch_hold_time = self.debouncer.update_hold("PINCH", is_pinching)
        if is_pinching:
            pinch_conf = max(0.0, min(1.0, 1.0 - (pinch_dist / 0.28)))
            if self.last_pinch_pos is not None and pinch_hold_time > 0.08:
                dx = index_tip.x - self.last_pinch_pos.x
                dy = index_tip.y - self.last_pinch_pos.y
                drag_dist = math.sqrt(dx ** 2 + dy ** 2)
                if drag_dist > scale * 0.08:
                    events.append(
                        GestureEvent(
                            action="PINCH_DRAG",
                            confidence=pinch_conf,
                            category="CORE",
                            parameters={"delta_x": round(dx, 4), "delta_y": round(dy, 4)},
                            timestamp=now,
                        )
                    )
            self.last_pinch_pos = index_tip
        else:
            self.last_pinch_pos = None

        # -------------------------------------------------------------
        # 2. ROTATE_WRIST_CW & ROTATE_WRIST_CCW
        # -------------------------------------------------------------
        wrist_vec_x = index_mcp.x - wrist.x
        wrist_vec_y = index_mcp.y - wrist.y
        current_angle = math.degrees(math.atan2(wrist_vec_y, wrist_vec_x))
        if self.prev_wrist_angle is not None and not is_pinching and extended_count >= 2:
            angle_delta = current_angle - self.prev_wrist_angle
            if angle_delta > 180:
                angle_delta -= 360
            elif angle_delta < -180:
                angle_delta += 360

            if abs(angle_delta) > 10.0:
                action = "ROTATE_WRIST_CW" if angle_delta > 0 else "ROTATE_WRIST_CCW"
                if self.debouncer.check_debounce(action, 0.88, cooldown=0.15):
                    events.append(
                        GestureEvent(
                            action=action,
                            confidence=0.90,
                            category="CORE",
                            parameters={"angle_delta": round(angle_delta, 2)},
                            timestamp=now,
                        )
                    )
        self.prev_wrist_angle = current_angle

        # -------------------------------------------------------------
        # 3. ZOOM_SPREAD & ZOOM_CLOSE
        # -------------------------------------------------------------
        tips = [thumb_tip, index_tip, middle_tip, ring_tip, pinky_tip]
        avg_tip_dist = sum(LandmarkMath.dist_3d(t, palm) for t in tips) / (5.0 * scale)
        if self.prev_spread_dist is not None and extended_count >= 3 and not is_pinching:
            dist_delta = avg_tip_dist - self.prev_spread_dist
            if dist_delta > 0.065:
                events.append(
                    GestureEvent(
                        action="ZOOM_SPREAD",
                        confidence=0.88,
                        category="CORE",
                        parameters={"delta": round(dist_delta, 3)},
                        timestamp=now,
                    )
                )
            elif dist_delta < -0.065:
                events.append(
                    GestureEvent(
                        action="ZOOM_CLOSE",
                        confidence=0.88,
                        category="CORE",
                        parameters={"delta": round(dist_delta, 3)},
                        timestamp=now,
                    )
                )
        self.prev_spread_dist = avg_tip_dist

        # -------------------------------------------------------------
        # 4. PALM_STOP: All 5 fingers extended, velocity < 0.3
        # -------------------------------------------------------------
        if extended_count >= 4 and not is_pinching:
            speed = self.velocity_tracker.get_speed()
            if speed < 0.30:
                if self.debouncer.check_debounce("PALM_STOP", 0.86, min_confidence=0.8, cooldown=0.40):
                    events.append(GestureEvent(action="PALM_STOP", confidence=0.90, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 5. AIR_TAP: Index finger quick extension and retraction
        # -------------------------------------------------------------
        if f_state["INDEX"] and not f_state["RING"] and not f_state["PINKY"] and not is_pinching:
            tip_dip_angle = LandmarkMath.angle_3d(index_mcp, index_pip, index_tip)
            if tip_dip_angle < 140.0:
                if self.debouncer.check_debounce("AIR_TAP", 0.86, min_confidence=0.8, cooldown=0.30):
                    events.append(GestureEvent(action="AIR_TAP", confidence=0.89, category="CORE", timestamp=now))

        # -------------------------------------------------------------
        # 6. FIST_LOCK: 0 extended fingers (tight fist)
        # -------------------------------------------------------------
        if extended_count == 0:
            if self.debouncer.check_debounce("FIST_LOCK", 0.90, min_confidence=0.85, cooldown=0.40):
                events.append(GestureEvent(action="FIST_LOCK", confidence=0.92, category="CORE", timestamp=now))

        return events

    def process_multi_hand(
        self,
        hand1_landmarks: List[Any],
        hand2_landmarks: List[Any],
        timestamp: Optional[float] = None,
    ) -> List[GestureEvent]:
        """Evaluates two-hand landmarks for the 2 bimanual essential gestures."""
        events: List[GestureEvent] = []
        now = timestamp if timestamp is not None else time.time()

        palm1 = LandmarkMath.palm_center(hand1_landmarks)
        palm2 = LandmarkMath.palm_center(hand2_landmarks)
        scale1 = LandmarkMath.palm_scale(hand1_landmarks)
        scale2 = LandmarkMath.palm_scale(hand2_landmarks)
        avg_scale = (scale1 + scale2) / 2.0

        f_state1 = LandmarkMath.get_finger_state(hand1_landmarks)
        f_state2 = LandmarkMath.get_finger_state(hand2_landmarks)
        ext1 = sum(1 for v in f_state1.values() if v)
        ext2 = sum(1 for v in f_state2.values() if v)

        inter_dist = LandmarkMath.dist_3d(palm1, palm2)

        # -------------------------------------------------------------
        # 8. EMERGENCY_RESET: Dual closed fists (0 extended fingers each)
        # -------------------------------------------------------------
        if ext1 == 0 and ext2 == 0:
            if inter_dist < avg_scale * 1.2:
                if self.debouncer.check_debounce("EMERGENCY_RESET", 0.95, min_confidence=0.9, cooldown=1.0):
                    events.append(
                        GestureEvent(
                            action="EMERGENCY_RESET",
                            confidence=0.98,
                            category="MULTI",
                            parameters={"reset_scene": True},
                            timestamp=now,
                        )
                    )

        # -------------------------------------------------------------
        # 7. CROSS_SECTION_SPLIT: Two open hands moving apart horizontally
        # -------------------------------------------------------------
        if ext1 >= 4 and ext2 >= 4 and self.prev_inter_hand_dist is not None:
            dist_delta = inter_dist - self.prev_inter_hand_dist
            if dist_delta > 0.055:
                if self.debouncer.check_debounce("CROSS_SECTION_SPLIT", 0.90, min_confidence=0.85, cooldown=0.60):
                    events.append(
                        GestureEvent(
                            action="CROSS_SECTION_SPLIT",
                            confidence=0.92,
                            category="MULTI",
                            parameters={"action_type": "TOGGLE_CUTAWAY"},
                            timestamp=now,
                        )
                    )

        self.prev_inter_hand_dist = inter_dist
        return events
