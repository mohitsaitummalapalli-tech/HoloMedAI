"""Wake-word voice control with keyboard fallback for HoloMed AI, sent to Unity via UDP."""

import json
import re
import socket
import sys
import threading
from typing import Any, Optional

import speech_recognition as sr

try:
    import pyaudio  # SpeechRecognition uses PyAudio for sr.Microphone().
except ImportError:
    pyaudio = None

UDP_HOST = "127.0.0.1"
UDP_PORT = 5053
WAKE_WORDS = ("ultron", "jarvis")


class Colour:
    RESET = "\033[0m"
    CYAN = "\033[96m"
    GREEN = "\033[92m"
    YELLOW = "\033[93m"
    RED = "\033[91m"
    MAGENTA = "\033[95m"
    BOLD = "\033[1m"


def log(colour: str, message: str) -> None:
    print(f"{colour}{message}{Colour.RESET}")


def extract_command(text: str) -> Optional[str]:
    """Return text following Ultron or Jarvis, otherwise ``None``."""
    match = re.search(r"\b(?:ultron|jarvis)\b", text, flags=re.IGNORECASE)
    return text[match.end():].strip(" ,.?!:;-") if match else None


def extract_intent(spoken_text: str) -> Optional[tuple[dict[str, Any], str]]:
    """Find a medical-model intent anywhere in a conversational command.

    Filler such as "could you please" is ignored because only meaningful
    action verbs and anatomy/visualization nouns are inspected.
    """
    command = re.sub(r"\s+", " ", spoken_text.lower()).strip()

    if any(word in command for word in ("rotate", "spin", "turn")):
        return (
            {"command": "ROTATE", "organ": "HEART", "direction": "LEFT", "auto_zoom": False},
            "Sure thing, rotating the model now.",
        )
    if any(word in command for word in ("hide", "remove", "clear")) and any(
        word in command for word in ("wall", "layer")
    ):
        return (
            {"command": "HIDE", "organ": "HEART", "target_part": "OUTER_WALL", "auto_zoom": False},
            "Sure thing, hiding the outer wall now.",
        )
    if any(word in command for word in ("cut", "inside", "section")):
        return (
            {"command": "SECTION_VIEW", "organ": "HEART", "target_part": "ALL", "auto_zoom": False},
            "Opening the internal section view.",
        )
    if any(word in command for word in ("reset", "normal", "back")):
        return (
            {"command": "RESET", "organ": "HEART", "target_part": "ALL", "auto_zoom": False},
            "Resetting the model to normal.",
        )
    if any(phrase in command for phrase in ("oxygenated", "aorta", "main artery")):
        return (
            {"command": "HIGHLIGHT", "organ": "HEART", "target_part": "AORTA", "auto_zoom": True},
            "Highlighting the aorta now, doctor.",
        )
    if "left" in command or "pumps to body" in command or "pumps to the body" in command:
        return (
            {"command": "HIGHLIGHT", "organ": "HEART", "target_part": "LEFT_VENTRICLE", "auto_zoom": True},
            "Highlighting the left ventricle now, doctor.",
        )
    return None


def speak(text: str) -> None:
    """Speak one response using Windows SAPI5 in a background thread."""
    def _run_tts():
        try:
            import pyttsx3

            engine = pyttsx3.init()
            engine.say(text)
            engine.runAndWait()
            engine.stop()
        except Exception as error:
            print(f"[TTS ERROR]: {error}")

    threading.Thread(target=_run_tts, daemon=True).start()


def execute_command_string(raw_input_text: str, socket_client: socket.socket, is_keyboard: bool = False) -> bool:
    """Parse text, trigger TTS, and dispatch UDP JSON payload to Unity.

    Returns True if a valid command was processed, False otherwise.
    """
    if is_keyboard:
        # Accept commands typed with or without wake word
        cleaned = extract_command(raw_input_text) or raw_input_text.strip()
        log(Colour.MAGENTA, f'[KEYBOARD INPUT] "{raw_input_text}"')
    else:
        cleaned = extract_command(raw_input_text)
        if cleaned is None:
            log(Colour.YELLOW, "[IGNORED] No wake word detected.")
            return False
        log(Colour.MAGENTA, f'[WAKE WORD] Detected in: "{raw_input_text}"')

    intent = extract_intent(cleaned)
    if intent is None:
        log(Colour.YELLOW, f'[IGNORED] Unsupported command: "{cleaned}"')
        if is_keyboard:
            log(Colour.CYAN, "  💡 Supported: 'rotate model', 'highlight aorta', 'left ventricle', 'cut inside', 'hide wall', 'reset'")
        return False

    payload, response = intent
    json_payload = json.dumps(payload, separators=(",", ":"))

    # Trigger TTS feedback
    speak(response)
    log(Colour.GREEN, f"[TTS] \"{response}\"")

    # Send over UDP to Unity
    try:
        socket_client.sendto(json_payload.encode("utf-8"), (UDP_HOST, UDP_PORT))
        log(Colour.GREEN, f"[JSON] {json_payload}")
        log(Colour.GREEN, f"[UDP {UDP_HOST}:{UDP_PORT}] Sent successfully")
        return True
    except OSError as error:
        log(Colour.RED, f"[UDP ERROR] {error}")
        return False


