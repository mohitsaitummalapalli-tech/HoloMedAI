"""
HoloMed AI - Two-Hand Bimanual Cooperative Gestures
File: gestures/multi_gestures.py

Strict 21-landmark geometric rules for two-hand bimanual interaction:
1. TWO_HAND_SCALE_UP: Both hands moving apart (scale up 3D hologram).
2. TWO_HAND_SCALE_DOWN: Both hands moving closer (scale down 3D hologram).
3. TWO_HAND_ROTATE_CW: Dual palms rotating clockwise around common center.
4. TWO_HAND_ROTATE_CCW: Dual palms rotating counter-clockwise around common center.
5. TWO_HAND_PAN: Synchronous parallel translation of both hands (pan position).
6. PRECISION_ORBIT: One stationary pivot hand (fist) + one orbiting controller hand.
7. CROSS_SECTION_SPLIT: Dual open hands parting horizontally to trigger cutaway view.
8. FIST_PALM_UNLOCK: Fist meeting open flat palm (authorization / unlock gesture).
9. EMERGENCY_RESET: Dual closed fists touching/crossing (instant reset to defaults).
10. FRAME_CAPTURE: Dual 'L' framing shape (viewfinder bounding box frame capture).
"""

import math
import time
from typing import Any, Dict, List, Optional

from .utils import (
    GestureDebouncer,
    GestureEvent,
    LandmarkIndex,
    LandmarkMath,
    Point3D,
)


