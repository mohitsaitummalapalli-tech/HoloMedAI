"""
================================================================================
  HoloMed AI - Category 2: Voice & Audio Ingestion Subsystem
  File: python/holomed/input/voice.py
================================================================================
Captures microphone audio in a dedicated background worker thread to prevent
blocking the computer vision / camera HUD loop. Uses SpeechRecognition
(with PyAudio / sounddevice / whisper fallbacks) to transcribe speech into text
and forward it to the intelligence intent parser (UltronBrain).
"""

import logging
import queue
import threading
import time
from typing import Any, Callable, Optional

# Configure module logger
logger = logging.getLogger("holomed.input.voice")
if not logger.handlers:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] [%(name)s] %(message)s")

# Speech recognition imports with graceful degradation
try:
    import speech_recognition as sr
    HAS_SPEECH_RECOGNITION = True
except ImportError:
    sr = None
    HAS_SPEECH_RECOGNITION = False

try:
    import pyaudio
    HAS_PYAUDIO = True
except ImportError:
    pyaudio = None
    HAS_PYAUDIO = False


class VoiceListener:
    """Real-time asynchronous microphone listener.
    
    Runs continuously on a background thread without blocking the camera
    or perception pipeline. When speech is recognized, it dispatches the
    transcribed text to registered callbacks or an UltronBrain instance.
    """

    def __init__(
        self,
        on_speech_detected: Optional[Callable[[str], None]] = None,
        energy_threshold: int = 300,
        pause_threshold: float = 0.8,
        dynamic_energy_threshold: bool = True,
        sample_rate: int = 16000,
    ) -> None:
        """Initialize the VoiceListener.

        Args:
            on_speech_detected: Callback function invoked with recognized text.
            energy_threshold: Microphone sensitivity threshold.
            pause_threshold: Seconds of non-speaking audio before phrase is considered complete.
            dynamic_energy_threshold: Whether to automatically adjust for ambient noise.
            sample_rate: Audio sampling frequency in Hz.
        """
        self.on_speech_detected = on_speech_detected
        self.energy_threshold = energy_threshold
        self.pause_threshold = pause_threshold
        self.dynamic_energy_threshold = dynamic_energy_threshold
        self.sample_rate = sample_rate

        self._is_listening = False
        self._thread: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._audio_queue: queue.Queue[str] = queue.Queue()

        # Initialize speech recognizer
        self._recognizer: Optional[Any] = None
        self._microphone: Optional[Any] = None

        if HAS_SPEECH_RECOGNITION:
            try:
                self._recognizer = sr.Recognizer()
                self._recognizer.energy_threshold = self.energy_threshold
                self._recognizer.pause_threshold = self.pause_threshold
                self._recognizer.dynamic_energy_threshold = self.dynamic_energy_threshold
            except Exception as ex:
                logger.warning("Could not initialize SpeechRecognition Recognizer: %s", ex)
        else:
            logger.info("SpeechRecognition library not installed. Simulation/injection mode active.")

    @property
    def is_listening(self) -> bool:
        """Returns True if the background audio thread is currently active."""
        return self._is_listening

    def start_listening(self) -> None:
        """Starts real-time microphone listening on a separate background thread.
        
        Guarantees that the main camera / OpenCV rendering thread is never blocked.
        """
        if self._is_listening:
            logger.warning("VoiceListener is already running.")
            return

        self._is_listening = True
        self._stop_event.clear()

        self._thread = threading.Thread(
            target=self._listen_worker,
            name="HoloMedVoiceListenerThread",
            daemon=True,
        )
        self._thread.start()
        logger.info("[VoiceListener] Background microphone worker thread started successfully.")

    def stop_listening(self) -> None:
        """Stops the background listening thread cleanly."""
        if not self._is_listening:
            return

        logger.info("[VoiceListener] Stopping background audio listener...")
        self._is_listening = False
        self._stop_event.set()

        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=2.0)
            self._thread = None

        logger.info("[VoiceListener] Background audio listener stopped.")

    def inject_phrase(self, text: str) -> None:
        """Simulate speech input without a physical microphone (useful for tests and CLI)."""
        clean_text = text.strip()
        if clean_text:
            logger.info("[VoiceListener INJECTED]: \"%s\"", clean_text)
            self._dispatch_text(clean_text)

    def _listen_worker(self) -> None:
        """Worker loop executing on the background thread."""
        logger.info("[VoiceListener] Audio worker thread active. Listening for medical speech...")

        if not HAS_SPEECH_RECOGNITION or not HAS_PYAUDIO:
            logger.warning(
                "[VoiceListener] Hardware microphone requirements (SpeechRecognition/PyAudio) not fully available. "
                "Running in standby mode. Use inject_phrase() to trigger voice intents."
            )
            while not self._stop_event.is_set():
                time.sleep(0.2)
            return

        # Attempt to acquire default microphone
        try:
            self._microphone = sr.Microphone(sample_rate=self.sample_rate)
            with self._microphone as source:
                logger.info("[VoiceListener] Calibrating microphone for ambient room noise (1.0s)...")
                self._recognizer.adjust_for_ambient_noise(source, duration=1.0)
                logger.info(
                    "[VoiceListener] Calibration complete. Energy threshold set to %d.",
                    self._recognizer.energy_threshold,
                )
        except Exception as ex:
            logger.error("[VoiceListener] Failed to access microphone: %s. Entering standby mode.", ex)
            while not self._stop_event.is_set():
                time.sleep(0.2)
            return

        # Continuous listening loop
        while not self._stop_event.is_set():
            try:
                with self._microphone as source:
                    # Listen with timeout so thread can respond to stop_event
                    try:
                        audio = self._recognizer.listen(source, timeout=2.0, phrase_time_limit=8.0)
                    except sr.WaitTimeoutError:
                        continue

                # Transcribe audio chunk
                try:
                    text = self._recognizer.recognize_google(audio)
                    if text and text.strip():
                        recognized = text.strip()
                        logger.info("[VoiceListener HEARD]: \"%s\"", recognized)
                        self._dispatch_text(recognized)
                except sr.UnknownValueError:
                    # Unintelligible speech / ambient noise
                    pass
                except sr.RequestError as req_err:
                    logger.warning("[VoiceListener] Speech API request error: %s", req_err)
                except Exception as ex:
                    logger.debug("[VoiceListener] Recognition error: %s", ex)

            except Exception as loop_err:
                if not self._stop_event.is_set():
                    logger.error("[VoiceListener] Worker loop error: %s", loop_err)
                    time.sleep(0.5)

    def _dispatch_text(self, text: str) -> None:
        """Forward recognized text to the callback."""
        if self.on_speech_detected:
            try:
                self.on_speech_detected(text)
            except Exception as ex:
                logger.error("[VoiceListener] Error in on_speech_detected callback: %s", ex)
