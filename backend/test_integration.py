"""
HoloMed AI - End-to-End Module Integration Test
File: backend/test_integration.py
"""

import json
import socket
import threading
import time
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from backend.ai_orchestrator import UltronAIOrchestrator
from backend.hardware_bridge import HoloMedHardwareBridge

TEST_PORT = 5059


def run_test():
    print("=== HoloMed AI Integration & UDP Pipeline Test ===")
    
    received_packets = []
    running = True

    # 1. Start a mock UDP Receiver server
    def udp_server():
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", TEST_PORT))
        sock.settimeout(0.5)
        while running:
            try:
                data, _ = sock.recvfrom(2048)
                packet = json.loads(data.decode("utf-8"))
                received_packets.append(packet)
            except socket.timeout:
                continue
            except Exception as e:
                pass
        sock.close()

    server_thread = threading.Thread(target=udp_server, daemon=True)
    server_thread.start()
    time.sleep(0.2)

    # 2. Test AI Orchestrator
    orchestrator = UltronAIOrchestrator(udp_port=TEST_PORT)
    queries = [
        "Show me the left ventricle",
        "Locate the aorta",
        "Cut it open",
        "Zoom in",
        "Reset model",
    ]

    for q in queries:
        orchestrator.process_command(q)
        time.sleep(0.05)

    # 3. Test Hardware Bridge
    bridge = HoloMedHardwareBridge(udp_port=TEST_PORT, simulation_mode=True)
    bridge.handle_sensor_payload('{"DISTANCE": 35.0}')
    bridge.handle_sensor_payload('{"BUTTON": "RESET"}')
    bridge.handle_sensor_payload('{"ENCODER": 20.0}')

    time.sleep(0.3)
    running = False
    server_thread.join(timeout=1.0)

    print(f"\n[Test Result] Total Packets Received over UDP: {len(received_packets)}")
    for i, pkt in enumerate(received_packets):
        cmd = pkt.get("command") or pkt.get("action")
        print(f"  Packet {i+1}: command={cmd} -> payload={pkt}")

    assert len(received_packets) >= 7, f"Expected at least 7 packets, got {len(received_packets)}"
    print("\n>>> ALL PHASE 2 TESTS PASSED SUCCESSFULLY! <<<")


if __name__ == "__main__":
    run_test()
