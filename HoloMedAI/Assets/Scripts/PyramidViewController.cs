using System;
using UnityEngine;

namespace HoloMedAI
{
    /// <summary>
    /// HoloMed AI - 4-Way Camera Split-Screen Pyramid View Controller.
    /// Configures 4 perspective projection viewports on a pure #000000 black background
    /// aligned for 4-sided optical acrylic hologram pyramid reflection.
    /// </summary>
    public class PyramidViewController : MonoBehaviour
    {
        [Header("Target Focus Point")]
        [Tooltip("Transform representing the center of the hologram projection")]
        public Transform targetFocus;

        [Header("Camera Positioning")]
        public float cameraDistance = 2.5f;
        public float cameraHeight = 0.0f;
        public float fieldOfView = 45.0f;

        [Header("Holographic Display Mode")]
        public bool isPyramidActive = false;

        private Camera _mainCamera;
        private Camera _camNorth;
        private Camera _camSouth;
        private Camera _camEast;
        private Camera _camWest;
        private GameObject _pyramidCamerasRoot;

        void Awake()
        {
            if (targetFocus == null)
            {
                GameObject heartObj = GameObject.Find("Human Heart");
                if (heartObj != null) targetFocus = heartObj.transform;
                else targetFocus = this.transform;
            }

            _mainCamera = Camera.main;
            SetupPyramidCameras();
        }

        void OnEnable()
        {
            UdpTelemetryReceiver.OnPyramidViewChanged += SetPyramidView;
        }

        void OnDisable()
        {
            UdpTelemetryReceiver.OnPyramidViewChanged -= SetPyramidView;
        }

        private void SetupPyramidCameras()
        {
            _pyramidCamerasRoot = new GameObject("PyramidCamerasRoot");
            _pyramidCamerasRoot.transform.SetParent(this.transform);

            // 1. South Camera (Bottom Viewport, Standard Orientation)
            _camSouth = CreatePyramidCamera("Cam_South", new Rect(0.25f, 0.0f, 0.5f, 0.5f), 0f, 0f);

            // 2. North Camera (Top Viewport, Inverted for Top Pyramid Face)
            _camNorth = CreatePyramidCamera("Cam_North", new Rect(0.25f, 0.5f, 0.5f, 0.5f), 180f, 180f);

            // 3. West Camera (Left Viewport, Rotated 90 Deg)
            _camWest = CreatePyramidCamera("Cam_West", new Rect(0.0f, 0.25f, 0.5f, 0.5f), 90f, 90f);

            // 4. East Camera (Right Viewport, Rotated -90 Deg)
            _camEast = CreatePyramidCamera("Cam_East", new Rect(0.5f, 0.25f, 0.5f, 0.5f), -90f, -90f);

            // Initial State
            ApplyPyramidMode(isPyramidActive);
        }

        private Camera CreatePyramidCamera(string name, Rect viewportRect, float yawAngle, float rollAngle)
        {
            GameObject camObj = new GameObject(name);
            camObj.transform.SetParent(_pyramidCamerasRoot.transform);

            Camera cam = camObj.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black; // Pure #000000 background for pyramid reflection
            cam.rect = viewportRect;
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.depth = 1; // Render on top of main camera

            // Position radially around targetFocus
            Vector3 focusPos = targetFocus != null ? targetFocus.position : Vector3.zero;
            Quaternion rot = Quaternion.Euler(0f, yawAngle, rollAngle);
            Vector3 dir = Quaternion.Euler(0f, yawAngle, 0f) * Vector3.back;
            camObj.transform.position = focusPos + dir * cameraDistance + Vector3.up * cameraHeight;
            camObj.transform.rotation = rot;
            camObj.transform.LookAt(focusPos);
            // Apply roll rotation on Z for pyramid projection face alignment
            camObj.transform.Rotate(0f, 0f, rollAngle, Space.Self);

            return cam;
        }

        public void SetPyramidView(bool active)
        {
            if (isPyramidActive == active) return;
            isPyramidActive = active;
            ApplyPyramidMode(isPyramidActive);
        }

        public void TogglePyramidView()
        {
            SetPyramidView(!isPyramidActive);
        }

        private void ApplyPyramidMode(bool active)
        {
            if (_pyramidCamerasRoot != null)
            {
                _pyramidCamerasRoot.SetActive(active);
            }

            if (_mainCamera != null)
            {
                _mainCamera.enabled = !active;
                if (!active)
                {
                    _mainCamera.clearFlags = CameraClearFlags.SolidColor;
                    _mainCamera.backgroundColor = Color.black;
                }
            }

            Debug.Log($"[HoloMed AI] 4-Way Pyramid Split-Screen Mode: {(active ? "ENABLED (Pure #000000)" : "DISABLED (Single View)")}");
        }

        void Update()
        {
            // Keyboard shortcut backup directly in Unity Editor / Player
            if (Input.GetKeyDown(KeyCode.V))
            {
                TogglePyramidView();
            }
        }
    }
}
