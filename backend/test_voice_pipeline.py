"""
================================================================================
  HoloMed AI - Voice & Ultron NLP Pipeline Integration Test
  File: backend/test_voice_pipeline.py
================================================================================
Verifies that:
1. VoiceListener initializes and can run on a separate background thread.
2. UltronBrain accurately parses dummy and medical voice queries into strict JSON intents.
3. VoiceListener -> UltronBrain -> Dispatcher integration successfully packages
   and transmits 'sys.input.voice' JSON packets over UDP.
"""

import os
import socket
import sys
import time

# Ensure workspace root and python folder are in sys.path
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PYTHON_DIR = os.path.join(BASE_DIR, "python")
if PYTHON_DIR not in sys.path:
    sys.path.insert(0, PYTHON_DIR)
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from holomed.input.voice import VoiceListener
from holomed.intelligence.ultron import UltronBrain


def test_intent_parsing():
    print("\n--- Test 1: UltronBrain Intent Parsing ---")
    brain = UltronBrain()

    # 1. Test requested dummy intent: "show me the heart"
    intent_heart = brain.parse_intent("show me the heart")
    assert intent_heart["intent"] == "LOAD_ORGAN", f"Expected LOAD_ORGAN, got {intent_heart['intent']}"
    assert intent_heart["organ"] == "HEART", f"Expected HEART, got {intent_heart['organ']}"
    print("  [PASS] 'show me the heart' -> LOAD_ORGAN (HEART)")

    # 2. Test rotation intent
    intent_rotate = brain.parse_intent("Ultron, rotate the model to the left")
    assert intent_rotate["intent"] == "ROTATE", f"Expected ROTATE, got {intent_rotate['intent']}"
    assert intent_rotate["direction"] == "LEFT"
    print("  [PASS] 'rotate the model to the left' -> ROTATE (LEFT)")

    # 3. Test zoom intent
    intent_zoom = brain.parse_intent("Computer, zoom in")
    assert intent_zoom["intent"] == "ZOOM"
    assert intent_zoom["delta"] > 0
    print("  [PASS] 'zoom in' -> ZOOM (delta > 0)")

    # 4. Test surgical cross-section intent
    intent_slice = brain.parse_intent("Cut open the ventricular chamber")
    assert intent_slice["intent"] == "SECTION_VIEW"
    print("  [PASS] 'cut open...' -> SECTION_VIEW")

    # 5. Test anatomical highlight intent
    intent_aorta = brain.parse_intent("Highlight the aorta")
    assert intent_aorta["intent"] == "HIGHLIGHT"
    assert intent_aorta["target_part"] == "AORTA"
    print("  [PASS] 'highlight the aorta' -> HIGHLIGHT (AORTA)")

    # 6. Test reset intent
    intent_reset = brain.parse_intent("Reset the model")
    assert intent_reset["intent"] == "RESET"
    print("  [PASS] 'reset the model' -> RESET")


def test_dispatcher_payload_formatting():
    print("\n--- Test 2: Dispatcher Payload Formatting ---")
    brain = UltronBrain()
    intent = brain.parse_intent("show me the heart")
    payload = brain.format_voice_payload(intent, raw_text="show me the heart")

    assert payload["action"] == "sys.input.voice", f"Expected action sys.input.voice, got {payload['action']}"
    assert payload["command"] == "LOAD_ORGAN"
    assert payload["organ"] == "HEART"
    assert "timestamp" in payload
    assert payload["payload"] == intent
    print(f"  [PASS] Formatted payload: {payload}")


def test_end_to_end_voice_dispatcher_pipeline():
    print("\n--- Test 3: End-to-End VoiceListener -> UltronBrain -> Dispatcher Pipeline ---")
    test_port = 5099
    rx_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rx_sock.bind(("127.0.0.1", test_port))
    rx_sock.settimeout(2.0)

    # Mock dispatcher that simulates forwarding to UDP test port
    class MockDispatcher:
        def __init__(self, target_port):
            self.target_port = target_port
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.dispatched_payloads = []

        def send_payload(self, payload):
            import json
            self.dispatched_payloads.append(payload)
            self.sock.sendto(json.dumps(payload).encode("utf-8"), ("127.0.0.1", self.target_port))

    mock_dispatcher = MockDispatcher(target_port=test_port)
    brain = UltronBrain(dispatcher=mock_dispatcher)

    # Initialize VoiceListener wired to UltronBrain
    listener = VoiceListener(on_speech_detected=brain.process_and_dispatch)

    # Start listener on background thread
    listener.start_listening()
    assert listener.is_listening, "VoiceListener should be running on background thread"
    print("  [PASS] VoiceListener running in background thread without blocking.")

    # Inject phrase simulation
    test_phrase = "Hey Ultron, show me the heart"
    listener.inject_phrase(test_phrase)

    # Receive over UDP socket
    data, _ = rx_sock.recvfrom(4096)
    import json
    received = json.loads(data.decode("utf-8"))

    assert received["action"] == "sys.input.voice"
    assert received["command"] == "LOAD_ORGAN"
    assert received["organ"] == "HEART"
    print(f"  [PASS] UDP socket received verified voice command: {received['command']} on organ {received['organ']}")

    # Clean shutdown
    listener.stop_listening()
    rx_sock.close()
    brain.close()
    assert not listener.is_listening
    print("  [PASS] VoiceListener stopped cleanly.")


if __name__ == "__main__":
    print("================================================================================")
    print("  Running HoloMed AI Category 2: Voice & Ultron NLP Pipeline Verification Test")
    print("================================================================================")
    test_intent_parsing()
    test_dispatcher_payload_formatting()
    test_end_to_end_voice_dispatcher_pipeline()
    print("\n================================================================================")
    print("  ALL CATEGORY 2 VOICE ARCHITECTURE TESTS PASSED SUCCESSFULLY!")
    print("================================================================================")
