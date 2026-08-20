"""
================================================================================
  HoloMed AI - Next-Gen Holographic Medical AI Suite
  Main Application Launcher & System Orchestrator
================================================================================
"""

import argparse
import os
import sys
import threading
import time

# Ensure project root is in sys.path
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from backend.ai_orchestrator import UltronAIOrchestrator, run_interactive_cli
from backend.hand_tracker import HoloMedHandTracker
from backend.hardware_bridge import HoloMedHardwareBridge
from backend.test_integration import run_test


def print_banner():
    banner = r"""
================================================================================
   _  _     _       __  __          _     _    ___ 
  | || |___| |___  |  \/  |___  __| |   /_\  |_ _|
  | __ / _ \ / _ \ | |\/| / -_)/ _` |  / _ \  | | 
  |_||_\___/_\___/ |_|  |_\___|\__,_| /_/ \_\|___|
                                                   
  Holographic Medical Anatomy Visualization & Neural Gesture Engine
================================================================================
  [1] MediaPipe 3D Neural Hand Tracker (Vision HUD Window)
  [2] Ultron Medical NLP Command & Intent Orchestrator
  [3] ESP32 Microcontroller Serial / Hardware Telemetry Bridge
  [UDP Port]: 5052 -> Connected to Unity 3D Holographic Rendering Core
================================================================================
"""
    print(banner)


def launch_full_suite(camera_index: int = 0, udp_port: int = 5052):
    print("\n[HoloMed AI] Launching Full Integrated Holographic Suite...")

    # 1. Start Hardware Bridge in background thread
    print("[1/3] Initializing ESP32 Hardware Bridge...")
    bridge = HoloMedHardwareBridge(udp_port=udp_port, simulation_mode=False)
    bridge_thread = threading.Thread(target=bridge.start, daemon=True, name="HardwareBridgeThread")
    bridge_thread.start()

    # 2. Start AI Orchestrator
    print("[2/3] Initializing Ultron AI Command Orchestrator...")
    orchestrator = UltronAIOrchestrator(udp_port=udp_port)

    # Run quick diagnostics self-test
    test_queries = [
        "Show me the left ventricle",
        "Cut it open",
        "Locate the aorta",
        "Reset model",
    ]
    print("[HoloMed AI] Executing initial anatomy pipeline verification...")
    for q in test_queries:
        orchestrator.process_command(q)
        time.sleep(0.05)

    # 3. Start Hand Tracker Vision HUD (Runs on Main Thread for OpenCV GUI window)
    print("\n[3/3] Launching MediaPipe Neural Vision HUD Window...")
    print("--------------------------------------------------------------------------------")
    print("Controls: In the Vision Window:")
    print("  'Q' - Exit application")
    print("  'H' - Toggle Hologram Presentation HUD Overlay")
    print("  'R' - Send Instant Reset Command to Unity")
    print("--------------------------------------------------------------------------------")
    
    tracker = HoloMedHandTracker(udp_port=udp_port, camera_index=camera_index)
    
    try:
        tracker.run()
    except KeyboardInterrupt:
        print("\n[HoloMed AI] Terminating session...")
    finally:
        bridge.stop()
        print("[HoloMed AI] All modules stopped cleanly. Goodbye.")


def main():
    parser = argparse.ArgumentParser(description="HoloMed AI - Next-Gen Holographic Medical AI Suite")
    parser.add_argument("--mode", choices=["all", "vision", "ai", "hardware", "test"], default="all",
                        help="Execution mode: all (Full Suite), vision (Hand Tracker), ai (Orchestrator CLI), hardware (Bridge), test (Integration Test)")
    parser.add_argument("--camera", type=int, default=0, help="Camera device index (default: 0)")
    parser.add_argument("--port", type=int, default=5052, help="UDP transmission port for Unity 3D (default: 5052)")
    
    args = parser.parse_args()
    print_banner()

    if args.mode == "all":
        launch_full_suite(camera_index=args.camera, udp_port=args.port)
    elif args.mode == "vision":
        print("[HoloMed AI] Launching Hand Tracker Vision HUD...")
        tracker = HoloMedHandTracker(udp_port=args.port, camera_index=args.camera)
        tracker.run()
    elif args.mode == "ai":
        print("[HoloMed AI] Launching Ultron Medical NLP CLI...")
        run_interactive_cli()
    elif args.mode == "hardware":
        print("[HoloMed AI] Launching ESP32 Hardware Serial Bridge...")
        bridge = HoloMedHardwareBridge(udp_port=args.port)
        try:
            bridge.start()
        except KeyboardInterrupt:
            bridge.stop()
    elif args.mode == "test":
        print("[HoloMed AI] Running Automated Integration Tests...")
        run_test()


if __name__ == "__main__":
    main()
