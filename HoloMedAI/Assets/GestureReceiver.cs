using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Category 2: Gesture Subsystem & Command Bus
    /// File: GestureReceiver.cs
    /// 
    /// Architecture:
    /// External Computer Vision / MediaPipe Engine (Outside Unity):
    ///   Camera -> Landmark Detection -> Gesture Classification -> JSON Generation -> UDP (Port 5052)
    /// 
    /// Unity GestureReceiver (Inside Unity):
    ///   UDP Reception -> JSON Deserialization -> Validation -> Main-Thread Queue -> Command Router -> Subsystems
    /// </summary>
    [DisallowMultipleComponent]
    public class GestureReceiver : MonoBehaviour
    {
        [Header("Target 3D Model")]
        [Tooltip("Assign the root Transform of your 3D Anatomical Organ (e.g. Heart)")]
        [SerializeField] private Transform targetModel;

        [Header("Network Configuration")]
        [Tooltip("UDP Port matching external Python CV engine (Default: 5052)")]
        [SerializeField] private int udpPort = 5052;

        [Header("Confidence & Validation")]
        [Tooltip("Minimum confidence threshold [0.0 - 1.0]. Packets below this threshold will be discarded.")]
        [Range(0.1f, 1.0f)]
        [SerializeField] private float minimumConfidence = 0.75f;

        [Header("Interaction Calibration")]
        [Tooltip("Pan / Translation speed multiplier.")]
        [SerializeField] private float moveSpeed = 1.0f;

        [Tooltip("Zoom / Scale speed multiplier.")]
        [SerializeField] private float zoomSpeed = 1.0f;

        [Tooltip("Rotation speed multiplier.")]
        [SerializeField] private float rotationSpeed = 1.0f;

        [Tooltip("Scale limits")]
        [SerializeField] private float minScale = 0.1f;
        [SerializeField] private float maxScale = 5.0f;

        [Header("Logging & Diagnostics")]
        [Tooltip("Configure verbosity level for console diagnostics")]
        [SerializeField] private GestureLogLevel logLevel = GestureLogLevel.INFO;

        [Header("Attached Subsystem Controllers (Optional Override)")]
        [SerializeField] private ModelManipulationController manipulationController;
        [SerializeField] private AnatomySelectionController anatomyController;
        [SerializeField] private NavigationController navigationController;
        [SerializeField] private GestureStateManager stateManager;
        [SerializeField] private ApplicationStateManager applicationStateManager;

        // Thread-Safe Datagram Queue
        private readonly ConcurrentQueue<string> packetQueue = new ConcurrentQueue<string>();
        private Thread udpThread;
        private UdpClient udpClient;
        private volatile bool isListening = false;

        // Command Router
        private GestureCommandRouter commandRouter;

        public Transform TargetModel => targetModel;
        public int UdpPort => udpPort;
        public float MinimumConfidence => minimumConfidence;
        public GestureLogLevel LogLevel => logLevel;

        private void Awake()
        {
            if (targetModel == null)
            {
                GameObject heartObj = GameObject.Find("Heart");
                targetModel = heartObj != null ? heartObj.transform : transform;
            }

            // Auto-locate or attach required controllers
            if (manipulationController == null)
            {
                manipulationController = GetComponent<ModelManipulationController>() ?? gameObject.AddComponent<ModelManipulationController>();
            }
            manipulationController.TargetModel = targetModel;

            if (anatomyController == null)
            {
                anatomyController = GetComponent<AnatomySelectionController>() ?? gameObject.AddComponent<AnatomySelectionController>();
            }

            if (navigationController == null)
            {
                navigationController = GetComponent<NavigationController>() ?? gameObject.AddComponent<NavigationController>();
            }

            if (stateManager == null)
            {
                stateManager = GetComponent<GestureStateManager>() ?? gameObject.AddComponent<GestureStateManager>();
            }

            if (applicationStateManager == null)
            {
                applicationStateManager = GetComponent<ApplicationStateManager>() ?? gameObject.AddComponent<ApplicationStateManager>();
            }

            // Initialize centralized Command Router
            commandRouter = new GestureCommandRouter(
                anatomyController,
                manipulationController,
                navigationController,
                stateManager,
                applicationStateManager,
                minimumConfidence,
                logLevel
            );
        }

        private void Start()
        {
            StartUDPListener();
        }

        private void OnValidate()
        {
            if (commandRouter != null)
            {
                commandRouter.MinimumConfidence = minimumConfidence;
                commandRouter.LogLevel = logLevel;
            }
        }

        /// <summary>
        /// Starts the background UDP listener thread.
        /// </summary>
        public void StartUDPListener()
        {
            if (isListening) return;

            try
            {
                udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

                isListening = true;
                udpThread = new Thread(new ThreadStart(UDPListenWorker))
                {
                    IsBackground = true,
                    Name = "HoloMedAI_Gesture_UDP_Worker"
                };
                udpThread.Start();

                Log(GestureLogLevel.INFO, $"Listening for external CV gesture stream on UDP port {udpPort}. Target: '{targetModel.name}'");
            }
            catch (Exception ex)
            {
                Log(GestureLogLevel.ERROR, $"Failed to bind UDP port {udpPort}: {ex.Message}");
            }
        }

        /// <summary>
        /// Background worker thread: captures UDP datagrams and enqueues them for main thread execution.
        /// </summary>
        private void UDPListenWorker()
        {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (isListening)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref remoteEndPoint);
                    if (data != null && data.Length > 0)
                    {
                        string rawJson = Encoding.UTF8.GetString(data);
                        packetQueue.Enqueue(rawJson);
                    }
                }
                catch (SocketException)
                {
                    // Socket closed / application stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (isListening)
                    {
                        // Safe logging on error
                    }
                }
            }
        }

        // Thread-Safe Main Thread Action Queue
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Unity Main Thread Update: safely dequeues and deserializes packets, routing them to subsystems.
        /// </summary>
        private void Update()
        {
            // Execute queued main thread actions
            while (_executionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Log(GestureLogLevel.ERROR, $"ExecutionQueue error: {ex.Message}");
                }
            }

            // Process queued packets
            while (packetQueue.TryDequeue(out string rawPayload))
            {
                if (string.IsNullOrEmpty(rawPayload)) continue;

                if (logLevel == GestureLogLevel.DEBUG)
                {
                    Log(GestureLogLevel.DEBUG, $"RX Raw: {rawPayload}");
                }

                ProcessIncomingPayload(rawPayload);
            }
        }

        /// <summary>
        /// Deserializes JSON safely and submits the command to the router.
        /// </summary>
        private void ProcessIncomingPayload(string rawJson)
        {
            string trimmed = rawJson.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                // Fallback for simple string commands like "RESET" or "SELECT"
                CoreGestureCommand plainCmd = new CoreGestureCommand
                {
                    command = trimmed.ToUpperInvariant(),
                    confidence = 1.0f
                };
                commandRouter.RouteCommand(plainCmd);
                return;
            }

            try
            {
                CoreGestureCommand parsedCommand = JsonUtility.FromJson<CoreGestureCommand>(trimmed);
                if (parsedCommand == null)
                {
                    Log(GestureLogLevel.WARNING, $"Failed to parse command from JSON: {trimmed}");
                    return;
                }

                if (string.IsNullOrEmpty(parsedCommand.command) && !string.IsNullOrEmpty(parsedCommand.action))
                {
                    parsedCommand.command = parsedCommand.action;
                }

                if (string.IsNullOrEmpty(parsedCommand.command))
                {
                    Log(GestureLogLevel.WARNING, $"Failed to parse command from JSON: {trimmed}");
                    return;
                }

                string cmd = parsedCommand.command.Trim();

                // 1. ROTATION (sys.input.interact, ROTATE, PINCH_DRAG, DRAG)
                if (cmd.Equals("sys.input.interact", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("INTERACT", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("ROTATE", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("ROTATE_MODEL", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("PINCH_DRAG", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("DRAG", StringComparison.OrdinalIgnoreCase))
                {
                    float dx = parsedCommand.payload != null && parsedCommand.payload.dx != 0f ? parsedCommand.payload.dx : parsedCommand.dx;
                    float dy = parsedCommand.payload != null && parsedCommand.payload.dy != 0f ? parsedCommand.payload.dy : parsedCommand.dy;
                    if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                    {
                        ExtractDeltas(trimmed, out dx, out dy);
                    }

                    _executionQueue.Enqueue(() =>
                    {
                        Transform target = targetModel ?? (GameObject.Find("Heart")?.transform ?? transform);
                        if (target != null)
                        {
                            Debug.Log($"[HoloMed] Rotating Heart dx:{dx} dy:{dy}");
                            target.Rotate(Vector3.up, -dx * rotationSpeed, Space.World);
                            target.Rotate(Vector3.right, dy * rotationSpeed, Space.World);
                        }
                    });
                    return;
                }

                // 2. ZOOM (Scaling)
                if (cmd.Equals("ZOOM", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("TWO-HAND ZOOM", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("TWO_HAND_ZOOM", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("ZOOM_MODEL", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("SCALE", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("ZOOM_SPREAD", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("ZOOM_CLOSE", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("TWO_HAND_SCALE_UP", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("TWO_HAND_SCALE_DOWN", StringComparison.OrdinalIgnoreCase))
                {
                    float delta = 0f;
                    if (parsedCommand.payload != null)
                    {
                        if (parsedCommand.payload.dz != 0f) delta = parsedCommand.payload.dz;
                        else if (parsedCommand.payload.scale != 0f) delta = parsedCommand.payload.scale;
                        else if (parsedCommand.payload.delta != 0f) delta = parsedCommand.payload.delta;
                        else if (parsedCommand.payload.distanceDelta != 0f) delta = parsedCommand.payload.distanceDelta;
                        else if (parsedCommand.payload.value != 0f) delta = parsedCommand.payload.value;
                        else if (parsedCommand.payload.zoomFactor > 0f) delta = (parsedCommand.payload.zoomFactor - 1.0f) * 0.1f;
                    }
                    if (Mathf.Approximately(delta, 0f))
                    {
                        if (parsedCommand.dz != 0f) delta = parsedCommand.dz;
                        else if (parsedCommand.scale != 0f) delta = parsedCommand.scale;
                        else if (parsedCommand.delta != 0f) delta = parsedCommand.delta;
                        else if (parsedCommand.distanceDelta != 0f) delta = parsedCommand.distanceDelta;
                        else if (parsedCommand.value != 0f) delta = parsedCommand.value;
                    }

                    if (Mathf.Approximately(delta, 0f))
                    {
                        ExtractZoomDelta(trimmed, out delta);
                    }

                    if (cmd.Equals("ZOOM_SPREAD", StringComparison.OrdinalIgnoreCase) || cmd.Equals("TWO_HAND_SCALE_UP", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Mathf.Approximately(delta, 0f)) delta = 0.05f;
                    }
                    else if (cmd.Equals("ZOOM_CLOSE", StringComparison.OrdinalIgnoreCase) || cmd.Equals("TWO_HAND_SCALE_DOWN", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Mathf.Approximately(delta, 0f)) delta = -0.05f;
                    }

                    _executionQueue.Enqueue(() =>
                    {
                        Transform target = targetModel ?? (GameObject.Find("Heart")?.transform ?? transform);
                        if (target != null)
                        {
                            Debug.Log($"[HoloMed] Zooming Heart delta:{delta}");
                            Vector3 newScale = target.localScale + (Vector3.one * delta * zoomSpeed);
                            target.localScale = Vector3.Max(Vector3.one * minScale, Vector3.Min(newScale, Vector3.one * maxScale));
                        }
                    });
                    return;
                }

                // 3. MOVE (Translation/Pan)
                if (cmd.Equals("MOVE", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("PAN", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("TRANSLATE", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("MOVE_MODEL", StringComparison.OrdinalIgnoreCase) ||
                    cmd.Equals("sys.input.move", StringComparison.OrdinalIgnoreCase))
                {
                    float dx = parsedCommand.payload != null && parsedCommand.payload.dx != 0f ? parsedCommand.payload.dx : parsedCommand.dx;
                    float dy = parsedCommand.payload != null && parsedCommand.payload.dy != 0f ? parsedCommand.payload.dy : parsedCommand.dy;
                    if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                    {
                        ExtractDeltas(trimmed, out dx, out dy);
                    }

                    _executionQueue.Enqueue(() =>
                    {
                        Transform target = targetModel ?? (GameObject.Find("Heart")?.transform ?? transform);
                        if (target != null)
                        {
                            Debug.Log($"[HoloMed] Moving Heart dx:{dx} dy:{dy}");
                            target.Translate(new Vector3(dx * moveSpeed, -dy * moveSpeed, 0), Space.World);
                        }
                    });
                    return;
                }

                commandRouter.RouteCommand(parsedCommand);
            }
            catch (Exception ex)
            {
                Log(GestureLogLevel.ERROR, $"JSON Deserialization exception: {ex.Message} | Payload: {trimmed}");
            }
        }

        private void ExtractDeltas(string json, out float dx, out float dy)
        {
            dx = 0f;
            dy = 0f;
            try
            {
                int idxX = json.IndexOf("\"dx\":", StringComparison.OrdinalIgnoreCase);
                if (idxX >= 0)
                {
                    int start = idxX + 5;
                    int end = json.IndexOfAny(new char[] { ',', '}', ' ' }, start);
                    if (end > start)
                    {
                        float.TryParse(json.Substring(start, end - start).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dx);
                    }
                }

                int idxY = json.IndexOf("\"dy\":", StringComparison.OrdinalIgnoreCase);
                if (idxY >= 0)
                {
                    int start = idxY + 5;
                    int end = json.IndexOfAny(new char[] { ',', '}', ' ' }, start);
                    if (end > start)
                    {
                        float.TryParse(json.Substring(start, end - start).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dy);
                    }
                }
            }
            catch { }
        }

        private void ExtractZoomDelta(string json, out float delta)
        {
            delta = 0f;
            try
            {
                string[] keys = new string[] { "\"dz\":", "\"scale\":", "\"delta\":", "\"value\":", "\"dist_delta\":", "\"distanceDelta\":" };
                foreach (string key in keys)
                {
                    int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int start = idx + key.Length;
                        int end = json.IndexOfAny(new char[] { ',', '}', ' ' }, start);
                        if (end > start)
                        {
                            if (float.TryParse(json.Substring(start, end - start).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delta))
                            {
                                if (!Mathf.Approximately(delta, 0f)) return;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void StopUDPListener()
        {
            isListening = false;

            if (udpClient != null)
            {
                try { udpClient.Close(); } catch { }
                udpClient = null;
            }

            if (udpThread != null && udpThread.IsAlive)
            {
                try { udpThread.Abort(); } catch { }
                udpThread = null;
            }

            Log(GestureLogLevel.INFO, "UDP Listener stopped cleanly.");
        }

        private void Log(GestureLogLevel level, string message)
        {
            if (logLevel == GestureLogLevel.NONE || level > logLevel) return;

            switch (level)
            {
                case GestureLogLevel.ERROR:
                    Debug.LogError($"<color=#FF3366>[HoloMed AI Receiver]</color> {message}");
                    break;
                case GestureLogLevel.WARNING:
                    Debug.LogWarning($"<color=#FFAA00>[HoloMed AI Receiver]</color> {message}");
                    break;
                case GestureLogLevel.INFO:
                case GestureLogLevel.DEBUG:
                    Debug.Log($"<color=#00FFB4>[HoloMed AI Receiver]</color> {message}");
                    break;
            }
        }

        private void OnDisable() => StopUDPListener();
        private void OnDestroy() => StopUDPListener();
        private void OnApplicationQuit() => StopUDPListener();
    }
}
