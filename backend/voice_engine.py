import sounddevice as sd
from scipy.io.wavfile import write
import speech_recognition as sr
import pyttsx3
import socket
import json
import os
import numpy as np

# UDP Network Settings
UDP_IP = "127.0.0.1"
UDP_PORT = 5052
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# Audio Settings (Forcing Device 2: Headset M-801)
RATE = 16000
DURATION = 4
MIC_DEVICE = 2 
TEMP_FILE = "temp_voice.wav"

recognizer = sr.Recognizer()

# Initialize Ultron's Voice
engine = pyttsx3.init()
engine.setProperty('rate', 170)

def speak(text):
    """Makes the computer speak out loud."""
    print(f"🤖 Ultron says: {text}")
    engine.say(text)
    engine.runAndWait()

def send_command(action):
    """Sends the command to Unity."""
    payload = {"type": "VOICE", "action": action}
    sock.sendto(json.dumps(payload).encode('utf-8'), (UDP_IP, UDP_PORT))

print("="*80)
print(" HoloMed AI - Talking Voice Engine (Wake Word: ULTRON)")
print("="*80)
speak("System online. Waiting for your command, Doctor.")

try:
    while True:
        # 1. Record as float32 for clean capture
        audio_data = sd.rec(int(DURATION * RATE), samplerate=RATE, channels=1, dtype='float32', device=MIC_DEVICE)
        sd.wait() 
        
        # 2. Normalize and convert to standard 16-bit PCM WAV format
        audio_data = (audio_data * 32767).astype(np.int16)
        write(TEMP_FILE, RATE, audio_data)
        
        # 3. Read the file with SpeechRecognition
        with sr.AudioFile(TEMP_FILE) as source:
            audio_file = recognizer.record(source)
            
        try:
            text = recognizer.recognize_google(audio_file).lower()
            
            if "ultron" in text:
                print(f"\n🎙️ You said: '{text}'")
                
                if "rotate" in text and "right" in text:
                    speak("Rotating right.")
                    send_command("ROTATE_WRIST_CW")
                elif "rotate" in text and "left" in text:
                    speak("Rotating left.")
                    send_command("ROTATE_WRIST_CCW")
                elif "zoom" in text and "in" in text:
                    speak("Magnifying the model.")
                    send_command("ZOOM_SPREAD")
                elif "zoom" in text and "out" in text:
                    speak("Zooming out.")
                    send_command("ZOOM_CLOSE")
                elif "slice" in text or "cut" in text:
                    speak("Deploying cross section view.")
                    send_command("CROSS_SECTION_SPLIT")
                elif "stop" in text or "freeze" in text:
                    speak("Motion frozen.")
                    send_command("PALM_STOP")
                elif "reset" in text or "center" in text:
                    speak("Resetting workstation.")
                    send_command("EMERGENCY_RESET")
                else:
                    speak("I heard my name, but I did not recognize the command.")

        except sr.UnknownValueError:
            pass
        except sr.RequestError:
            print("\n❌ Network error.")

except KeyboardInterrupt:
    speak("Shutting down.")
finally:
    if os.path.exists(TEMP_FILE):
        os.remove(TEMP_FILE)
    sock.close() 