class MultiHandGestureRecognizer:
    """Detects 2-hand cooperative and bimanual gestures."""

    def __init__(self, debouncer: Optional[GestureDebouncer] = None):
        self.debouncer = debouncer or GestureDebouncer()
        self.prev_inter_hand_dist: Optional[float] = None
        self.prev_angle: Optional[float] = None
        self.prev_center: Optional[Point3D] = None

    def process(
        self,
        hand1_landmarks: List[Any],
        hand2_landmarks: List[Any],
        timestamp: Optional[float] = None,
    ) -> List[GestureEvent]:
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

        index1_tip = LandmarkMath.to_point(hand1_landmarks[LandmarkIndex.INDEX_FINGER_TIP])
        thumb1_tip = LandmarkMath.to_point(hand1_landmarks[LandmarkIndex.THUMB_TIP])
        index2_tip = LandmarkMath.to_point(hand2_landmarks[LandmarkIndex.INDEX_FINGER_TIP])
        thumb2_tip = LandmarkMath.to_point(hand2_landmarks[LandmarkIndex.THUMB_TIP])

        inter_dist = LandmarkMath.dist_3d(palm1, palm2)
        mutual_center = Point3D(
            (palm1.x + palm2.x) / 2.0,
            (palm1.y + palm2.y) / 2.0,
            (palm1.z + palm2.z) / 2.0,
        )

        dx = palm2.x - palm1.x
        dy = palm2.y - palm1.y
        mutual_angle = math.degrees(math.atan2(dy, dx))

        # -------------------------------------------------------------
        # 1. EMERGENCY_RESET (Dual closed fists touching or crossed)
        # -------------------------------------------------------------
        if ext1 == 0 and ext2 == 0:
            if inter_dist < avg_scale * 1.1:
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
        # 2. FIST_PALM_UNLOCK (One fist + one open flat palm)
        # -------------------------------------------------------------
        if (ext1 == 0 and ext2 >= 4) or (ext2 == 0 and ext1 >= 4):
            if inter_dist < avg_scale * 1.2:
                if self.debouncer.check_debounce("FIST_PALM_UNLOCK", 0.92, cooldown=0.8):
                    events.append(
                        GestureEvent(
                            action="FIST_PALM_UNLOCK",
                            confidence=0.95,
                            category="MULTI",
                            parameters={"authorized": True},
                            timestamp=now,
                        )
                    )

        # -------------------------------------------------------------
        # 3. FRAME_CAPTURE (Dual 'L' Framing / Viewfinder)
        # -------------------------------------------------------------
        if (
            f_state1["THUMB"] and f_state1["INDEX"] and not f_state1["RING"] and not f_state1["PINKY"]
            and f_state2["THUMB"] and f_state2["INDEX"] and not f_state2["RING"] and not f_state2["PINKY"]
        ):
            diag_dist = LandmarkMath.dist_3d(index1_tip, thumb2_tip) / avg_scale
            if 0.8 < diag_dist < 3.5:
                if self.debouncer.check_debounce("FRAME_CAPTURE", 0.90, min_confidence=0.85, cooldown=1.0):
                    events.append(
                        GestureEvent(
                            action="FRAME_CAPTURE",
                            confidence=0.93,
                            category="MULTI",
                            parameters={"capture_frame": True},
                            timestamp=now,
                        )
                    )

        # -------------------------------------------------------------
        # 4 & 5. TWO_HAND_SCALE_UP / TWO_HAND_SCALE_DOWN
        # -------------------------------------------------------------
        if self.prev_inter_hand_dist is not None:
            dist_delta = inter_dist - self.prev_inter_hand_dist
            if abs(dist_delta) > 0.025:
                if dist_delta > 0.025:
                    action = "TWO_HAND_SCALE_UP"
                    zoom_factor = 1.0 + (dist_delta * 4.0)
                else:
                    action = "TWO_HAND_SCALE_DOWN"
                    zoom_factor = max(0.2, 1.0 + (dist_delta * 4.0))

                events.append(
                    GestureEvent(
                        action=action,
                        confidence=0.88,
                        category="MULTI",
                        parameters={"zoom_factor": round(zoom_factor, 3), "delta": round(dist_delta, 4)},
                        timestamp=now,
                    )
                )

        # -------------------------------------------------------------
        # 6 & 7. TWO_HAND_ROTATE_CW / TWO_HAND_ROTATE_CCW
        # -------------------------------------------------------------
        if self.prev_angle is not None and ext1 >= 3 and ext2 >= 3:
            angle_delta = mutual_angle - self.prev_angle
            if angle_delta > 180:
                angle_delta -= 360
            elif angle_delta < -180:
                angle_delta += 360

            if abs(angle_delta) > 6.0:
                action = "TWO_HAND_ROTATE_CW" if angle_delta > 0 else "TWO_HAND_ROTATE_CCW"
                if self.debouncer.check_debounce(action, 0.85, cooldown=0.18):
                    events.append(
                        GestureEvent(
                            action=action,
                            confidence=0.88,
                            category="MULTI",
                            parameters={"angle_delta": round(angle_delta, 2)},
                            timestamp=now,
                        )
                    )

        # -------------------------------------------------------------
        # 8. TWO_HAND_PAN (Synchronous parallel translation)
        # -------------------------------------------------------------
        if self.prev_center is not None and ext1 >= 3 and ext2 >= 3:
            cdx = mutual_center.x - self.prev_center.x
            cdy = mutual_center.y - self.prev_center.y
            shift = math.sqrt(cdx ** 2 + cdy ** 2)
            if shift > 0.025:
                if self.debouncer.check_debounce("TWO_HAND_PAN", 0.85, cooldown=0.12):
                    events.append(
                        GestureEvent(
                            action="TWO_HAND_PAN",
                            confidence=0.88,
                            category="MULTI",
                            parameters={"delta_x": round(cdx, 4), "delta_y": round(cdy, 4)},
                            timestamp=now,
                        )
                    )

        # -------------------------------------------------------------
        # 9. PRECISION_ORBIT (One fist anchor + one index orbiting)
        # -------------------------------------------------------------
        if (ext1 == 0 and f_state2["INDEX"]) or (ext2 == 0 and f_state1["INDEX"]):
            if self.debouncer.check_debounce("PRECISION_ORBIT", 0.88, cooldown=0.25):
                events.append(
                    GestureEvent(
                        action="PRECISION_ORBIT",
                        confidence=0.90,
                        category="MULTI",
                        parameters={"precision_mode": True},
                        timestamp=now,
                    )
                )

        # -------------------------------------------------------------
        # 10. CROSS_SECTION_SPLIT (Dual open hands parting horizontally)
        # -------------------------------------------------------------
        if ext1 >= 4 and ext2 >= 4 and self.prev_inter_hand_dist is not None:
            if (inter_dist - self.prev_inter_hand_dist) > 0.055:
                if self.debouncer.check_debounce("CROSS_SECTION_SPLIT", 0.89, cooldown=0.6):
                    events.append(
                        GestureEvent(
                            action="CROSS_SECTION_SPLIT",
                            confidence=0.92,
                            category="MULTI",
                            parameters={"action_type": "SPLIT_CROSS_SECTION"},
                            timestamp=now,
                        )
                    )

        # Update cache
        self.prev_inter_hand_dist = inter_dist
        self.prev_angle = mutual_angle
        self.prev_center = mutual_center

        return events