def start_keyboard_listener(socket_client: socket.socket, stop_event: threading.Event) -> None:
    """Runs in a background thread allowing instant keyboard command fallback."""
    while not stop_event.is_set():
        try:
            typed_command = input().strip()
            if not typed_command:
                continue

            if typed_command.lower() in ("exit", "quit", "q"):
                log(Colour.YELLOW, "\n[STOPPING] Exit requested via keyboard.")
                stop_event.set()
                break

            execute_command_string(typed_command, socket_client, is_keyboard=True)
            print()  # Add clean spacing after response
        except (EOFError, KeyboardInterrupt):
            stop_event.set()
            break


def run_voice_assistant() -> None:
    """Run dual-input voice & keyboard assistant until Ctrl+C or 'exit'."""
    socket_client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    stop_event = threading.Event()

    print("=" * 62)
    log(Colour.CYAN, f"{Colour.BOLD}HoloMed AI Voice & Keyboard Assistant")
    log(Colour.CYAN, f"UDP Target: {UDP_HOST}:{UDP_PORT}")
    log(Colour.CYAN, "• Voice: Say 'Ultron' or 'Jarvis' + command")
    log(Colour.CYAN, "• Keyboard Fallback: Type anytime & press [Enter] (e.g. 'rotate model')")
    log(Colour.CYAN, "• Type 'exit' or press Ctrl+C to quit")
    print("=" * 62)

    # Launch keyboard fallback thread immediately
    kb_thread = threading.Thread(
        target=start_keyboard_listener,
        args=(socket_client, stop_event),
        daemon=True,
    )
    kb_thread.start()

    if pyaudio is None:
        log(Colour.YELLOW, "[MIC NOTICE] PyAudio is not available. Running in Keyboard-Only mode.")
        log(Colour.GREEN, "[READY] Type any command below and press Enter:")
        while not stop_event.is_set():
            stop_event.wait(0.5)
        socket_client.close()
        log(Colour.CYAN, "[STOPPED] UDP socket closed.")
        return

    recognizer = sr.Recognizer()
    mic = sr.Microphone()

    try:
        with mic as source:
            log(Colour.CYAN, "[SETUP] Calibrating microphone for ambient noise...")
            recognizer.adjust_for_ambient_noise(source, duration=1.5)
            recognizer.energy_threshold = 300
            recognizer.dynamic_energy_threshold = True
            recognizer.pause_threshold = 0.8
            log(Colour.GREEN, "[READY] Listening on microphone + keyboard active.")

            while not stop_event.is_set():
                try:
                    # phrase_time_limit prevents blocking the loop indefinitely
                    audio = recognizer.listen(source, phrase_time_limit=4, timeout=3.0)
                    text = recognizer.recognize_google(audio).lower()
                except sr.WaitTimeoutError:
                    continue  # Timeout is normal; allows checking stop_event & keyboard inputs
                except sr.UnknownValueError:
                    log(Colour.YELLOW, "[UNRECOGNIZED] Voice not clear. You can speak again or type the command.")
                    continue
                except sr.RequestError as error:
                    log(Colour.RED, f"[RECOGNITION ERROR] {error}")
                    continue

                log(Colour.CYAN, f'[HEARD] "{text}"')
                execute_command_string(text, socket_client, is_keyboard=False)

    except KeyboardInterrupt:
        log(Colour.YELLOW, "\n[STOPPING] Voice assistant interrupted.")
    except OSError as error:
        log(Colour.RED, f"[MICROPHONE ERROR] {error}")
        log(Colour.YELLOW, "[FALLBACK] Microphone failed, but Keyboard input remains active.")
        while not stop_event.is_set():
            stop_event.wait(0.5)
    finally:
        stop_event.set()
        socket_client.close()
        log(Colour.CYAN, "[STOPPED] UDP socket closed.")


if __name__ == "__main__":
    run_voice_assistant()
