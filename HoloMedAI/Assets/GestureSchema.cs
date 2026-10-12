using System;
using UnityEngine;

/// <summary>
/// HoloMed AI - Gesture Subsystem Data Contracts & JSON Schemas
/// File: GestureSchema.cs
/// 
/// Strictly defines incoming JSON command structures from external Computer Vision systems.
/// Compatible with UnityEngine.JsonUtility.
/// </summary>
namespace HoloMedAI.Gestures
{
    public enum GestureLogLevel
    {
        NONE = 0,
        ERROR = 1,
        WARNING = 2,
        INFO = 3,
        DEBUG = 4
    }

    public enum InteractionState
    {
        IDLE,
        CANDIDATE,
        CONFIRMED,
        ACTIVE,
        COMPLETE
    }

    public enum NavigationDirection
    {
        UNKNOWN,
        LEFT,
        RIGHT,
        UP,
        DOWN
    }

    #region JSON Serialization Data Contracts

    [Serializable]
    public class GestureCommandPayload
    {
        // SELECT_STRUCTURE fields
        public string organ;
        public string structure;

        // ROTATE_MODEL fields
        public float dx;
        public float dy;

        // ZOOM_MODEL fields
        public float distanceDelta;
        public float zoomFactor;
        public float delta;

        // NAVIGATE fields
        public string direction;
        public float magnitude;

        // State & Meta fields
        public string state;
        public float value;
    }

    [Serializable]
    public class CoreGestureCommand
    {
        public string command;
        public string action;
        public string state;
        public string target;
        public float confidence = 1.0f;
        public long timestamp = 0;
        public GestureCommandPayload payload;

        // Flat JSON fallbacks for flexible UDP compatibility
        public string organ;
        public string structure;
        public float dx;
        public float dy;
        public float distanceDelta;
        public string direction;
        public float magnitude;
    }

    #endregion
}
