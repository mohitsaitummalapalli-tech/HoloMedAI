using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
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

        [Header("Movement / Translation Settings")]
        [Tooltip("Translation sensitivity multiplier for hand move / pan")]
        [SerializeField] private float moveSpeed = 1.0f;

        private Vector3 initialPosition;
        private Quaternion initialRotation;
        private Vector3 initialScale;

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

        public void RotateModel(float dx, float dy)
        {
            if (targetModel == null) return;

            if (float.IsNaN(dx) || float.IsInfinity(dx)) dx = 0f;
            if (float.IsNaN(dy) || float.IsInfinity(dy)) dy = 0f;

            dx = Mathf.Clamp(dx, -maxRotationDeltaPerPacket, maxRotationDeltaPerPacket);
            dy = Mathf.Clamp(dy, -maxRotationDeltaPerPacket, maxRotationDeltaPerPacket);

            float euclideanDist = Mathf.Sqrt(dx * dx + dy * dy);
            if (euclideanDist < rotationDeadZone)
            {
                currentFilteredDx = 0f;
                currentFilteredDy = 0f;
                return;
            }

            if (Mathf.Abs(dx) < rotationDeadZone) dx = 0f;
            if (Mathf.Abs(dy) < rotationDeadZone) dy = 0f;

            if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
            {
                currentFilteredDx = 0f;
                currentFilteredDy = 0f;
                return;
            }

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

            float effectiveDx = invertHorizontal ? -finalDx : finalDx;
            float effectiveDy = invertVertical ? -finalDy : finalDy;

            float rotY = -effectiveDx * rotationSensitivity;
            float rotX = effectiveDy * rotationSensitivity;

            targetModel.Rotate(Vector3.up, rotY, Space.World);
            targetModel.Rotate(Vector3.right, rotX, Space.World);
        }

        public void ZoomModel(float distanceDelta)
        {
            if (targetModel == null) return;

            if (float.IsNaN(distanceDelta) || float.IsInfinity(distanceDelta)) return;

            distanceDelta = Mathf.Clamp(distanceDelta, -maxZoomDeltaPerPacket, maxZoomDeltaPerPacket);

            if (Mathf.Abs(distanceDelta) < zoomDeadZone) return;

            Vector3 newScale = targetModel.localScale + (Vector3.one * distanceDelta * zoomSensitivity);
            targetModel.localScale = Vector3.Max(Vector3.one * minScale, Vector3.Min(newScale, Vector3.one * maxScale));
        }

        public void MoveModel(float dx, float dy)
        {
            if (targetModel == null) return;
            if (float.IsNaN(dx) || float.IsInfinity(dx)) dx = 0f;
            if (float.IsNaN(dy) || float.IsInfinity(dy)) dy = 0f;

            targetModel.Translate(new Vector3(dx * moveSpeed, -dy * moveSpeed, 0), Space.World);
        }

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
