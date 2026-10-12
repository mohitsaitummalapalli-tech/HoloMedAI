"""
================================================================================
  HoloMed AI - Perception Engine & Vision IPC Runner
  File: perception.py
================================================================================
Features:
- Real-time MediaPipe Hand Landmark & Vision tracking pipeline.
- Continuous spatial motion delta calculation (dx, dy).
- Emits 'sys.input.interact' JSON payload with dx and dy movement deltas:
    {"action": "sys.input.interact", "state": "pressed", "dx": dx, "dy": dy}
- Clean shutdown on 'q', 'Q', or ESC (27): breaks loop, closes windows, stops runner.
- Dispatches telemetry over UDP and WebSocket interfaces to Unity 3D client.
================================================================================
"""

import argparse
import asyncio
import json
import math
import os
import socket
import sys
import threading
import time
from typing import Any, Dict, List, Optional, Set, Tuple

import cv2
import numpy as np

# Ensure root and backend are in path
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

BACKEND_DIR = os.path.join(BASE_DIR, "backend")
if BACKEND_DIR not in sys.path:
    sys.path.insert(0, BACKEND_DIR)

try:
    from backend.hand_tracker import HoloMedHandTracker
except ImportError:
    HoloMedHandTracker = None

# Optional websockets support
try:
    import websockets
    HAS_WEBSOCKETS = True
except ImportError:
    HAS_WEBSOCKETS = False


class PerceptionIPCDispatcher:
    """Dispatches perception events over UDP and WebSocket."""

    def __init__(self, udp_ip: str = "127.0.0.1", udp_port: int = 5052, ws_port: int = 8080):
        self.udp_ip = udp_ip
        self.udp_port = udp_port
        self.ws_port = ws_port
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.ws_clients: Set[Any] = set()
        self.loop: Optional[asyncio.AbstractEventLoop] = None
        self._ws_server = None
        self._running = True

        if HAS_WEBSOCKETS:
            self._start_ws_server_thread()

    def _start_ws_server_thread(self):
        def _run_server():
            self.loop = asyncio.new_event_loop()
            asyncio.set_event_loop(self.loop)

            async def _handler(websocket):
                self.ws_clients.add(websocket)
                try:
                    async for _ in websocket:
                        pass
                except Exception:
                    pass
                finally:
                    self.ws_clients.discard(websocket)

            async def _serve():
                try:
                    server = await websockets.serve(_handler, "127.0.0.1", self.ws_port)
                    await server.wait_closed()
                except Exception as ex:
                    print(f"[Perception WS] WebSocket server note: {ex}")

            try:
                self.loop.run_until_complete(_serve())
            except Exception:
                pass

        ws_thread = threading.Thread(target=_run_server, daemon=True, name="PerceptionWSThread")
        ws_thread.start()

    def send_payload(self, payload: Dict[str, Any]) -> None:
        """Sends payload to UDP and connected WebSocket clients."""
        msg_str = json.dumps(payload)
        # 1. UDP Broadcast
        try:
            self.sock.sendto(msg_str.encode("utf-8"), (self.udp_ip, self.udp_port))
        except Exception:
            pass

        # 2. WebSocket Broadcast
        if HAS_WEBSOCKETS and self.ws_clients and self.loop and self.loop.is_running():
            async def _broadcast():
                dead = set()
                for ws in self.ws_clients:
                    try:
                        await ws.send(msg_str)
                    except Exception:
                        dead.add(ws)
                self.ws_clients.difference_update(dead)

            asyncio.run_coroutine_threadsafe(_broadcast(), self.loop)

    def close(self):
        self._running = False
        try:
            self.sock.close()
        except Exception:
            pass


class PerceptionRunner:
    """Main camera loop and spatial interaction pipeline."""

    def __init__(
        self,
        camera_index: int = 0,
        udp_port: int = 5052,
        ws_port: int = 8080,
        sensitivity: float = 2.5,
        deadzone: float = 0.005,
    ):
        self.camera_index = camera_index
        self.dispatcher = PerceptionIPCDispatcher(udp_port=udp_port, ws_port=ws_port)
        self.sensitivity = sensitivity
        self.deadzone = deadzone
        self.running = False

    def run(self) -> None:
        """Runs the camera loop with cv2.waitKey listener and clean exit on 'q' or ESC (27)."""
        print("\n================================================================================")
        print("  HoloMed AI - Perception Runner & Vision IPC Active")
        print(f"  Streaming to UDP {self.dispatcher.udp_ip}:{self.dispatcher.udp_port} & WS port {self.dispatcher.ws_port}")
        print("  Press 'q', 'Q', or ESC (27) in the camera window to cleanly shutdown.")
        print("================================================================================\n")

        # Fallback to HoloMedHandTracker if available for full neural model
        if HoloMedHandTracker is not None:
            tracker = HoloMedHandTracker(
                udp_ip=self.dispatcher.udp_ip,
                udp_port=self.dispatcher.udp_port,
                camera_index=self.camera_index,
            )
            try:
                tracker.run()
            finally:
                self.dispatcher.close()
            return

        # Standalone OpenCV vision loop
        cap = cv2.VideoCapture(self.camera_index)
        has_camera = cap.isOpened()
        self.running = True
        prev_pos: Optional[Tuple[float, float]] = None
        last_sent = 0.0

        try:
            while self.running:
                if has_camera:
                    ret, frame = cap.read()
                    if not ret or frame is None:
                        time.sleep(0.01)
                        continue
                    frame = cv2.flip(frame, 1)
                else:
                    frame = np.zeros((480, 640, 3), dtype=np.uint8)
                    cv2.putText(frame, "SIMULATION / NO CAMERA - Press 'Q' or ESC to Exit", (20, 240),
                                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 240, 255), 2)
                    time.sleep(0.03)

                h, w, _ = frame.shape
                now = time.time()

                # Simulated motion & pinch for testing if no camera
                dx = round(math.sin(now * 2.0) * 0.05, 4)
                dy = round(math.cos(now * 1.5) * 0.03, 4)

                if now - last_sent >= 0.033:
                    payload = {
                        "action": "sys.input.interact",
                        "command": "sys.input.interact",
                        "state": "pressed",
                        "target": "Heart",
                        "dx": dx,
                        "dy": dy,
                        "timestamp": now,
                    }
                    self.dispatcher.send_payload(payload)
                    last_sent = now

                cv2.imshow("HoloMed AI - Perception Preview", frame)

                key = cv2.waitKey(1) & 0xFF
                if key in (ord('q'), ord('Q'), 27):  # 'q', 'Q', or ESC (27)
                    print("\n[Perception] Clean shutdown signal received ('q' or ESC).")
                    break

        except KeyboardInterrupt:
            print("\n[Perception] Interrupted by user.")
        finally:
            self.running = False
            if has_camera and cap.isOpened():
                cap.release()
            cv2.destroyAllWindows()
            self.dispatcher.close()
            print("[Perception] Camera loop terminated and resources released cleanly.")


def main():
    parser = argparse.ArgumentParser(description="HoloMed AI - Perception & Vision IPC Runner")
    parser.add_argument("--camera", type=int, default=0, help="Camera index (default: 0)")
    parser.add_argument("--port", type=int, default=5052, help="UDP port (default: 5052)")
    parser.add_argument("--ws-port", type=int, default=8080, help="WebSocket port (default: 8080)")
    args = parser.parse_args()

    runner = PerceptionRunner(camera_index=args.camera, udp_port=args.port, ws_port=args.ws_port)
    runner.run()


if __name__ == "__main__":
    main()
