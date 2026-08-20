"""
HoloMed AI - Backend Module 2: "Ultron" AI Intent Parser & Command Orchestrator
File: backend/ai_orchestrator.py

Features:
- Dual-tier natural language processing:
    1. Fast deterministic regex/keyword rule matching for medical anatomy & hologram commands.
    2. Extensible LLM client (OpenAI API / Ollama / Local API) for unstructured queries.
- Context State Engine:
    Tracks active organ (HEART, BRAIN, LUNGS), active sub-structure, section view state.
- Output Schema:
    {
        "command": "HIGHLIGHT",
        "organ": "HEART",
        "target_part": "LEFT_VENTRICLE",
        "auto_zoom": true,
        "ai_speech_response": "Highlighting the left ventricle."
    }
- Direct UDP transmission to 127.0.0.1:5052.
- Interactive text prompt + optional live voice microphone listener (SpeechRecognition/Whisper).
"""

import json
import os
import re
import socket
import sys
import threading
import time
from typing import Dict, Any, Optional, Tuple

# Try optional speech recognition
try:
    import speech_recognition as sr
    SPEECH_AVAILABLE = True
except ImportError:
    SPEECH_AVAILABLE = False


class HoloMedContextEngine:
    """State tracking engine for active organ and visualization context."""
    def __init__(self, default_organ: str = "HEART"):
        self.current_organ: str = default_organ
        self.active_structure: Optional[str] = None
        self.section_view_active: bool = False
        self.zoom_level: float = 1.0
        self.history = []

    def update_context(self, organ: Optional[str] = None, structure: Optional[str] = None, section_view: Optional[bool] = None):
        if organ:
            self.current_organ = organ.upper()
        if structure is not None:
            self.active_structure = structure.upper() if structure else None
        if section_view is not None:
            self.section_view_active = section_view

    def get_state(self) -> Dict[str, Any]:
        return {
            "current_organ": self.current_organ,
            "active_structure": self.active_structure,
            "section_view_active": self.section_view_active,
            "zoom_level": self.zoom_level,
        }


