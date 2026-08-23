"""
================================================================================
  HoloMed AI - "Ultron" AI Medical Intent Parser & Command Orchestrator
  File: backend/ai_orchestrator.py
================================================================================
Phase 1: The AI & Visual Core

Features:
1. Dynamic Medical Query Engine:
   - Understands clinical questions (e.g., "what is the function of this organ?", "explain the aortic valve", "tell me about coronary artery disease").
   - Generates comprehensive clinical/anatomical responses and packs them into the "ai_speech_response" JSON field.
2. Layer & Pathology State Management:
   - Detects layer visibility commands (e.g., "hide vascular layer", "show muscle layer", "isolate electrical conduction").
   - Detects pathology/disease state toggle commands (e.g., "show diseased state", "simulate myocardial infarction", "switch to healthy model").
   - Formats payloads with "layer_state", "target_layer", "layer_visible", and "pathology" keys.
3. Multi-Axis Hologram Manipulation:
   - Controls rotation, zoom, cross-section slicing, and targeted anatomical highlighting.
4. Non-Blocking UDP Transmission:
   - Streams JSON payloads to Unity on 127.0.0.1:5052 (Voice & AI Core).
"""

import json
import os
import re
import socket
import sys
import threading
import time
from typing import Any, Dict, List, Optional, Tuple

# Try optional speech recognition
try:
    import speech_recognition as sr
    SPEECH_AVAILABLE = True
except ImportError:
    SPEECH_AVAILABLE = False


class HoloMedContextEngine:
    """Tracks the active holographic context, organ, active layer, and pathology state."""

    def __init__(self, default_organ: str = "HEART"):
        self.current_organ: str = default_organ
        self.active_structure: Optional[str] = None
        self.section_view_active: bool = False
        self.current_pathology: str = "HEALTHY"
        self.layer_states: Dict[str, bool] = {
            "VASCULAR": True,
            "MUSCLE": True,
            "ELECTRICAL": True,
            "CHAMBERS": True,
            "PERICARDIUM": True,
        }
        self.zoom_level: float = 1.0
        self.history: List[str] = []

    def update_context(
        self,
        organ: Optional[str] = None,
        structure: Optional[str] = None,
        section_view: Optional[bool] = None,
        pathology: Optional[str] = None,
        layer_update: Optional[Tuple[str, bool]] = None,
    ) -> None:
        if organ:
            self.current_organ = organ.upper()
        if structure is not None:
            self.active_structure = structure.upper() if structure else None
        if section_view is not None:
            self.section_view_active = section_view
        if pathology:
            self.current_pathology = pathology.upper()
        if layer_update:
            layer_name, visible = layer_update
            self.layer_states[layer_name.upper()] = visible

    def get_state(self) -> Dict[str, Any]:
        return {
            "current_organ": self.current_organ,
            "active_structure": self.active_structure,
            "section_view_active": self.section_view_active,
            "current_pathology": self.current_pathology,
            "layer_states": self.layer_states,
            "zoom_level": self.zoom_level,
        }


