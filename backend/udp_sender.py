import socket
import json
import time

UDP_IP = "127.0.0.1" 
UDP_PORT = 5052

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

print(f"Starting UDP stream to port {UDP_PORT}...")

try:
    while True:
        payload = {
            "command": "ROTATE",
            "target": "HEART",
            "axis": "Y",
            "value": 1.0 
        }
        
        message = json.dumps(payload).encode('utf-8')
        sock.sendto(message, (UDP_IP, UDP_PORT))
        print(f"Sent: {payload}")
        
        time.sleep(1)
except KeyboardInterrupt:
    print("\nStopping stream.")
    sock.close()