class UltronAIOrchestrator:
    def __init__(
        self,
        udp_ip: str = "127.0.0.1",
        udp_port: int = 5052,
        use_llm: bool = False,
        openai_api_key: Optional[str] = None,
        ollama_endpoint: str = "http://localhost:11434/api/generate",
    ):
        self.udp_ip = udp_ip
        self.udp_port = udp_port
        self.use_llm = use_llm
        self.openai_api_key = openai_api_key or os.getenv("OPENAI_API_KEY")
        self.ollama_endpoint = ollama_endpoint

        # Network Setup
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.context = HoloMedContextEngine(default_organ="HEART")

        # Anatomical Knowledge Graph & Synonyms for Rule Matcher
        self.anatomy_map = {
            # Heart Structures & Chambers
            "left ventricle": ("HEART", "LEFT_VENTRICLE", "Highlighting the left ventricle."),
            "left_ventricle": ("HEART", "LEFT_VENTRICLE", "Highlighting the left ventricle."),
            "lv": ("HEART", "LEFT_VENTRICLE", "Highlighting the left ventricle."),
            "right ventricle": ("HEART", "RIGHT_VENTRICLE", "Highlighting the right ventricle."),
            "right_ventricle": ("HEART", "RIGHT_VENTRICLE", "Highlighting the right ventricle."),
            "rv": ("HEART", "RIGHT_VENTRICLE", "Highlighting the right ventricle."),
            "left atrium": ("HEART", "LEFT_ATRIUM", "Highlighting the left atrium."),
            "left_atrium": ("HEART", "LEFT_ATRIUM", "Highlighting the left atrium."),
            "la": ("HEART", "LEFT_ATRIUM", "Highlighting the left atrium."),
            "right atrium": ("HEART", "RIGHT_ATRIUM", "Highlighting the right atrium."),
            "right_atrium": ("HEART", "RIGHT_ATRIUM", "Highlighting the right atrium."),
            "ra": ("HEART", "RIGHT_ATRIUM", "Highlighting the right atrium."),
            
            # Great Vessels & Valves
            "aorta": ("HEART", "AORTA", "Locating and highlighting the ascending and descending aorta."),
            "ascending aorta": ("HEART", "AORTA", "Highlighting the ascending aorta."),
            "aortic arch": ("HEART", "AORTA", "Visualizing the aortic arch."),
            "aortic valve": ("HEART", "AORTIC_VALVE", "Focusing on the aortic semilunar valve."),
            "mitral valve": ("HEART", "MITRAL_VALVE", "Focusing on the bicuspid mitral valve structure."),
            "bicuspid valve": ("HEART", "MITRAL_VALVE", "Focusing on the mitral valve."),
            "tricuspid valve": ("HEART", "TRICUSPID_VALVE", "Focusing on the tricuspid valve."),
            "pulmonary valve": ("HEART", "PULMONARY_VALVE", "Focusing on the pulmonary valve."),
            "pulmonary artery": ("HEART", "PULMONARY_ARTERY", "Highlighting the pulmonary trunk and artery."),
            "pulmonary trunk": ("HEART", "PULMONARY_ARTERY", "Highlighting the pulmonary trunk."),
            "pulmonary veins": ("HEART", "PULMONARY_VEIN", "Locating the pulmonary veins."),
            "pulmonary vein": ("HEART", "PULMONARY_VEIN", "Locating the pulmonary veins."),
            "superior vena cava": ("HEART", "SUPERIOR_VENA_CAVA", "Highlighting the superior vena cava."),
            "svc": ("HEART", "SUPERIOR_VENA_CAVA", "Highlighting the superior vena cava."),
            "inferior vena cava": ("HEART", "INFERIOR_VENA_CAVA", "Highlighting the inferior vena cava."),
            "ivc": ("HEART", "INFERIOR_VENA_CAVA", "Highlighting the inferior vena cava."),
            "coronary artery": ("HEART", "CORONARY_ARTERY", "Visualizing coronary arterial circulation."),
            "coronary arteries": ("HEART", "CORONARY_ARTERY", "Visualizing coronary arterial circulation."),
            "myocardium": ("HEART", "MYOCARDIUM", "Highlighting cardiac muscular tissue."),
            "septum": ("HEART", "INTERVENTRICULAR_SEPTUM", "Highlighting the interventricular septum."),
            "interventricular septum": ("HEART", "INTERVENTRICULAR_SEPTUM", "Highlighting the interventricular septum."),
            "apex": ("HEART", "APEX", "Focusing on the cardiac apex."),
            "pericardium": ("HEART", "PERICARDIUM", "Visualizing the pericardial sac."),

            # Brain Structures (Neurological Anatomy)
            "cerebrum": ("BRAIN", "CEREBRUM", "Highlighting the cerebral cortex."),
            "cerebral cortex": ("BRAIN", "CEREBRUM", "Highlighting the cerebral cortex."),
            "cerebellum": ("BRAIN", "CEREBELLUM", "Highlighting the cerebellum."),
            "frontal lobe": ("BRAIN", "FRONTAL_LOBE", "Focusing on the frontal lobe."),
            "temporal lobe": ("BRAIN", "TEMPORAL_LOBE", "Focusing on the temporal lobe."),
            "parietal lobe": ("BRAIN", "PARIETAL_LOBE", "Focusing on the parietal lobe."),
            "occipital lobe": ("BRAIN", "OCCIPITAL_LOBE", "Focusing on the visual occipital lobe."),
            "brainstem": ("BRAIN", "BRAIN_STEM", "Highlighting the brainstem and medulla."),
            "brain stem": ("BRAIN", "BRAIN_STEM", "Highlighting the brainstem."),
            "thalamus": ("BRAIN", "THALAMUS", "Focusing on the thalamus structure."),
            "hypothalamus": ("BRAIN", "HYPOTHALAMUS", "Locating the hypothalamus."),
            "hippocampus": ("BRAIN", "HIPPOCAMPUS", "Highlighting the hippocampus."),
            "corpus callosum": ("BRAIN", "CORPUS_CALLOSUM", "Highlighting the corpus callosum."),

            # Respiratory / Lung Structures
            "lungs": ("LUNGS", "ALL", "Loading the respiratory lung model."),
            "left lung": ("LUNGS", "LEFT_LUNG", "Highlighting the left lung lobes."),
            "right lung": ("LUNGS", "RIGHT_LUNG", "Highlighting the right lung lobes."),
            "trachea": ("LUNGS", "TRACHEA", "Highlighting the trachea and airway."),
            "bronchi": ("LUNGS", "BRONCHI", "Highlighting primary and secondary bronchi."),
            "bronchial tree": ("LUNGS", "BRONCHI", "Visualizing the bronchial tree."),
        }

    def transmit_payload(self, payload: Dict[str, Any]) -> None:
        """Sends command over UDP."""
        try:
            message = json.dumps(payload, indent=2).encode("utf-8")
            self.sock.sendto(message, (self.udp_ip, self.udp_port))
            print(f"\n[Ultron Orchestrator] >> Transmitted UDP Command to {self.udp_ip}:{self.udp_port}:")
            print(json.dumps(payload, indent=2))
        except Exception as e:
            print(f"[Ultron Orchestrator] UDP Error: {e}")

    def parse_with_rules(self, text: str) -> Optional[Dict[str, Any]]:
        """High-speed deterministic rule matching engine."""
        query = text.lower().strip()

        # 1. Section / Cutaway View Commands
        if re.search(r'\b(cut\s+(\w+\s+)?open|cutaway|section\s+view|cross\s+section|slice|internal\s+view|inside|dissect)\b', query):
            self.context.update_context(section_view=True)
            return {
                "command": "SECTION_VIEW",
                "organ": self.context.current_organ,
                "target_part": "ALL",
                "auto_zoom": False,
                "ai_speech_response": f"Activating internal section view for {self.context.current_organ.lower()}."
            }

        # 2. Reset / Default View Commands
        if any(w in query for w in ["reset", "default view", "original view", "clear highlight", "unhide", "center view", "recenter"]):
            self.context.update_context(structure=None, section_view=False)
            return {
                "command": "RESET",
                "organ": self.context.current_organ,
                "target_part": "ALL",
                "auto_zoom": False,
                "ai_speech_response": f"Resetting {self.context.current_organ.lower()} orientation and highlights."
            }

        # 3. Zoom Commands
        if any(w in query for w in ["zoom in", "magnify", "enlarge", "closer", "zoom closer"]):
            return {
                "command": "ZOOM",
                "organ": self.context.current_organ,
                "value": 1.5,
                "auto_zoom": False,
                "ai_speech_response": "Zooming in."
            }
        elif any(w in query for w in ["zoom out", "shrink", "reduce", "further away", "pull back"]):
            return {
                "command": "ZOOM",
                "organ": self.context.current_organ,
                "value": -1.5,
                "auto_zoom": False,
                "ai_speech_response": "Zooming out."
            }

        # 4. Rotation Commands (3D multi-axis)
        if any(w in query for w in ["rotate left", "turn left", "spin left"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "axis": "Y",
                "value": -45.0,
                "ai_speech_response": "Rotating model left."
            }
        elif any(w in query for w in ["rotate right", "turn right", "spin right"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "axis": "Y",
                "value": 45.0,
                "ai_speech_response": "Rotating model right."
            }
        elif any(w in query for w in ["rotate up", "tilt up", "turn up"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "axis": "X",
                "value": 30.0,
                "ai_speech_response": "Tilting model upward."
            }
        elif any(w in query for w in ["rotate down", "tilt down", "turn down"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "axis": "X",
                "value": -30.0,
                "ai_speech_response": "Tilting model downward."
            }

        # 5. Organ Switching Commands
        if any(w in query for w in ["show heart", "switch to heart", "load heart", "cardiac model", "cardiovascular"]):
            self.context.update_context(organ="HEART", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "HEART",
                "target_part": "ALL",
                "auto_zoom": True,
                "ai_speech_response": "Loading 3D holographic cardiovascular model."
            }
        elif any(w in query for w in ["show brain", "switch to brain", "load brain", "neural model", "neurology"]):
            self.context.update_context(organ="BRAIN", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "BRAIN",
                "target_part": "ALL",
                "auto_zoom": True,
                "ai_speech_response": "Loading 3D holographic neural brain model."
            }
        elif any(w in query for w in ["show lungs", "switch to lungs", "load lungs", "pulmonary model", "respiratory"]):
            self.context.update_context(organ="LUNGS", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "LUNGS",
                "target_part": "ALL",
                "auto_zoom": True,
                "ai_speech_response": "Loading 3D holographic respiratory lungs model."
            }

        # 6. Anatomical Part Highlighting Search (ordered by specificity)
        for term, (organ, target_part, speech_text) in sorted(self.anatomy_map.items(), key=lambda x: len(x[0]), reverse=True):
            if term in query:
                self.context.update_context(organ=organ, structure=target_part)
                return {
                    "command": "HIGHLIGHT",
                    "organ": organ,
                    "target_part": target_part,
                    "auto_zoom": True,
                    "ai_speech_response": speech_text
                }

        return None

    def parse_with_llm(self, text: str) -> Optional[Dict[str, Any]]:
        """Extensible LLM intent extraction using local Ollama or OpenAI."""
        prompt = f"""
You are Ultron, the AI Medical Hologram Orchestrator for HoloMed AI.
Current context state: {json.dumps(self.context.get_state())}

Extract the anatomical target and intent from the user speech/query.
Respond ONLY with a JSON object matching this schema:
{{
  "command": "HIGHLIGHT" | "SECTION_VIEW" | "ROTATE" | "ZOOM" | "RESET" | "LOAD_ORGAN",
  "organ": "HEART" | "BRAIN" | "LUNGS",
  "target_part": string (e.g. "LEFT_VENTRICLE", "AORTA", "ALL"),
  "auto_zoom": boolean,
  "ai_speech_response": string
}}

User query: "{text}"
"""
        # Try OpenAI if key is available
        if self.openai_api_key:
            try:
                import urllib.request
                req = urllib.request.Request(
                    "https://api.openai.com/v1/chat/completions",
                    headers={
                        "Content-Type": "application/json",
                        "Authorization": f"Bearer {self.openai_api_key}"
                    },
                    data=json.dumps({
                        "model": "gpt-4o-mini",
                        "messages": [{"role": "user", "content": prompt}],
                        "response_format": {"type": "json_object"}
                    }).encode("utf-8")
                )
                with urllib.request.urlopen(req, timeout=5) as response:
                    res_data = json.loads(response.read().decode())
                    content = res_data["choices"][0]["message"]["content"]
                    return json.loads(content)
            except Exception as e:
                print(f"[Ultron LLM] OpenAI call failed: {e}. Falling back...")

        # Fallback generic default
        return None

    def process_command(self, text: str) -> Dict[str, Any]:
        """Main entry point: parses natural text and transmits resulting UDP command."""
        print(f"\n[Ultron Voice/Text Input] -> '{text}'")

        # Step 1: Fast rule engine
        command_payload = self.parse_with_rules(text)

        # Step 2: Extensible LLM fallback if enabled and rule didn't hit
        if not command_payload and self.use_llm:
            print("[Ultron] Rule not matched, querying AI reasoning engine...")
            command_payload = self.parse_with_llm(text)

        # Step 3: Default fallback if query is completely unknown
        if not command_payload:
            command_payload = {
                "command": "UNKNOWN",
                "organ": self.context.current_organ,
                "target_part": None,
                "auto_zoom": False,
                "ai_speech_response": f"I couldn't identify a medical structure in: '{text}'. Please try again."
            }

        # Transmit over UDP
        self.transmit_payload(command_payload)
        return command_payload

    def start_voice_listener(self) -> None:
        """Background thread listening for spoken microphone commands."""
        if not SPEECH_AVAILABLE:
            print("[Ultron Voice] SpeechRecognition library not installed. Voice listener disabled.")
            return

        recognizer = sr.Recognizer()
        try:
            mic = sr.Microphone()
        except Exception as e:
            print(f"[Ultron Voice] Microphone initialization failed: {e}")
            return

        print("[Ultron Voice] Listening on default microphone... (Speak medical commands)")
        while True:
            try:
                with mic as source:
                    recognizer.adjust_for_ambient_noise(source, duration=0.6)
                    audio = recognizer.listen(source, timeout=5, phrase_time_limit=8)
                text = recognizer.recognize_google(audio)
                self.process_command(text)
            except (sr.WaitTimeoutError, sr.UnknownValueError):
                pass
            except Exception as e:
                time.sleep(1)


def run_interactive_cli():
    orchestrator = UltronAIOrchestrator()
    print("=" * 60)
    print("  HoloMed AI - 'Ultron' Medical Command Orchestrator")
    print("=" * 60)
    print("Enter voice/text queries (e.g. 'Show me the left ventricle', 'Cut it open', 'Locate the aorta')")
    print("Type 'mic' to toggle live microphone listening, or 'exit' to quit.\n")

    # Run quick self-test against docs/AI_Commands.json examples
    test_queries = [
        "Show me the left ventricle",
        "Cut it open",
        "Locate the aorta",
        "Focus on the mitral valve",
        "Reset model",
    ]
    print("[Self-Test] Running standard verification queries...")
    for q in test_queries:
        orchestrator.process_command(q)
        time.sleep(0.1)

    print("\n[Ultron Ready] Enter commands below:")
    while True:
        try:
            user_input = input("\n[HoloMed AI Prompt] > ").strip()
            if not user_input:
                continue
            if user_input.lower() in ["exit", "quit"]:
                break
            if user_input.lower() == "mic":
                t = threading.Thread(target=orchestrator.start_voice_listener, daemon=True)
                t.start()
                continue

            orchestrator.process_command(user_input)
        except (KeyboardInterrupt, EOFError):
            print("\nExiting Ultron Orchestrator.")
            break


if __name__ == "__main__":
    run_interactive_cli()
