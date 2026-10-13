"""
HoloMed AI - Category 2 Voice Control Engine (ULTRON)
File: backend/voice_server.py

Features:
- Continuous background microphone listener via SpeechRecognition.
- Automatic ambient noise calibration.
- PyAudio / SoundDevice compatibility layer for cross-version audio streaming.
- Wake-word detection ("Ultron") for hands-free surgical commands.
- Transmits commands over UDP port 5053 to Unity's VoiceCommandReceiver.
"""

import json
import socket
import sys
import time
import types

# Ensure PyAudio is available; if missing, inject SoundDevice compatibility shim
try:
    import pyaudio
except ImportError:
    try:
        import sounddevice as sd

        class PyAudioShim:
            paInt16 = 8
            paFloat32 = 1

            class Stream:
                def __init__(self, stream):
                    self.stream = stream

                def read(self, num_frames, exception_on_overflow=False):
                    data, overflowed = self.stream.read(num_frames)
                    return bytes(data)

                def is_stopped(self):
                    return self.stream.stopped

                def is_active(self):
                    return self.stream.active

                def stop_stream(self):
                    self.stream.stop()

                def close(self):
                    self.stream.close()

            def __init__(self):
                pass

            def get_device_count(self):
                return len(sd.query_devices())

            def get_default_input_device_info(self):
                idx = sd.default.device[0]
                if idx is None or idx < 0:
                    idx = 1 if len(sd.query_devices()) > 1 else 0
                info = sd.query_devices(idx)
                return {
                    "index": idx,
                    "name": info["name"],
                    "defaultSampleRate": info["default_samplerate"],
                    "maxInputChannels": info["max_input_channels"],
                }

            def get_device_info_by_host_api_device_index(self, host_api, host_api_device_index):
                info = sd.query_devices(host_api_device_index)
                return {
                    "index": host_api_device_index,
                    "name": info["name"],
                    "defaultSampleRate": info["default_samplerate"],
                    "maxInputChannels": info["max_input_channels"],
                }

            def open(self, *args, **kwargs):
                rate = int(kwargs.get("rate", 16000))
                channels = int(kwargs.get("channels", 1))
                device = kwargs.get("input_device_index", None)
                stream = sd.RawInputStream(samplerate=rate, channels=channels, dtype="int16", device=device)
                stream.start()
                return self.Stream(stream)

            def terminate(self):
                pass

        pyaudio_mod = types.ModuleType("pyaudio")
        pyaudio_mod.PyAudio = PyAudioShim
        pyaudio_mod.paInt16 = 8
        pyaudio_mod.get_sample_size = lambda fmt: 2
        sys.modules["pyaudio"] = pyaudio_mod
    except Exception as ex:
        print(f"[ULTRON WARNING] Could not initialize audio shim: {ex}")

import speech_recognition as sr

# UDP Network Configuration
UDP_IP = "127.0.0.1"
UDP_PORT = 5053
WAKE_WORD = "ultron"

# ANSI Colors for holographic CLI output
CYAN = "\033[96m"
GREEN = "\033[92m"
YELLOW = "\033[93m"
BOLD = "\033[1m"
RESET = "\033[0m"


def send_udp_command(sock: socket.socket, payload: dict) -> None:
    """Serializes and sends a command JSON payload over UDP to Unity."""
    try:
        message = json.dumps(payload).encode("utf-8")
        sock.sendto(message, (UDP_IP, UDP_PORT))
    except Exception as e:
        print(f"[ULTRON ERROR] Failed to send UDP payload: {e}")


def listen_for_commands() -> None:
    """Continuous listening loop for speech recognition and Ultron commands."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    recognizer = sr.Recognizer()
    recognizer.pause_threshold = 0.8
    recognizer.dynamic_energy_threshold = True

    try:
        mic = sr.Microphone()
    except Exception as e:
        print(f"[ULTRON ERROR] Could not initialize microphone: {e}")
        return

    print(f"{CYAN}Calibrating microphone for ambient room noise...{RESET}")
    with mic as source:
        recognizer.adjust_for_ambient_noise(source, duration=1.0)
    print(f"{GREEN}Microphone calibrated. Energy threshold: {recognizer.energy_threshold:.0f}{RESET}")
    print(f"{CYAN}Listening for wake word 'Ultron' on UDP Port {UDP_PORT}...{RESET}\n")

    while True:
        try:
            with mic as source:
                print(f"{YELLOW}[ULTRON LISTENING...]{RESET}", end="\r", flush=True)
                audio = recognizer.listen(source, timeout=3.0, phrase_time_limit=6.0)

            print(" " * 30, end="\r", flush=True)  # Clear listening indicator

            try:
                text = recognizer.recognize_google(audio)
            except sr.UnknownValueError:
                continue
            except sr.RequestError as re:
                print(f"[ULTRON ERROR] Speech recognition service error: {re}")
                time.sleep(1.0)
                continue

            if not text:
                continue

            text_lower = text.lower().strip()
            print(f"[Heard]: \"{text}\"")

            # Check for wake word "ultron"
            if WAKE_WORD in text_lower:
                if "reset" in text_lower:
                    print(f"{CYAN}{BOLD}ULTRON: Executing Reset...{RESET}")
                    payload = {"command": "RESET", "timestamp": time.time()}
                    send_udp_command(sock, payload)

                elif "enable slice" in text_lower or "turn on slice" in text_lower:
                    print(f"{CYAN}{BOLD}ULTRON: Enabling Slice Plane...{RESET}")
                    payload = {"command": "ENABLE_SLICE", "timestamp": time.time()}
                    send_udp_command(sock, payload)

                elif "disable slice" in text_lower or "turn off slice" in text_lower:
                    print(f"{CYAN}{BOLD}ULTRON: Disabling Slice Plane...{RESET}")
                    payload = {"command": "DISABLE_SLICE", "timestamp": time.time()}
                    send_udp_command(sock, payload)

                else:
                    print(f"{YELLOW}ULTRON: Wake word detected, command not recognized.{RESET}")

        except sr.WaitTimeoutError:
            continue
        except KeyboardInterrupt:
            print(f"\n{YELLOW}[ULTRON] Server shutting down...{RESET}")
            break
        except Exception as ex:
            print(f"[ULTRON EXCEPTION]: {ex}")
            time.sleep(0.5)

    sock.close()
    print(f"{GREEN}[ULTRON] Offline.{RESET}")


if __name__ == "__main__":
    banner = rf"""{CYAN}{BOLD}
================================================================================
  _   _ _   _____ ____   ___  _   _    __     _____ ___ ____ _____ 
 | | | | | |_   _|  _ \ / _ \| \ | |   \ \   / / _ \_ _/ ___| ____|
 | | | | |   | | | |_) | | | |  \| |____\ \ / / | | | | |   |  _|  
 | |_| | |___| | |  _ <| |_| | |\  |_____\ V /| |_| | | |___| |___ 
  \___/|_____|_| |_| \_\___/|_| \_|      \_/  \___/___|____|_____|
================================================================================
                    *** ULTRON VOICE SERVER ONLINE ***
                  Target: 127.0.0.1:{UDP_PORT} (HoloMedAI Unity)
================================================================================{RESET}"""
    print(banner)
    listen_for_commands()
