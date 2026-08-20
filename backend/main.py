"""
HoloMed AI - Backend Module Entry Point
File: backend/main.py
"""

import os
import sys

# Add parent directory to sys.path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import main

if __name__ == "__main__":
    main()
