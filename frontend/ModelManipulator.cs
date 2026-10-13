using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Model Manipulator Component
    /// Attached to the 3D anatomical organ (e.g. Heart GameObject).
    /// Handles main-thread queued direct manipulation for:
    /// - Rotation (dx, dy)
    /// - Zoom / Scaling (dz, scale, delta)
    /// - Move / Translation / Pan (dx, dy)
    /// </summary>
    [DisallowMultipleComponent]
    public class ModelManipulator : MonoBehaviour
    {
        public static ModelManipulator Instance { get; private set; }

        [Header("Motion Calibration")]
        [Tooltip("Pan / Translation speed multiplier.")]
        [SerializeField] private float moveSpeed = 1.0f;

        [Tooltip("Zoom / Scale speed multiplier.")]
        [SerializeField] private float zoomSpeed = 1.0f;

        [Tooltip("Rotation speed multiplier.")]
        [SerializeField] private float rotationSpeed = 1.0f;

        [Header("Scale Boundaries")]
        [SerializeField] private float minScale = 0.1f;
        [SerializeField] private float maxScale = 5.0f;

        [Header("Diagnostics")]
        [SerializeField] private bool verboseLogging = false;

        // Thread-safe main-thread execution queue
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

        // Cache initial transform
        private Vector3 initialPosition;
        private Quaternion initialRotation;
        private Vector3 initialScale;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            initialPosition = transform.localPosition;
            initialRotation = transform.localRotation;
            initialScale = transform.localScale;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Unity Update loop: executes all queued actions on the Main Render Thread.
        /// </summary>
        private void Update()
        {
            // Reset transform explicitly bound to the 'r' key
            if (Input.GetKeyDown(KeyCode.R))
            {
                ResetTransform();
            }

            while (_executionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ModelManipulator] Main thread execution error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Enqueues a generic action to the main thread execution queue.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action != null)
            {
                _executionQueue.Enqueue(action);
            }
        }

        /// <summary>
        /// 1. Zoom (Scaling):
        /// Applies local scaling to the Heart object within clamped boundaries [0.1f - 5.0f].
        /// </summary>
        public void Zoom(float delta)
        {
            _executionQueue.Enqueue(() =>
            {
                Debug.Log($"[HoloMed] Zooming Heart delta:{delta}");
                Vector3 newScale = transform.localScale + (Vector3.one * delta * zoomSpeed);
                transform.localScale = Vector3.Max(Vector3.one * minScale, Vector3.Min(newScale, Vector3.one * maxScale));
            });
        }

        /// <summary>
        /// Alias for Zoom to match standard naming.
        /// </summary>
        public void ZoomModel(float delta) => Zoom(delta);

        /// <summary>
        /// 2. Move (Translation/Pan):
        /// Applies translation to the Heart object in World Space.
        /// </summary>
        public void Move(float dx, float dy)
        {
            _executionQueue.Enqueue(() =>
            {
                Debug.Log($"[HoloMed] Moving Heart dx:{dx} dy:{dy}");
                transform.Translate(new Vector3(dx * moveSpeed, -dy * moveSpeed, 0), Space.World);
            });
        }

        /// <summary>
        /// Alias for Move to match standard naming.
        /// </summary>
        public void MoveModel(float dx, float dy) => Move(dx, dy);

        /// <summary>
        /// Rotation:
        /// Applies continuous rotation around World Y (horizontal) and World X (vertical).
        /// </summary>
        public void Rotate(float dx, float dy)
        {
            _executionQueue.Enqueue(() =>
            {
                Debug.Log($"[HoloMed] Rotating Heart dx:{dx} dy:{dy}");
                transform.Rotate(Vector3.up, -dx * rotationSpeed, Space.World);
                transform.Rotate(Vector3.right, dy * rotationSpeed, Space.World);
            });
        }

        /// <summary>
        /// Alias for Rotate to match standard naming.
        /// </summary>
        public void RotateModel(float dx, float dy) => Rotate(dx, dy);

        /// <summary>
        /// Resets the transform back to its initial spawn state.
        /// </summary>
        public void ResetTransform()
        {
            _executionQueue.Enqueue(() =>
            {
                transform.localPosition = initialPosition;
                transform.localRotation = initialRotation;
                transform.localScale = initialScale;
                Debug.Log("[HoloMed] Reset Heart transform to initial state.");
            });
        }
    }
}
