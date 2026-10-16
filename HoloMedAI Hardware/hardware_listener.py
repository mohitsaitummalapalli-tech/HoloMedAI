#!/usr/bin/env python3
"""
HoloMed AI - Hardware Serial Listener
File: HoloMedAI Hardware/hardware_listener.py

Auto-detects the ESP32 COM port, connects at 115200 baud, and listens
for incoming telemetry. Parses and clearly prints JSON payloads such as
{"event":"BUTTON_PRESS", ...} received from the ESP32.
"""

import sys
import time
import json
import argparse
from datetime import datetime
import serial
import serial.tools.list_ports


def find_esp32_port():
    """Scans system serial ports and returns the candidate ESP32 port name."""
    ports = list(serial.tools.list_ports.comports())
    if not ports:
        return None

    # Common descriptors and VID/PID substrings for ESP32 USB-UART bridges
    esp_identifiers = [
        "CP210",          # Silicon Labs CP2102/CP2104 (VID 10C4)
        "CH340", "CH910", # WCH CH340/CH9102 (VID 1A86)
        "FTDI",           # FTDI FT232 (VID 0403)
        "UART",           # Generic UART bridge
        "USB Serial",
        "10C4:EA60",      # Silicon Labs CP210x Hardware ID
        "1A86:7523",      # CH340 Hardware ID
        "Silicon Labs",
    ]

    for p in ports:
        desc = (p.description or "").lower()
        hwid = (p.hwid or "").lower()
        for ident in esp_identifiers:
            if ident.lower() in desc or ident.lower() in hwid:
                return p.device

    # Fallback: if only one port exists, use it
    if len(ports) == 1:
        return ports[0].device

    return None


def list_ports():
    """Prints all active COM ports."""
    ports = list(serial.tools.list_ports.comports())
    if not ports:
        print("[!] No active COM ports found.")
        return
    print("\n--- Detected COM Ports ---")
    for p in ports:
        print(f"  • {p.device}: {p.description} (HWID: {p.hwid})")
    print("--------------------------\n")


def listen(port: str, baudrate: int = 115200):
    """Listens continuously on the given serial port."""
    print(f"\n[+] Opening serial connection to {port} @ {baudrate} baud...")
    try:
        ser = serial.Serial(port, baudrate=baudrate, timeout=1.0)
    except serial.SerialException as err:
        print(f"[-] Serial error opening {port}: {err}")
        return

    # Settle connection and discard initial garbage bytes
    time.sleep(1.0)
    ser.reset_input_buffer()
    print(f"[✓] Successfully connected to {port}.")
    print("[*] Listening for ESP32 events... (Press Ctrl+C to stop)\n")

    try:
        while True:
            line_bytes = ser.readline()
            if not line_bytes:
                continue

            try:
                line = line_bytes.decode("utf-8", errors="replace").strip()
            except Exception:
                continue

            if not line:
                continue

            timestamp = datetime.now().strftime("%H:%M:%S.%f")[:-3]

            # Detect and parse JSON payloads
            if line.startswith("{") and line.endswith("}"):
                try:
                    data = json.loads(line)
                    event_type = data.get("event", "UNKNOWN")
                    print("=" * 60)
                    print(f"[{timestamp}] ⚡ EVENT RECEIVED: {event_type}")
                    for k, v in data.items():
                        print(f"    • {k}: {v}")
                    print("=" * 60)
                    continue
                except json.JSONDecodeError:
                    pass

            # Non-JSON strings (e.g., boot messages, handshakes)
            if "ESP32_READY" in line:
                print(f"[{timestamp}] 🚀 ESP32 Boot Handshake: {line}")
            else:
                print(f"[{timestamp}] [RAW] {line}")

    except KeyboardInterrupt:
        print("\n[!] User terminated listener.")
    finally:
        if ser.is_open:
            ser.close()
            print("[✓] Serial port closed.")


def main():
    parser = argparse.ArgumentParser(description="HoloMedAI Hardware Serial Listener")
    parser.add_argument("-p", "--port", type=str, default=None, help="Explicit COM port (e.g. COM3)")
    parser.add_argument("-b", "--baud", type=int, default=115200, help="Baud rate (default: 115200)")
    parser.add_argument("--list", action="store_true", help="List active ports and exit")
    args = parser.parse_args()

    if args.list:
        list_ports()
        sys.exit(0)

    port = args.port
    if not port:
        print("[*] Detecting ESP32 COM port...")
        port = find_esp32_port()

        if not port:
            print("[!] Could not auto-detect an ESP32 port.")
            list_ports()
            print("[*] Waiting for device to be plugged in / driver attached... (Ctrl+C to abort)")
            try:
                while not port:
                    time.sleep(1.0)
                    port = find_esp32_port()
            except KeyboardInterrupt:
                print("\n[!] Exiting.")
                sys.exit(0)
            print(f"[✓] Discovered ESP32 on {port}!")

    listen(port=port, baudrate=args.baud)


if __name__ == "__main__":
    main()
