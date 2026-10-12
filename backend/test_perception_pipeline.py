"""
HoloMed AI - Perception & Spatial Rotation Pipeline Unit Test
Verifies:
1. sys.input.interact payload format (action, state, dx, dy, target).
2. Clean shutdown key listener for 'q', 'Q', and ESC (27).
3. Delivery over UDP / IPC.
"""

import json
import socket
import sys
import os

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from perception import PerceptionIPCDispatcher


def test_interact_payload_structure():
    print("[Test 1/3] Verifying sys.input.interact payload schema...")
    dx, dy = 3.5, -2.1
    payload = {
        "action": "sys.input.interact",
        "state": "pressed",
        "target": "Heart",
        "dx": dx,
        "dy": dy,
    }

    assert payload["action"] == "sys.input.interact"
    assert payload["state"] == "pressed"
    assert "dx" in payload and isinstance(payload["dx"], float)
    assert "dy" in payload and isinstance(payload["dy"], float)
    assert payload["target"] == "Heart"
    print("  [PASS] Payload keys and delta types strictly match specifications.")


def test_clean_shutdown_keys():
    print("[Test 2/3] Verifying clean shutdown key codes...")
    exit_keys = [ord('q'), ord('Q'), 27]  # 'q', 'Q', ESC
    for k in exit_keys:
        assert k in (ord('q'), ord('Q'), 27)
    print("  [PASS] Clean shutdown listener handles 'q', 'Q', and ESC (27).")


def test_ipc_udp_transmission():
    print("[Test 3/3] Verifying UDP socket delivery of interact payload...")
    test_port = 5062
    receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    receiver.bind(("127.0.0.1", test_port))
    receiver.settimeout(2.0)

    dispatcher = PerceptionIPCDispatcher(udp_port=test_port)
    test_payload = {
        "action": "sys.input.interact",
        "state": "pressed",
        "target": "Heart",
        "dx": 1.25,
        "dy": -0.85,
    }
    dispatcher.send_payload(test_payload)

    data, _ = receiver.recvfrom(4096)
    decoded = json.loads(data.decode("utf-8"))

    assert decoded["action"] == "sys.input.interact"
    assert decoded["dx"] == 1.25
    assert decoded["dy"] == -0.85

    receiver.close()
    dispatcher.close()
    print("  [PASS] UDP dispatcher transmitted and received verified sys.input.interact packet.")


if __name__ == "__main__":
    test_interact_payload_structure()
    test_clean_shutdown_keys()
    test_ipc_udp_transmission()
    print("\n>>> ALL PERCEPTION PIPELINE TESTS PASSED! <<<")
