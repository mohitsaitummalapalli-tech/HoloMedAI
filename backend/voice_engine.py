"""
================================================================================
  HoloMed AI - Real-Time Voice Command Recognition Engine (sounddevice + scipy)
  File: backend/voice_engine.py
================================================================================
Captures microphone audio using sounddevice and scipy.io.wavfile (no PyAudio dependency).
Transcribes audio via SpeechRecognition (sr.AudioFile) and streams rich medical
command JSON payloads (including clinical queries, layer toggles, and pathology states)
over UDP to Unity (Port: 5052).
"""

import argparse
import json
import os
import socket
import sys
import tempfile
import threading
import time
from typing import Any, Dict, Optional

# Audio recording via sounddevice & scipy
try:
    import sounddevice as sd
    import scipy.io.wavfile as wav
    import numpy as np
    HAS_SD = True
except ImportError:
    HAS_SD = False

# Speech recognition
try:
    import speech_recognition as sr
    HAS_SR = True
except ImportError:
    HAS_SR = False

from ai_orchestrator import UltronAIOrchestrator


class VoiceCommandEngine:
    def __init__(self, host: str = "127.0.0.1", port: int = 5052, sample_rate: int = 44100, record_duration: float = 3.5):
        self.host = host
        self.port = port
        self.sample_rate = sample_rate
        self.record_duration = record_duration
        self.is_running = True
        self.orchestrator = UltronAIOrchestrator(udp_ip=self.host, udp_port=self.port)
        self.recognizer = sr.Recognizer() if HAS_SR else None

        # Temp audio file path
        self.temp_wav = os.path.join(tempfile.gettempdir(), "holomed_temp_voice.wav")

    def record_and_transcribe(self) -> Optional[str]:
        """Records a 3.5-second audio chunk via sounddevice and transcribes it with SpeechRecognition."""
        if not HAS_SD or not HAS_SR:
            return None

        try:
            # 1. Record chunk via sounddevice
            audio_data = sd.rec(
                int(self.record_duration * self.sample_rate),
                samplerate=self.sample_rate,
                channels=1,
                dtype="int16",
            )
            sd.wait()

            # 2. Save temporarily to WAV using scipy.io.wavfile
            wav.write(self.temp_wav, self.sample_rate, audio_data)

            # 3. Transcribe using SpeechRecognition AudioFile
            with sr.AudioFile(self.temp_wav) as source:
                audio = self.recognizer.record(source)

            text = self.recognizer.recognize_google(audio)
            return text.strip()
        except sr.UnknownValueError:
            return None
        except Exception as e:
            # Audio capture / recognition exception
            return None

    def listen_microphone_loop(self) -> None:
        """Continuous background audio listening loop."""
        if not HAS_SD or not HAS_SR:
            print("[WARN] sounddevice or speech_recognition not available. Use console prompt.")
            return

        print(f"[HoloMed AI Voice] Continuous listener active ({self.record_duration}s sampling buffer)... Speak medical commands!")

        while self.is_running:
            text = self.record_and_transcribe()
            if text:
                print(f"\n[Microphone Heard]: \"{text}\"")
                self.orchestrator.process_command(text)
            time.sleep(0.1)

    def interactive_cli_loop(self) -> None:
        """Interactive keyboard command prompt."""
        print("[HoloMed AI] Console Input active. Type commands (e.g. 'What is the function of the heart?', 'Hide vascular layer', 'Show diseased state') or 'q' to quit:\n")
        while self.is_running:
            try:
                sys.stdout.write("HoloMed-Voice> ")
                sys.stdout.flush()
                line = sys.stdin.readline()
                if not line:
                    break
                text = line.strip()
                if text.lower() in ("q", "quit", "exit"):
                    self.is_running = False
                    break
                if text:
                    self.orchestrator.process_command(text)
            except (KeyboardInterrupt, EOFError):
                self.is_running = False
                break

    def run(self) -> None:
        banner = f"""
================================================================================
  HoloMed AI - Voice & Clinical Query Engine (UDP Port {self.port})
================================================================================
  - Engine: sounddevice + scipy.io.wavfile + SpeechRecognition
  - Target: {self.host}:{self.port}
================================================================================
"""
        print(banner)

        # Start microphone recording thread
        mic_thread = threading.Thread(target=self.listen_microphone_loop, daemon=True)
        mic_thread.start()

        # Run interactive CLI on main thread
        try:
            self.interactive_cli_loop()
        finally:
            self.is_running = False
            if os.path.exists(self.temp_wav):
                try:
                    os.remove(self.temp_wav)
                except Exception:
                    pass
            print("[HoloMed AI] Voice Engine stopped.")


def main():
    parser = argparse.ArgumentParser(description="HoloMed AI - Voice Command & Medical Query Engine")
    parser.add_argument("--host", type=str, default="127.0.0.1", help="Target UDP host IP (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=5052, help="Target UDP port for Unity (default: 5052)")
    parser.add_argument("--duration", type=float, default=3.5, help="Audio buffer duration in seconds (default: 3.5)")

    args = parser.parse_args()
    engine = VoiceCommandEngine(host=args.host, port=args.port, record_duration=args.duration)
    engine.run()


if __name__ == "__main__":
    main()