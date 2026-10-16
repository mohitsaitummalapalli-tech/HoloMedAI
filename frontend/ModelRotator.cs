using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Buttery Smooth 3D Model Rotator
    /// File: frontend/ModelRotator.cs (Mirrored from Assets/Scripts/ModelRotator.cs)
    /// 
    /// Features:
    /// - Continuous spherical linear interpolation (Quaternion.Slerp) on Update()
    /// - Configurable deadzone threshold to eliminate micro-jitter and tracking noise
    /// - Smooth momentum / decay dampening when hand stops moving
    /// - World-space horizontal (yaw) and vertical (pitch) rotation without gimbal lock
    /// - Direct API for UDP receivers on port 5052
    /// </summary>
    [DisallowMultipleComponent]
    public class ModelRotator : MonoBehaviour
    {
        public static ModelRotator Instance { get; private set; }

        [Header("Target Model")]
        [Tooltip("Assign the target Transform (e.g. Heart). Defaults to this transform.")]
        [SerializeField] private Transform targetModel;

        public Transform TargetModel
        {
            get => targetModel != null ? targetModel : transform;
            set
            {
                targetModel = value;
                if (targetModel != null)
                {
                    initialRotation = targetModel.localRotation;
                    targetRotation = targetModel.localRotation;
                }
            }
        }

        [Header("Slerp Smoothing & Dynamics")]
        [Tooltip("Interpolation speed for Quaternion.Slerp (higher = snappier, lower = smoother).")]
        [Range(1f, 30f)]
        [SerializeField] private float slerpSpeed = 15f;

        [Tooltip("Multiplier for incoming rotation delta.")]
        [Range(0.1f, 5f)]
        [SerializeField] private float rotationSpeed = 1.0f;

        [Header("Deadzone & Filtering")]
        [Tooltip("Movement threshold below which deltas are ignored to suppress sensor tremor.")]
        [Range(0.0001f, 0.05f)]
        [SerializeField] private float deadzone = 0.003f;

        [Tooltip("Maximum angle delta allowed per frame to prevent sudden camera spin jumps.")]
        [SerializeField] private float maxDeltaClamp = 45f;

        [Header("Axis Inversion")]
        [SerializeField] private bool invertX = false;
        [SerializeField] private bool invertY = false;

        private Quaternion initialRotation;
        private Quaternion targetRotation;
        private bool isInitialized = false;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            if (targetModel == null)
            {
                targetModel = transform;
            }

            initialRotation = targetModel.localRotation;
            targetRotation = targetModel.localRotation;
            isInitialized = true;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Applies delta rotation from perception UDP telemetry stream.
        /// Rotates horizontally around World Y (-dx) and vertically around World X (dy).
        /// </summary>
        public void Rotate(float dx, float dy)
        {
            if (!isInitialized || targetModel == null) return;

            // 1. Sanitize against NaN and Infinity
            if (float.IsNaN(dx) || float.IsInfinity(dx)) dx = 0f;
            if (float.IsNaN(dy) || float.IsInfinity(dy)) dy = 0f;

            // 2. Deadzone check to eliminate sensor noise and rest tremor
            float magnitude = Mathf.Sqrt(dx * dx + dy * dy);
            if (magnitude < deadzone) return;

            // Soft-knee scaling above deadzone
            float scale = (magnitude - deadzone) / magnitude;
            float effDx = dx * scale;
            float effDy = dy * scale;

            if (invertX) effDx = -effDx;
            if (invertY) effDy = -effDy;

            // 3. Compute frame angle deltas
            float angleY = -effDx * rotationSpeed;
            float angleX = effDy * rotationSpeed;

            angleY = Mathf.Clamp(angleY, -maxDeltaClamp, maxDeltaClamp);
            angleX = Mathf.Clamp(angleX, -maxDeltaClamp, maxDeltaClamp);

            // 4. Update target quaternion in world space orientation
            Quaternion deltaRot = Quaternion.AngleAxis(angleY, Vector3.up) * Quaternion.AngleAxis(angleX, Vector3.right);
            targetRotation = deltaRot * targetRotation;
        }

        /// <summary>
        /// Alias matching standard naming conventions.
        /// </summary>
        public void RotateModel(float dx, float dy) => Rotate(dx, dy);

        private void Update()
        {
            if (targetModel == null) return;

            // Buttery smooth Slerp towards targetRotation on every render frame
            targetModel.localRotation = Quaternion.Slerp(
                targetModel.localRotation,
                targetRotation,
                Time.deltaTime * slerpSpeed
            );
        }

        /// <summary>
        /// Resets rotation to initial baseline.
        /// </summary>
        public void ResetRotation()
        {
            targetRotation = initialRotation;
            if (targetModel != null)
            {
                targetModel.localRotation = initialRotation;
            }
            Debug.Log("[ModelRotator] Rotation reset to initial baseline.");
        }
    }
}
