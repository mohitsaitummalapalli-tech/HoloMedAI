using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    public enum GestureLogLevel { NONE = 0, ERROR = 1, WARNING = 2, INFO = 3, DEBUG = 4 }
    public enum InteractionState { IDLE, CANDIDATE, CONFIRMED, ACTIVE, COMPLETE }
    public enum NavigationDirection { UNKNOWN, LEFT, RIGHT, UP, DOWN }

    [Serializable]
    public class GestureCommandPayload
    {
        public string organ;
        public string structure;
        public float dx;
        public float dy;
        public float distanceDelta;
        public float zoomFactor;
        public float delta;
        public string direction;
        public float magnitude;
        public string state;
        public float value;
    }

    [Serializable]
    public class CoreGestureCommand
    {
        public string command;
        public float confidence = 1.0f;
        public long timestamp = 0;
        public GestureCommandPayload payload;

        public string organ;
        public string structure;
        public float dx;
        public float dy;
        public float distanceDelta;
        public string direction;
        public float magnitude;
    }
}
