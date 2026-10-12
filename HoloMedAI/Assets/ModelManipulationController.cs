using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Category 1: Direct 3D Model Manipulation Controller
    /// File: ModelManipulationController.cs
    /// 
    /// Direct Hand-Controlled Rotation (NO Auto-Spin, NO Momentum, NO Artificial 360° Limits):
    /// - Rotation amount and direction directly driven by user hand movement delta (dx, dy).
    /// - Hand stationary -> Model stationary (zero rotation).
    /// - Hand moves -> Model rotates by delta * sensitivity.
    /// - Hand stops -> Model stops rotating immediately.
    /// - Cumulative continuous rotation: 45°, 180°, 360°, 720° without artificial clamping.
    /// - Configurable dead-zone, sensitivity, and axis inversion in Unity Inspector.
    /// - Relative two-finger zoom with min/max scale clamping and dead-zone.
    /// </summary>
    public class ModelManipulationController : MonoBehaviour, IModelManipulationController
    {
        [Header("Target Hologram")]
        [Tooltip("Assign the root Transform of your 3D Anatomical Organ (e.g. Heart)")]
        [SerializeField] private Transform targetModel;

        [Header("Rotation Settings")]
        [Tooltip("Rotation sensitivity multiplier for horizontal & vertical hand movement")]
        [Range(10f, 300f)]
        [SerializeField] private float rotationSensitivity = 120f;

        [Tooltip("Dead-zone threshold for movement delta to eliminate micro-jitter and tracking noise")]
        [Range(0.001f, 0.05f)]
        [SerializeField] private float rotationDeadZone = 0.004f;

        [Tooltip("Maximum allowed single-frame delta to prevent jumps from malformed packets")]
        [SerializeField] private float maxRotationDeltaPerPacket = 1.0f;

        [Header("Axis Inversion")]
        [Tooltip("Invert horizontal rotation direction")]
        [SerializeField] private bool invertHorizontal = false;

        [Tooltip("Invert vertical rotation direction")]
        [SerializeField] private bool invertVertical = false;

        [Header("Delta Smoothing (Optional)")]
        [Tooltip("Enable smooth filtering on incoming delta without introducing drift or momentum")]
        [SerializeField] private bool enableDeltaSmoothing = false;

        [Tooltip("Smoothing factor for delta filter (higher = more responsive, lower = heavier filter)")]
        [Range(5f, 50f)]
        [SerializeField] private float deltaSmoothingSpeed = 25f;

        [Header("Zoom Settings")]
        [Tooltip("Zoom sensitivity multiplier for two-finger distance delta")]
        [Range(0.5f, 10f)]
        [SerializeField] private float zoomSensitivity = 3.0f;

        [Tooltip("Minimum allowable model scale")]
        [SerializeField] private float minScale = 0.25f;

        [Tooltip("Maximum allowable model scale")]
        [SerializeField] private float maxScale = 4.5f;

        [Tooltip("Dead-zone for zoom distance delta to eliminate camera noise")]
        [Range(0.001f, 0.05f)]
        [SerializeField] private float zoomDeadZone = 0.003f;

        [Tooltip("Maximum allowed zoom delta before clamping malformed packets")]
        [SerializeField] private float maxZoomDeltaPerPacket = 0.5f;

        // Baseline Reference Coordinates
        private Vector3 initialPosition;
        private Quaternion initialRotation;
        private Vector3 initialScale;

        // Filtered Delta State
        private float currentFilteredDx = 0f;
        private float currentFilteredDy = 0f;

        public Transform TargetModel
        {
            get => targetModel;
            set
            {
                targetModel = value;
                CacheInitialState();
            }
        }

        public bool InvertHorizontal
        {
            get => invertHorizontal;
            set => invertHorizontal = value;
        }

        public bool InvertVertical
        {
            get => invertVertical;
            set => invertVertical = value;
        }

        public float RotationSensitivity
        {
            get => rotationSensitivity;
            set => rotationSensitivity = value;
        }

        public float RotationDeadZone
        {
            get => rotationDeadZone;
            set => rotationDeadZone = value;
        }

        private void Awake()
        {
            if (targetModel == null)
            {
                targetModel = transform;
            }
            CacheInitialState();
        }

        private void CacheInitialState()
        {
            if (targetModel == null) return;
            initialPosition = targetModel.localPosition;
            initialRotation = targetModel.localRotation;
            initialScale = targetModel.localScale;
        }

        /// <summary>
        /// Applies direct relative rotation to targetModel based on user hand movement delta.
        /// Rotates ONLY when hand moves. Zero rotation when hand is stationary.
        /// NO automatic spin, NO momentum drift.
        /// </summary>
        public void RotateModel(float dx, float dy)
        {
            if (targetModel == null) return;

            // 1. Sanitize against NaN, Infinity, and malformed extreme values
            if (float.IsNaN(dx) || float.IsInfinity(dx)) dx = 0f;
            if (float.IsNaN(dy) || float.IsInfinity(dy)) dy = 0f;

            dx = Mathf.Clamp(dx, -maxRotationDeltaPerPacket, maxRotationDeltaPerPacket);
            dy = Mathf.Clamp(dy, -maxRotationDeltaPerPacket, maxRotationDeltaPerPacket);

            // 2. Apply Euclidean Dead-Zone threshold (suppresses minor hand tremor at rest with zero delta)
            float euclideanDist = Mathf.Sqrt(dx * dx + dy * dy);
            if (euclideanDist < rotationDeadZone)
            {
                currentFilteredDx = 0f;
                currentFilteredDy = 0f;
                return;
            }

            if (Mathf.Abs(dx) < rotationDeadZone) dx = 0f;
            if (Mathf.Abs(dy) < rotationDeadZone) dy = 0f;

            // Hand stationary -> early exit (zero rotation)
            if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
            {
                currentFilteredDx = 0f;
                currentFilteredDy = 0f;
                return;
            }

            // 3. Optional Delta Filter (filters input noise without momentum/drift)
            float finalDx = dx;
            float finalDy = dy;
            if (enableDeltaSmoothing)
            {
                float t = Mathf.Clamp01(Time.deltaTime * deltaSmoothingSpeed);
                currentFilteredDx = Mathf.Lerp(currentFilteredDx, dx, t);
                currentFilteredDy = Mathf.Lerp(currentFilteredDy, dy, t);
                finalDx = currentFilteredDx;
                finalDy = currentFilteredDy;
            }

            // 4. Inversion mapping
            float effectiveDx = invertHorizontal ? -finalDx : finalDx;
            float effectiveDy = invertVertical ? -finalDy : finalDy;

            // 5. Compute rotation delta degrees
            // Horizontal hand movement (dx) rotates around World Y axis
            // Vertical hand movement (dy) rotates around World X axis
            float rotY = -effectiveDx * rotationSensitivity;
            float rotX = effectiveDy * rotationSensitivity;

            // 6. Direct Relative Rotation (accumulates continuously to 45°, 180°, 360°, 720°, etc.)
            targetModel.Rotate(Vector3.up, rotY, Space.World);
            targetModel.Rotate(Vector3.right, rotX, Space.World);
        }

        /// <summary>
        /// Smoothly modifies targetModel local scale based on relative two-finger distance delta.
        /// </summary>
        public void ZoomModel(float distanceDelta)
        {
            if (targetModel == null) return;

            // 1. Sanitize against NaN, Infinity, and extreme values
            if (float.IsNaN(distanceDelta) || float.IsInfinity(distanceDelta)) return;

            distanceDelta = Mathf.Clamp(distanceDelta, -maxZoomDeltaPerPacket, maxZoomDeltaPerPacket);

            // 2. Apply Dead-Zone threshold
            if (Mathf.Abs(distanceDelta) < zoomDeadZone) return;

            // 3. Compute relative scale delta and clamp within safe limits
            float scaleAdjustment = distanceDelta * zoomSensitivity;
            float currentScale = targetModel.localScale.x;
            float newScale = Mathf.Clamp(currentScale + scaleAdjustment, minScale, maxScale);

            targetModel.localScale = new Vector3(newScale, newScale, newScale);
        }

        /// <summary>
        /// Clears manipulation delta and stops rotation immediately (called on PINCH_RELEASE).
        /// </summary>
        public void StopRotation()
        {
            currentFilteredDx = 0f;
            currentFilteredDy = 0f;
        }

        public void ResetTransform()
        {
            if (targetModel == null) return;
            targetModel.localPosition = initialPosition;
            targetModel.localRotation = initialRotation;
            targetModel.localScale = initialScale;
            currentFilteredDx = 0f;
            currentFilteredDy = 0f;
        }
    }
}
