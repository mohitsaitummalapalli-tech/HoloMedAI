"""
HoloMed AI - Backend Module 3: ESP32 Hardware Serial Bridge
File: backend/hardware_bridge.py

Features:
- Pyserial listener for ESP32 microcontrollers at 115200 baud.
- Automatic COM port discovery with plug-and-play connection.
- Graceful Mock/Simulation mode for testing when physical hardware is detached.
- Handles embedded events:
    * Ultrasonic proximity detection (User presence awake/sleep, proximity auto-zoom)
    * Physical buttons (RESET view, TOGGLE section view, SELECT)
    * Rotary encoder (Physical 3D hologram rotation)
- Forwards parsed telemetry and actions over UDP to 127.0.0.1:5052 and main backend loop.
"""

import json
import socket
import sys
import threading
import time
from typing import Optional, Dict, Any

try:
    import serial
    import serial.tools.list_ports
    SERIAL_AVAILABLE = True
except ImportError:
    SERIAL_AVAILABLE = False
    print("[HoloMed Hardware] 'pyserial' not installed. Running in simulation mode.")


class HoloMedHardwareBridge:
    def __init__(
        self,
        baud_rate: int = 115200,
        port: Optional[str] = None,
        udp_ip: str = "127.0.0.1",
        udp_port: int = 5052,
        simulation_mode: bool = False,
    ):
        self.baud_rate = baud_rate
        self.port = port
        self.udp_ip = udp_ip
        self.udp_port = udp_port
        self.simulation_mode = simulation_mode
        self.running = False
        self.serial_conn: Optional[Any] = None

        # Network Socket
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        # Hardware State Tracking
        self.user_present: bool = False
        self.last_distance_cm: float = 0.0
        self.last_button_state: int = 0

    def find_esp32_port(self) -> Optional[str]:
        """Auto-detects USB-to-UART / ESP32 serial COM port."""
        if not SERIAL_AVAILABLE:
            return None

        try:
            ports = list(serial.tools.list_ports.comports())
            for p in ports:
                # Common ESP32 USB Chips: CP210x, CH340, FTDI, USB Serial
                desc = (p.description or "").lower()
                mfg = (p.manufacturer or "").lower()
                if any(chip in desc or chip in mfg for chip in ["cp210", "ch340", "ftdi", "usb serial", "esp32", "silicon labs"]):
                    print(f"[HoloMed Hardware] Auto-detected ESP32 device on {p.device} ({p.description})")
                    return p.device

            if ports:
                print(f"[HoloMed Hardware] Defaulting to first available serial port: {ports[0].device}")
                return ports[0].device
        except Exception as e:
            print(f"[HoloMed Hardware] Port enumeration error: {e}")

        return None

    def send_udp(self, payload: Dict[str, Any]) -> None:
        """Transmits translated hardware command over UDP."""
        try:
            msg = json.dumps(payload).encode("utf-8")
            self.sock.sendto(msg, (self.udp_ip, self.udp_port))
            print(f"[HoloMed Hardware Bridge] -> UDP: {payload}")
        except Exception as e:
            print(f"[HoloMed Hardware Bridge] UDP error: {e}")

    def handle_sensor_payload(self, raw_line: str) -> None:
        """Parses serial JSON or key-value formatted telemetry from ESP32."""
        raw_line = raw_line.strip()
        if not raw_line:
            return

        try:
            # Check if ESP32 sends formatted JSON
            if raw_line.startswith("{") and raw_line.endswith("}"):
                data = json.loads(raw_line)
            else:
                # Fallback for plain text key:value formats e.g. "DIST:25", "BTN:RESET", "ROT:15"
                parts = raw_line.split(":")
                if len(parts) == 2:
                    data = {parts[0].strip().upper(): parts[1].strip()}
                else:
                    return

            # Helper for safe numeric retrieval
            def get_val(*keys):
                for k in keys:
                    if k in data and data[k] is not None:
                        return data[k]
                return None

            # Process Ultrasonic Distance
            dist_raw = get_val("DISTANCE", "DIST")
            if dist_raw is not None:
                dist_cm = float(dist_raw)
                self.last_distance_cm = dist_cm
                
                # Presence detection logic: Distance < 80cm indicates user is in front of hologram
                is_present = 5.0 < dist_cm < 80.0
                if is_present != self.user_present:
                    self.user_present = is_present
                    event_type = "USER_ENTERED" if is_present else "USER_LEFT"
                    print(f"[HoloMed Hardware] Status Event: {event_type} (Dist: {dist_cm:.1f} cm)")
                    self.send_udp({
                        "command": "PRESENCE_UPDATE",
                        "status": event_type,
                        "distance_cm": dist_cm,
                        "active": is_present
                    })

            # Process Physical Buttons
            btn_raw = get_val("BUTTON", "BTN")
            if btn_raw is not None:
                btn_action = str(btn_raw).upper()
                if btn_action in ["RESET", "1"]:
                    self.send_udp({"command": "RESET", "target": "HEART"})
                elif btn_action in ["SECTION", "2"]:
                    self.send_udp({"command": "SECTION_VIEW", "organ": "HEART", "target_part": "ALL"})
                elif btn_action in ["SELECT", "3"]:
                    self.send_udp({"command": "SELECT", "target": "NEAREST"})

            # Process Hardware Rotary Encoder
            rot_raw = get_val("ENCODER", "ROT")
            if rot_raw is not None:
                delta_rot = float(rot_raw)
                self.send_udp({
                    "command": "ROTATE",
                    "target": "HEART",
                    "axis": "Y",
                    "value": delta_rot
                })

        except Exception as e:
            print(f"[HoloMed Hardware] Line parsing error for '{raw_line}': {e}")

    def run_simulation(self) -> None:
        """Simulates periodic ESP32 sensor telemetry for testing when offline."""
        print("[HoloMed Hardware] Running in SIMULATION mode.")
        print("[HoloMed Hardware] Simulating ultrasonic presence and periodic telemetry...")
        
        sim_steps = [
            {"DIST": 45.2},
            {"BTN": "RESET"},
            {"ROT": 10.0},
            {"DIST": 30.5},
            {"BTN": "SECTION"},
        ]

        idx = 0
        while self.running:
            step = sim_steps[idx % len(sim_steps)]
            idx += 1
            raw_str = json.dumps(step)
            print(f"[HoloMed Sim ESP32] Telemetry TX: {raw_str}")
            self.handle_sensor_payload(raw_str)
            # Sleep in increments so stop() responds instantly
            for _ in range(50):
                if not self.running:
                    break
                time.sleep(0.1)

    def start(self) -> None:
        """Starts serial reading loop or fallback simulation."""
        self.running = True
        selected_port = self.port or self.find_esp32_port()

        if not selected_port or self.simulation_mode:
            print("[HoloMed Hardware] No physical ESP32 detected on serial ports.")
            self.run_simulation()
            return

        print(f"[HoloMed Hardware] Connecting to ESP32 on {selected_port} at {self.baud_rate} baud...")
        try:
            self.serial_conn = serial.Serial(selected_port, self.baud_rate, timeout=1)
            time.sleep(2)  # Wait for ESP32 boot/reset
            print("[HoloMed Hardware] Serial connection established successfully.")

            while self.running:
                if self.serial_conn.in_waiting > 0:
                    line = self.serial_conn.readline().decode("utf-8", errors="ignore").strip()
                    if line:
                        self.handle_sensor_payload(line)
                else:
                    time.sleep(0.01)

        except Exception as e:
            print(f"[HoloMed Hardware] Serial communication exception: {e}")
            print("[HoloMed Hardware] Switching to simulation fallback mode.")
            self.run_simulation()
        finally:
            if self.serial_conn and hasattr(self.serial_conn, "is_open") and self.serial_conn.is_open:
                self.serial_conn.close()
            try:
                self.sock.close()
            except Exception:
                pass
            print("[HoloMed Hardware] Hardware bridge stopped.")

    def stop(self) -> None:
        self.running = False


if __name__ == "__main__":
    bridge = HoloMedHardwareBridge()
    try:
        bridge.start()
    except KeyboardInterrupt:
        print("\n[HoloMed Hardware] Shutting down bridge.")
        bridge.stop()
