"""
================================================================================
  HoloMed AI - Category 2: Ultron Intelligence & NLP Intent Parser
  File: python/holomed/intelligence/ultron.py
================================================================================
Parses raw medical voice transcriptions into validated, typed JSON intents.
Formats standardized 'sys.input.voice' event envelopes and dispatches them
to the central IPC Dispatcher for real-time Unity 3D holographic manipulation.
"""

import json
import logging
import re
import socket
import time
from typing import Any, Callable, Dict, Optional

logger = logging.getLogger("holomed.intelligence.ultron")
if not logger.handlers:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] [%(name)s] %(message)s")


class UltronBrain:
    """Natural Language & Medical Intent Reasoning Engine (Ultron).
    
    Transforms transcribed speech into structured action intents and
    dispatches standardized 'sys.input.voice' datagrams to the central
    IPC bus / Unity UDP endpoint.
    """

    def __init__(
        self,
        dispatcher: Optional[Any] = None,
        udp_host: str = "127.0.0.1",
        udp_port: int = 5052,
    ) -> None:
        """Initialize the UltronBrain.

        Args:
            dispatcher: Optional IPC Dispatcher instance (e.g. PerceptionIPCDispatcher
                        or MessageDispatcher) with a send_payload() method.
            udp_host: Fallback UDP IP target for Unity streaming.
            udp_port: Fallback UDP port target for Unity streaming (default: 5052).
        """
        self.dispatcher = dispatcher
        self.udp_host = udp_host
        self.udp_port = udp_port
        self._sock: Optional[socket.socket] = None

        # Clinical Knowledge Base for basic anatomical query responses
        self._clinical_kb = {
            "myocardial infarction": (
                "A myocardial infarction occurs when blood flow decreases or stops to a part of the heart, "
                "causing damage to the heart muscle. Highlighting coronary arteries and left ventricle."
            ),
            "aortic valve": "The aortic valve is between the left ventricle and aorta, preventing backflow of blood.",
            "left ventricle": "The left ventricle is the thickest chamber, responsible for pumping oxygenated blood to the body.",
        }

    def parse_intent(self, text: str) -> Dict[str, Any]:
        """Maps raw speech transcriptions to strict JSON intents.

        Args:
            text: Raw speech string recognized by VoiceListener.

        Returns:
            Dictionary containing the normalized intent and anatomical parameters.
        """
        if not text or not text.strip():
            return {"intent": "NOOP", "confidence": 0.0}

        cleaned = text.strip()
        lower = cleaned.lower()

        # Strip wake words ("ultron", "jarvis", "computer", "hey ultron")
        lower = re.sub(r"^(?:hey\s+)?(?:ultron|jarvis|computer)[\s,:]*", "", lower).strip()
        lower = re.sub(r"[^\w\s-]", "", lower).strip()

        # 1. Dummy test mapping: "show me the heart" -> LOAD_ORGAN: HEART
        if "show me the heart" in lower or lower in ("load heart", "open heart", "show heart", "display heart"):
            return {
                "intent": "LOAD_ORGAN",
                "organ": "HEART",
                "confidence": 0.98,
                "ai_speech_response": "Loading 3D holographic heart model now, doctor.",
            }

        # 2. Additional Organ Loading
        if any(phrase in lower for phrase in ("show brain", "load brain", "display brain")):
            return {
                "intent": "LOAD_ORGAN",
                "organ": "BRAIN",
                "confidence": 0.95,
                "ai_speech_response": "Loading cranial neural model.",
            }

        # 3. Model Rotation
        if any(word in lower for word in ("rotate", "spin", "turn")):
            direction = "RIGHT" if "right" in lower else "LEFT"
            return {
                "intent": "ROTATE",
                "organ": "HEART",
                "direction": direction,
                "auto_zoom": False,
                "confidence": 0.90,
                "ai_speech_response": f"Rotating model {direction.lower()}.",
            }

        # 4. Model Zoom / Scaling
        if any(phrase in lower for phrase in ("zoom in", "scale up", "enlarge", "closer")):
            return {
                "intent": "ZOOM",
                "organ": "HEART",
                "delta": 0.25,
                "confidence": 0.92,
                "ai_speech_response": "Zooming in on cardiac anatomy.",
            }
        if any(phrase in lower for phrase in ("zoom out", "scale down", "shrink", "further")):
            return {
                "intent": "ZOOM",
                "organ": "HEART",
                "delta": -0.25,
                "confidence": 0.92,
                "ai_speech_response": "Zooming out.",
            }

        # 5. Model Translation / Pan
        if "move left" in lower or "pan left" in lower:
            return {"intent": "MOVE", "dx": -1.5, "dy": 0.0, "confidence": 0.90}
        if "move right" in lower or "pan right" in lower:
            return {"intent": "MOVE", "dx": 1.5, "dy": 0.0, "confidence": 0.90}
        if "move up" in lower or "pan up" in lower:
            return {"intent": "MOVE", "dx": 0.0, "dy": 1.5, "confidence": 0.90}
        if "move down" in lower or "pan down" in lower:
            return {"intent": "MOVE", "dx": 0.0, "dy": -1.5, "confidence": 0.90}

        # 6. Surgical Slicing / Section View
        if any(word in lower for word in ("cut", "slice", "inside", "section", "dissect")):
            return {
                "intent": "SECTION_VIEW",
                "organ": "HEART",
                "target_part": "ALL",
                "confidence": 0.93,
                "ai_speech_response": "Opening internal ventricular section view.",
            }

        # 7. Anatomical Highlights
        if "aorta" in lower or "oxygenated" in lower or "main artery" in lower:
            return {
                "intent": "HIGHLIGHT",
                "organ": "HEART",
                "target_part": "AORTA",
                "auto_zoom": True,
                "confidence": 0.95,
                "ai_speech_response": "Highlighting ascending aorta in yellow.",
            }
        if "left ventricle" in lower or "ventricle" in lower:
            return {
                "intent": "HIGHLIGHT",
                "organ": "HEART",
                "target_part": "LEFT_VENTRICLE",
                "auto_zoom": True,
                "confidence": 0.95,
                "ai_speech_response": "Highlighting left ventricle, primary systemic pump.",
            }

        # 8. Hide / Visibility Layer Toggles
        if any(word in lower for word in ("hide", "remove", "clear")) and any(word in lower for word in ("wall", "layer")):
            return {
                "intent": "HIDE",
                "organ": "HEART",
                "target_part": "OUTER_WALL",
                "confidence": 0.91,
                "ai_speech_response": "Hiding myocardium outer wall.",
            }

        # 9. Reset Transform & Appearance
        if any(word in lower for word in ("reset", "restore", "default", "normal")):
            return {
                "intent": "RESET",
                "organ": "HEART",
                "target_part": "ALL",
                "confidence": 0.98,
                "ai_speech_response": "Resetting holographic model to neutral coordinates.",
            }

        # 10. Clinical Q&A Query Check
        for disease, clinical_text in self._clinical_kb.items():
            if disease in lower:
                return {
                    "intent": "MEDICAL_QUERY",
                    "organ": "HEART",
                    "query": cleaned,
                    "ai_speech_response": clinical_text,
                    "confidence": 0.88,
                }

        # 11. Fallback Unknown Intent
        return {
            "intent": "UNKNOWN",
            "raw_query": cleaned,
            "confidence": 0.20,
            "ai_speech_response": f"I heard '{cleaned}', but no matching medical intent was recognized.",
        }

    def format_voice_payload(self, intent: Dict[str, Any], raw_text: str = "") -> Dict[str, Any]:
        """Formats an intent dictionary into a strict 'sys.input.voice' event payload.

        Matches the schema expected by Unity's VoiceCommandReceiver and UDPReceiver.
        """
        command_name = intent.get("intent", "UNKNOWN")
        return {
            "action": "sys.input.voice",
            "type": "VOICE_COMMAND",
            "command": command_name,
            "organ": intent.get("organ", "HEART"),
            "target_part": intent.get("target_part", ""),
            "direction": intent.get("direction", ""),
            "delta": intent.get("delta", 0.0),
            "dx": intent.get("dx", 0.0),
            "dy": intent.get("dy", 0.0),
            "ai_speech_response": intent.get("ai_speech_response", ""),
            "query_text": raw_text or intent.get("raw_query", ""),
            "confidence": intent.get("confidence", 1.0),
            "timestamp": time.time(),
            "payload": intent,
        }

    def process_and_dispatch(self, text: str) -> Dict[str, Any]:
        """Processes raw text from VoiceListener and sends it to the Dispatcher.

        1. Parses intent via parse_intent().
        2. Packages into sys.input.voice payload.
        3. Forwards to Dispatcher (or fallback UDP).
        4. Returns the payload for logging / verification.
        """
        intent = self.parse_intent(text)
        payload = self.format_voice_payload(intent, raw_text=text)

        logger.info(
            "[UltronBrain] Mapped: \"%s\" -> Intent: %s (organ: %s)",
            text,
            payload["command"],
            payload["organ"],
        )

        self.dispatch(payload)
        return payload

    def dispatch(self, payload: Dict[str, Any]) -> None:
        """Sends payload to the registered Dispatcher or direct UDP fallback."""
        # 1. Use registered Dispatcher if present
        if self.dispatcher is not None:
            try:
                if hasattr(self.dispatcher, "send_payload"):
                    self.dispatcher.send_payload(payload)
                    return
                elif hasattr(self.dispatcher, "dispatch"):
                    self.dispatcher.dispatch(payload)
                    return
                elif callable(self.dispatcher):
                    self.dispatcher(payload)
                    return
            except Exception as ex:
                logger.error("[UltronBrain] Error forwarding payload to Dispatcher: %s", ex)

        # 2. Fallback: Direct UDP broadcast (Port 5052)
        try:
            if self._sock is None:
                self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            msg_bytes = json.dumps(payload).encode("utf-8")
            self._sock.sendto(msg_bytes, (self.udp_host, self.udp_port))
            logger.debug("[UltronBrain] Transmitted UDP payload to %s:%d", self.udp_host, self.udp_port)
        except Exception as ex:
            logger.error("[UltronBrain] Failed to send UDP fallback packet: %s", ex)

    def close(self) -> None:
        """Closes UDP socket resources."""
        if self._sock:
            try:
                self._sock.close()
            except Exception:
                pass
            self._sock = None