class UltronAIOrchestrator:
    def __init__(
        self,
        udp_ip: str = "127.0.0.1",
        udp_port: int = 5052,
        use_llm: bool = False,
        openai_api_key: Optional[str] = None,
    ):
        self.udp_ip = udp_ip
        self.udp_port = udp_port
        self.use_llm = use_llm
        self.openai_api_key = openai_api_key or os.getenv("OPENAI_API_KEY")

        # Network Setup
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.context = HoloMedContextEngine(default_organ="HEART")

        # -------------------------------------------------------------
        # Clinical Medical Knowledge Base (Functions & Pathologies)
        # -------------------------------------------------------------
        self.clinical_knowledge: Dict[str, Dict[str, str]] = {
            "HEART": {
                "FUNCTION": "The heart is a muscular organ that pumps deoxygenated blood to the pulmonary circulation for oxygenation and distributes oxygenated blood throughout the systemic circulation via rhythmic contraction cycles (systole and diastole).",
                "PATHOLOGY": "Common cardiac pathologies include coronary artery disease, myocardial infarction, congestive heart failure, valvular stenosis, and arrhythmias such as atrial fibrillation.",
            },
            "LEFT_VENTRICLE": {
                "FUNCTION": "The left ventricle is the thickest cardiac chamber. It receives oxygenated blood from the left atrium through the mitral valve and pumps it against high systemic afterload into the aorta via the aortic valve.",
                "PATHOLOGY": "Pathologies include left ventricular hypertrophy (LVH), dilated cardiomyopathy, and myocardial infarction secondary to left anterior descending (LAD) coronary artery occlusion.",
            },
            "RIGHT_VENTRICLE": {
                "FUNCTION": "The right ventricle pumps deoxygenated blood through the pulmonary semilunar valve into the pulmonary artery toward the pulmonary capillary beds for gas exchange.",
                "PATHOLOGY": "Pathologies include right ventricular strain, cor pulmonale secondary to pulmonary hypertension, and arrhythmogenic right ventricular cardiomyopathy.",
            },
            "LEFT_ATRIUM": {
                "FUNCTION": "The left atrium receives oxygen-rich pulmonary venous return from the four pulmonary veins and contracts in late diastole to fill the left ventricle.",
                "PATHOLOGY": "Left atrial enlargement predisposes patients to thrombus formation and thromboembolic stroke in the setting of atrial fibrillation.",
            },
            "RIGHT_ATRIUM": {
                "FUNCTION": "The right atrium receives systemic deoxygenated venous return from the superior vena cava, inferior vena cava, and coronary sinus, housing the sinoatrial (SA) pacemaker node.",
                "PATHOLOGY": "Associated conditions include atrial septal defects (ASD) and sinoatrial nodal dysfunction (sick sinus syndrome).",
            },
            "AORTA": {
                "FUNCTION": "The aorta is the primary systemic conduit vessel, receiving high-pressure stroke volume from the left ventricle and distributing oxygenated blood to all organ systems through branching vascular beds.",
                "PATHOLOGY": "Critical pathologies include ascending and descending aortic aneurysm, acute Stanford Type A and Type B aortic dissection, and coarctation of the aorta.",
            },
            "AORTIC_VALVE": {
                "FUNCTION": "The aortic valve is a tricuspid semilunar valve that prevents retrograde diastolic backflow of blood from the high-pressure aorta into the left ventricle.",
                "PATHOLOGY": "Pathologies include calcific aortic stenosis leading to systolic ejection murmurs, and aortic regurgitation resulting in left ventricular volume overload.",
            },
            "MITRAL_VALVE": {
                "FUNCTION": "The bicuspid mitral valve regulates unidirectional blood flow from the left atrium to the left ventricle, anchored by chordae tendineae and papillary muscles.",
                "PATHOLOGY": "Major conditions include mitral valve prolapse, rheumatic mitral stenosis, and ischemic mitral regurgitation.",
            },
            "CORONARY_ARTERY": {
                "FUNCTION": "Coronary arteries branch from the aortic root to supply oxygenated blood, nutrients, and electrolytes directly to the active myocardium during ventricular diastole.",
                "PATHOLOGY": "Atherosclerotic plaque accumulation produces coronary ischemia, angina pectoris, and acute ST-elevation myocardial infarction (STEMI).",
            },
            "MYOCARDIUM": {
                "FUNCTION": "The myocardium consists of specialized striated cardiac muscle cells connected by intercalated discs with gap junctions, enabling rapid syncytial electrical conduction and coordinated contraction.",
                "PATHOLOGY": "Myocarditis, ischemic necrosis (infarction), hypertrophic cardiomyopathy, and amyloid infiltration.",
            },
            "BRAIN": {
                "FUNCTION": "The brain coordinates cognitive processing, sensory perception, motor control, emotional regulation, neuroendocrine secretion, and autonomic homeostasis.",
                "PATHOLOGY": "Pathologies include ischemic and hemorrhagic cerebrovascular accidents (stroke), glioblastoma, traumatic brain injury, and neurodegenerative dementias.",
            },
            "LUNGS": {
                "FUNCTION": "The lungs facilitate external respiration, providing continuous alveolar gas exchange between environmental oxygen and venous carbon dioxide across the capillary membrane.",
                "PATHOLOGY": "Pathologies include chronic obstructive pulmonary disease (COPD), acute respiratory distress syndrome (ARDS), pneumonia, and pulmonary embolism.",
            },
        }

        # -------------------------------------------------------------
        # Anatomical Map & Synonyms
        # -------------------------------------------------------------
        self.anatomy_map = {
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
            "aorta": ("HEART", "AORTA", "Locating and highlighting the aorta."),
            "aortic valve": ("HEART", "AORTIC_VALVE", "Focusing on the aortic semilunar valve."),
            "mitral valve": ("HEART", "MITRAL_VALVE", "Focusing on the bicuspid mitral valve."),
            "tricuspid valve": ("HEART", "TRICUSPID_VALVE", "Focusing on the tricuspid valve."),
            "pulmonary valve": ("HEART", "PULMONARY_VALVE", "Focusing on the pulmonary valve."),
            "pulmonary artery": ("HEART", "PULMONARY_ARTERY", "Highlighting the pulmonary artery."),
            "coronary artery": ("HEART", "CORONARY_ARTERY", "Visualizing coronary arterial vasculature."),
            "coronary arteries": ("HEART", "CORONARY_ARTERY", "Visualizing coronary arterial vasculature."),
            "myocardium": ("HEART", "MYOCARDIUM", "Highlighting cardiac muscular tissue."),
            "septum": ("HEART", "INTERVENTRICULAR_SEPTUM", "Highlighting the interventricular septum."),
            "cerebrum": ("BRAIN", "CEREBRUM", "Highlighting the cerebral cortex."),
            "cerebellum": ("BRAIN", "CEREBELLUM", "Highlighting the cerebellum."),
            "brainstem": ("BRAIN", "BRAIN_STEM", "Highlighting the brainstem."),
            "lungs": ("LUNGS", "ALL", "Visualizing the pulmonary respiratory model."),
            "trachea": ("LUNGS", "TRACHEA", "Highlighting the trachea airway."),
        }

    def transmit_payload(self, payload: Dict[str, Any]) -> None:
        """Sends JSON command over UDP to Unity (Port: 5052)."""
        try:
            message = json.dumps(payload, indent=2).encode("utf-8")
            self.sock.sendto(message, (self.udp_ip, self.udp_port))
            print(f"\n[Ultron Orchestrator] >> Transmitted UDP Command to {self.udp_ip}:{self.udp_port}:")
            print(json.dumps(payload, indent=2))
        except Exception as e:
            print(f"[Ultron Orchestrator] UDP Error: {e}")

    def _resolve_target_structure(self, query: str) -> Tuple[str, str]:
        """Resolves target organ and sub-structure from query or context."""
        for term, (organ, part, _) in sorted(self.anatomy_map.items(), key=lambda x: len(x[0]), reverse=True):
            if term in query:
                return organ, part
        return self.context.current_organ, self.context.active_structure or "ORGAN"

    def parse_with_rules(self, text: str) -> Optional[Dict[str, Any]]:
        """High-speed deterministic medical rule engine with clinical reasoning."""
        query = text.lower().strip()

        # =============================================================
        # 1. DYNAMIC MEDICAL QUERIES & CLINICAL FUNCTION INQUIRIES
        # =============================================================
        is_query = any(w in query for w in [
            "function", "what does", "how does", "what is", "explain", "tell me about",
            "clinical", "purpose", "role of", "physiology", "pathophysiology", "symptoms"
        ])
        if is_query:
            organ, structure = self._resolve_target_structure(query)
            lookup_key = structure if structure in self.clinical_knowledge else organ

            if lookup_key in self.clinical_knowledge:
                if any(p in query for p in ["disease", "pathology", "infarct", "stroke", "failure", "stenosis", "abnormal"]):
                    clinical_text = self.clinical_knowledge[lookup_key].get("PATHOLOGY", "")
                else:
                    clinical_text = self.clinical_knowledge[lookup_key].get("FUNCTION", "")
            else:
                clinical_text = f"The {lookup_key.lower().replace('_', ' ')} is an integral anatomical component of the {organ.lower()} system."

            return {
                "command": "MEDICAL_QUERY",
                "organ": organ,
                "target_part": structure,
                "query_text": text,
                "ai_speech_response": clinical_text,
                "auto_zoom": True,
            }

        # =============================================================
        # 2. PATHOLOGY & DISEASE STATE TOGGLES (Healthy vs Diseased)
        # =============================================================
        if any(w in query for w in ["show diseased state", "diseased state", "simulate disease", "show pathology", "simulate infarction", "ischemic state", "myocardial infarction", "diseased model"]):
            self.context.update_context(pathology="DISEASED")
            return {
                "command": "SET_PATHOLOGY",
                "organ": self.context.current_organ,
                "pathology": "DISEASED",
                "ai_speech_response": "Simulating acute ischemic pathology with myocardial tissue discoloration.",
            }

        if any(w in query for w in ["show healthy state", "healthy state", "normal state", "healthy model", "clear pathology", "restore healthy"]):
            self.context.update_context(pathology="HEALTHY")
            return {
                "command": "SET_PATHOLOGY",
                "organ": self.context.current_organ,
                "pathology": "HEALTHY",
                "ai_speech_response": "Restoring healthy baseline anatomical tissue materials.",
            }

        # =============================================================
        # 3. ANATOMICAL LAYER TOGGLE COMMANDS
        # =============================================================
        layer_match = re.search(r'\b(hide|show|toggle|isolate|remove|enable|disable)\s+([a-z\s]+?)\s*(layer|system|vessels)?\b', query)
        if layer_match or any(w in query for w in ["vascular", "muscle", "myocardium", "electrical", "conduction", "chambers", "pericardium"]):
            action_word = "hide" if any(w in query for w in ["hide", "remove", "disable", "peel"]) else "show"
            is_visible = action_word == "show"

            target_layer = "ALL"
            if "vascular" in query or "vessel" in query or "coronary" in query or "arter" in query:
                target_layer = "VASCULAR"
            elif "muscle" in query or "myocardium" in query:
                target_layer = "MUSCLE"
            elif "electrical" in query or "conduction" in query or "pacemaker" in query:
                target_layer = "ELECTRICAL"
            elif "chamber" in query or "interior" in query:
                target_layer = "CHAMBERS"
            elif "pericardium" in query:
                target_layer = "PERICARDIUM"

            if target_layer != "ALL" or "layer" in query:
                self.context.update_context(layer_update=(target_layer, is_visible))
                state_str = "VISIBLE" if is_visible else "HIDDEN"
                return {
                    "command": "LAYER_TOGGLE",
                    "organ": self.context.current_organ,
                    "target_layer": target_layer,
                    "layer_visible": is_visible,
                    "layer_state": f"{target_layer}_{state_str}",
                    "ai_speech_response": f"{'Enabling' if is_visible else 'Hiding'} the {target_layer.lower()} layer on the {self.context.current_organ.lower()} model.",
                }

        # =============================================================
        # 4. SECTION / CUTAWAY / DISSECTION / SLICE
        # =============================================================
        if re.search(r'\b(cut|cutaway|section\s+view|cross\s+section|slice|internal\s+view|inside|dissect|open\s+up)\b', query):
            self.context.update_context(section_view=True)
            return {
                "command": "SECTION_VIEW",
                "organ": self.context.current_organ,
                "action": "TOGGLE_CUTAWAY",
                "ai_speech_response": "Executing surgical cross-section plane cut to expose internal cardiac anatomy.",
            }

        # =============================================================
        # 5. RESET / DEFAULT VIEW
        # =============================================================
        if any(w in query for w in ["reset", "default view", "original view", "clear highlight", "unhide", "center view", "recenter"]):
            self.context.update_context(structure=None, section_view=False, pathology="HEALTHY")
            return {
                "command": "RESET",
                "organ": self.context.current_organ,
                "target_part": "ALL",
                "pathology": "HEALTHY",
                "ai_speech_response": f"Resetting {self.context.current_organ.lower()} viewport, layers, and highlights.",
            }

        # =============================================================
        # 6. DICOM RADIOLOGICAL WINDOW LEVELING
        # =============================================================
        if re.search(r'\b(window\s+level|windowing|contrast|preset|density\s+window)\b', query) or any(w in query for w in ["angiography window", "bone window", "lung window", "soft tissue window"]):
            preset = "CARDIAC_SOFT_TISSUE"
            if "angio" in query or "coronary" in query or "contrast" in query:
                preset = "CORONARY_ANGIO"
            elif "bone" in query or "chest wall" in query or "skelet" in query:
                preset = "BONE_CHEST_WALL"
            elif "lung" in query or "airway" in query:
                preset = "LUNG_PARENCHYMA"
            elif "brain" in query or "neuro" in query:
                preset = "BRAIN_TISSUE"

            return {
                "command": "SET_WINDOW_LEVEL",
                "organ": self.context.current_organ,
                "preset": preset,
                "ai_speech_response": f"Applying radiological {preset.replace('_', ' ').lower()} window leveling preset.",
            }

        # =============================================================
        # 7. ZOOM COMMANDS
        # =============================================================
        if any(w in query for w in ["zoom in", "magnify", "enlarge", "closer", "zoom closer"]):
            return {
                "command": "ZOOM",
                "organ": self.context.current_organ,
                "value": 1.5,
                "ai_speech_response": "Zooming in.",
            }
        elif any(w in query for w in ["zoom out", "shrink", "reduce", "further away", "pull back"]):
            return {
                "command": "ZOOM",
                "organ": self.context.current_organ,
                "value": -1.5,
                "ai_speech_response": "Zooming out.",
            }

        # =============================================================
        # 7. ROTATION COMMANDS
        # =============================================================
        if any(w in query for w in ["rotate left", "turn left", "spin left"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "direction": "LEFT",
                "axis": "Y",
                "value": -45.0,
                "ai_speech_response": "Rotating model left.",
            }
        elif any(w in query for w in ["rotate right", "turn right", "spin right"]):
            return {
                "command": "ROTATE",
                "target": self.context.current_organ,
                "direction": "RIGHT",
                "axis": "Y",
                "value": 45.0,
                "ai_speech_response": "Rotating model right.",
            }

        # =============================================================
        # 8. ORGAN MODEL SWITCHING
        # =============================================================
        if any(w in query for w in ["show heart", "switch to heart", "load heart", "cardiac model", "cardiovascular"]):
            self.context.update_context(organ="HEART", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "HEART",
                "target_part": "ALL",
                "ai_speech_response": "Loading 3D cardiovascular heart model.",
            }
        elif any(w in query for w in ["show brain", "switch to brain", "load brain", "neural model"]):
            self.context.update_context(organ="BRAIN", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "BRAIN",
                "target_part": "ALL",
                "ai_speech_response": "Loading 3D neural brain model.",
            }
        elif any(w in query for w in ["show lungs", "switch to lungs", "load lungs", "respiratory model"]):
            self.context.update_context(organ="LUNGS", structure=None)
            return {
                "command": "LOAD_ORGAN",
                "organ": "LUNGS",
                "target_part": "ALL",
                "ai_speech_response": "Loading 3D respiratory lungs model.",
            }

        # =============================================================
        # 9. ANATOMICAL TARGET HIGHLIGHTING
        # =============================================================
        for term, (organ, target_part, speech_text) in sorted(self.anatomy_map.items(), key=lambda x: len(x[0]), reverse=True):
            if term in query:
                self.context.update_context(organ=organ, structure=target_part)
                return {
                    "command": "HIGHLIGHT",
                    "organ": organ,
                    "target_part": target_part,
                    "auto_zoom": True,
                    "ai_speech_response": speech_text,
                }

        return None

    def process_command(self, text: str) -> Dict[str, Any]:
        """Main entry point: parses text, generates clinical payload, and transmits via UDP."""
        print(f"\n[Ultron Voice/Text Input] -> '{text}'")

        command_payload = self.parse_with_rules(text)

        if not command_payload:
            command_payload = {
                "command": "UNKNOWN",
                "organ": self.context.current_organ,
                "target_part": None,
                "ai_speech_response": f"I didn't recognize a specific medical command in: '{text}'. You can ask about organ function, toggle layers, or simulate pathologies.",
            }

        self.transmit_payload(command_payload)
        return command_payload


def run_interactive_cli():
    orchestrator = UltronAIOrchestrator()
    print("=" * 70)
    print("  HoloMed AI - 'Ultron' Medical Command Orchestrator (Port 5052)")
    print("=" * 70)
    print("Examples:")
    print("  - Clinical Query: 'What is the function of the left ventricle?'")
    print("  - Layer Toggle:   'Hide vascular layer', 'Show muscle layer'")
    print("  - Pathology:      'Show diseased state', 'Show healthy state'")
    print("  - Manipulation:   'Rotate right', 'Slice open', 'Highlight aorta'")
    print("=" * 70)

    # Run quick automated verification test
    test_queries = [
        "What is the function of the left ventricle?",
        "Hide vascular layer",
        "Show diseased state",
        "Highlight aorta",
        "Show healthy state",
        "Cut it open",
    ]
    print("\n[Self-Test] Running Phase 1 verification queries...")
    for q in test_queries:
        orchestrator.process_command(q)
        time.sleep(0.05)

    print("\n[Ultron Ready] Enter queries below (or 'exit' to quit):")
    while True:
        try:
            user_input = input("\n[HoloMed AI Prompt] > ").strip()
            if not user_input:
                continue
            if user_input.lower() in ["exit", "quit"]:
                break
            orchestrator.process_command(user_input)
        except (KeyboardInterrupt, EOFError):
            print("\nExiting Ultron Orchestrator.")
            break


if __name__ == "__main__":
    run_interactive_cli()
