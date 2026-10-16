using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HoloMedAI
{
    /// <summary>
    /// HoloMed AI - Non-blocking 60Hz UDP Telemetry Receiver (Port 5005).
    /// Listens for unified vision, hardware, and demo hotkey packets on a background thread
    /// and applies smooth Quaternion.Slerp rotation and Vector3.Lerp zoom scaling to the 3D Heart model.
    /// </summary>
    public class UdpTelemetryReceiver : MonoBehaviour
    {
        [System.Serializable]
        public class PinchDelta
        {
            public float x;
            public float y;
        }

        [System.Serializable]
        public class RaycastPoint
        {
            public float x;
            public float y;
        }

        [System.Serializable]
        public class VisionData
        {
            public bool hand_detected;
            public string gesture;
            public bool pinch_active;
            public PinchDelta pinch_delta;
            public float zoom_scale;
            public string selected_target;
            public RaycastPoint raycast_point;
        }

        [System.Serializable]
        public class HardwareData
        {
            public bool connected;
            public string port;
            public float distance_cm;
            public bool presence;
            public float encoder_delta;
            public bool btn_reset;
            public bool btn_section;
        }

        [System.Serializable]
        public class HotkeyData
        {
            public bool reset;
            public bool cross_section;
            public string select_chamber;
            public bool toggle_pyramid;
        }

        [System.Serializable]
        public class UnifiedTelemetryPacket
        {
            public double timestamp;
            public float fps;
            public string command;
            public string target;
            public float dx;
            public float dy;
            public float zoom_scale;
            public bool is_cross_section;
            public bool pyramid_view;
            public VisionData vision;
            public HardwareData hardware;
            public HotkeyData hotkeys;
        }

        [Header("UDP Configuration")]
        [Tooltip("Port matching Python Unified Broadcaster (default: 5005)")]
        public int listenPort = 5005;

        [Header("3D Model References")]
        [Tooltip("Transform of the 3D Heart model to manipulate")]
        public Transform heartModel;

        [Tooltip("Optional internal cutaway cross-section GameObject")]
        public GameObject cutawayObject;

        [Header("Smoothing & Motion Speeds")]
        public float rotationSpeed = 120.0f;
        public float rotationSmoothing = 12.0f;
        public float zoomSmoothing = 8.0f;
        public float minZoom = 0.5f;
        public float maxZoom = 2.5f;

        [Header("Debug")]
        public bool logPackets = false;

        // Static Events for HUD, Callouts, and Pyramid Controller
        public static event Action<UnifiedTelemetryPacket> OnTelemetryReceived;
        public static event Action<string> OnChamberSelected;
        public static event Action<bool> OnCrossSectionChanged;
        public static event Action<bool> OnPyramidViewChanged;

        // Threading & Network
        private Thread _receiveThread;
        private UdpClient _udpClient;
        private bool _isRunning = false;
        private readonly object _packetLock = new object();
        private UnifiedTelemetryPacket _latestPacket = null;
        private bool _hasNewPacket = false;

        // Interpolation Targets
        private Quaternion _targetRotation;
        private Vector3 _initialScale;
        private Quaternion _initialRotation;
        private Vector3 _targetScale;
        private bool _lastCrossSectionState = false;
        private bool _lastPyramidState = false;

        void Awake()
        {
            if (heartModel == null)
            {
                // Auto-detect Human Heart or child transform if not explicitly set
                GameObject heartObj = GameObject.Find("Human Heart");
                if (heartObj != null)
                {
                    heartModel = heartObj.transform;
                }
                else
                {
                    heartModel = this.transform;
                }
            }

            _initialRotation = heartModel.localRotation;
            _initialScale = heartModel.localScale;
            _targetRotation = _initialRotation;
            _targetScale = _initialScale;
        }

        void Start()
        {
            StartUdpListener();
        }

        void OnDestroy()
        {
            StopUdpListener();
        }

        void OnApplicationQuit()
        {
            StopUdpListener();
        }

        public void StartUdpListener()
        {
            if (_isRunning) return;

            _isRunning = true;
            _receiveThread = new Thread(ReceiveWorker)
            {
                IsBackground = true,
                Name = "HoloMed_UdpTelemetryThread"
            };
            _receiveThread.Start();
            Debug.Log($"[HoloMed AI] UdpTelemetryReceiver background listener started on port {listenPort}");
        }

        public void StopUdpListener()
        {
            _isRunning = false;
            if (_udpClient != null)
            {
                try
                {
                    _udpClient.Close();
                }
                catch (Exception) { }
                _udpClient = null;
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                try
                {
                    _receiveThread.Abort();
                }
                catch (Exception) { }
                _receiveThread = null;
            }
        }

        private void ReceiveWorker()
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
                IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

                while (_isRunning)
                {
                    byte[] data = _udpClient.Receive(ref remoteEp);
                    if (data == null || data.Length == 0) continue;

                    string jsonStr = Encoding.UTF8.GetString(data);
                    UnifiedTelemetryPacket packet = JsonUtility.FromJson<UnifiedTelemetryPacket>(jsonStr);

                    if (packet != null)
                    {
                        lock (_packetLock)
                        {
                            _latestPacket = packet;
                            _hasNewPacket = true;
                        }
                    }
                }
            }
            catch (SocketException)
            {
                // Normal on client close
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HoloMed UDP] ReceiveWorker note: {ex.Message}");
            }
        }

        void Update()
        {
            UnifiedTelemetryPacket packet = null;
            bool isNew = false;

            lock (_packetLock)
            {
                if (_hasNewPacket)
                {
                    packet = _latestPacket;
                    isNew = true;
                    _hasNewPacket = false;
                }
                else
                {
                    packet = _latestPacket;
                }
            }

            if (packet != null)
            {
                ProcessPacket(packet, isNew);
            }

            // Smooth Interpolation every frame
            ApplySmoothTransforms();
        }

        private void ProcessPacket(UnifiedTelemetryPacket packet, bool isNew)
        {
            if (isNew)
            {
                if (logPackets)
                {
                    Debug.Log($"[Telemetry RX] Cmd: {packet.command} | Target: {packet.target} | Zoom: {packet.zoom_scale} | Cutaway: {packet.is_cross_section}");
                }

                // Fire static event for HUD & Subsystems
                OnTelemetryReceived?.Invoke(packet);

                // Handle Reset Command
                if (packet.command == "RESET" || (packet.hotkeys != null && packet.hotkeys.reset))
                {
                    ResetHologram();
                }

                // Handle Cross-Section Toggle
                if (packet.is_cross_section != _lastCrossSectionState)
                {
                    _lastCrossSectionState = packet.is_cross_section;
                    if (cutawayObject != null)
                    {
                        cutawayObject.SetActive(_lastCrossSectionState);
                    }
                    OnCrossSectionChanged?.Invoke(_lastCrossSectionState);
                }

                // Handle Pyramid Split-Screen Toggle
                if (packet.pyramid_view != _lastPyramidState)
                {
                    _lastPyramidState = packet.pyramid_view;
                    OnPyramidViewChanged?.Invoke(_lastPyramidState);
                }

                // Handle Target Chamber Selection
                if (!string.IsNullOrEmpty(packet.target) && packet.target != "HEART" && packet.target != "NONE")
                {
                    OnChamberSelected?.Invoke(packet.target);
                }
            }

            // Apply Continuous Rotation Deltas (from Pinch Drag or Rotary Encoder)
            if (Mathf.Abs(packet.dx) > 0.0001f || Mathf.Abs(packet.dy) > 0.0001f)
            {
                float rotY = -packet.dx * rotationSpeed * Time.deltaTime;
                float rotX = packet.dy * rotationSpeed * Time.deltaTime;

                // Multiply into target rotation (in world/local space)
                Quaternion deltaRot = Quaternion.Euler(rotX, rotY, 0f);
                _targetRotation = deltaRot * _targetRotation;
            }

            // Apply Zoom Target (Clamped strictly between 0.5x and 2.5x per spec)
            float clampedScale = Mathf.Clamp(packet.zoom_scale, minZoom, maxZoom);
            _targetScale = _initialScale * clampedScale;
        }

        private void ApplySmoothTransforms()
        {
            if (heartModel == null) return;

            // 1. Smooth Slerp Rotation
            heartModel.localRotation = Quaternion.Slerp(
                heartModel.localRotation,
                _targetRotation,
                Time.deltaTime * rotationSmoothing
            );

            // 2. Smooth Lerp Zoom Scale
            heartModel.localScale = Vector3.Lerp(
                heartModel.localScale,
                _targetScale,
                Time.deltaTime * zoomSmoothing
            );
        }

        public void ResetHologram()
        {
            _targetRotation = _initialRotation;
            _targetScale = _initialScale;
            Debug.Log("[HoloMed AI] Hologram position & orientation reset to default.");
        }
    }
}
