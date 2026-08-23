using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Continuous State Machine Gesture Receiver
/// File: frontend/GestureReceiver.cs
/// 
/// 1. Continuous Rotation: When currentGesture is ROTATE_WRIST_CW or ROTATE_WRIST_CCW,
///    applies continuous transform.Rotate() multiplied by rotationSpeed and Time.deltaTime.
/// 2. Slice / Cross-Section: When CROSS_SECTION_SPLIT is received, toggles crossSectionMesh.SetActive().
/// 3. Continuous Zoom & Drag: Continuous localScale and position delta updates.
/// 4. Non-Blocking UDP: Listens on Port 5053 concurrently alongside Voice (Port 5052).
/// </summary>
public class GestureReceiver : MonoBehaviour
{
    [System.Serializable]
    public class GestureParams
    {
        public float delta_x;
        public float delta_y;
        public float angle_delta;
        public float delta;
        public float zoom_factor;
        public string action_type;
        public bool reset_scene;
    }

    [System.Serializable]
    public class GesturePayload
    {
        public string type;
        public string action;
        public float confidence;
        public string category;
        public double timestamp;
        public GestureParams @params;

        // Legacy alias
        public string command;
    }

    [Header("Network Configuration")]
    [Tooltip("UDP Port for gesture telemetry (Default: 5053, Voice uses 5052)")]
    public int udpPort = 5053;

    [Header("Target 3D Hologram")]
    [Tooltip("Target Transform to rotate, scale, and pan. Defaults to this GameObject if empty.")]
    public Transform targetObject;

    [Header("Slice / Dissection Feature")]
    [Tooltip("Optional internal cutaway / cross-section mesh toggled via CROSS_SECTION_SPLIT")]
    public GameObject crossSectionMesh;

    [Header("Continuous Motion Speeds")]
    [Tooltip("Continuous rotation speed in degrees per second")]
    public float rotationSpeed = 100.0f;

    [Tooltip("Continuous zoom scale speed per second")]
    public float zoomSpeed = 2.5f;

    [Tooltip("Pinch drag translation speed")]
    public float dragSensitivity = 12.0f;

    [Tooltip("Minimum allowed local scale")]
    public float minScale = 0.2f;

    [Tooltip("Maximum allowed local scale")]
    public float maxScale = 20.0f;

    [Tooltip("Seconds before an unrefreshed gesture automatically returns to IDLE")]
    public float gestureTimeout = 0.40f;

    [Header("State Machine Monitor (Inspector Live View)")]
    public string currentGesture = "IDLE";
    public float currentConfidence = 0.0f;
    public float currentDeltaX = 0.0f;
    public float currentDeltaY = 0.0f;
    public int totalPacketsReceived = 0;

    [Header("Debug & Console Options")]
    public bool showOnScreenHUD = true;
    public bool logIncomingPackets = true;

    // Events for external subscribers
    public static event Action<GesturePayload> OnGestureReceived;

    // Background Threading
    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool isRunning = false;
    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();

    // Initial Transform Baselines (for Emergency Reset)
    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private Vector3 initialScale;

    private float lastPacketTime = 0.0f;
    private string previousTriggeredAction = "";

    void Awake()
    {
        if (targetObject == null)
        {
            targetObject = this.transform;
        }

        // Cache initial reference coordinates
        initialPosition = targetObject.localPosition;
        initialRotation = targetObject.localRotation;
        initialScale = targetObject.localScale;
    }

    void Start()
    {
        StartUDPServer();
    }

    public void StartUDPServer()
    {
        if (isRunning) return;

        try
        {
            udpClient = new UdpClient(udpPort);
            isRunning = true;
            receiveThread = new Thread(new ThreadStart(ReceiveDatagramLoop))
            {
                IsBackground = true,
                Name = "HoloMed_UDP_GestureReceiver_5053"
            };
            receiveThread.Start();
            Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> GestureReceiver active on UDP port {udpPort} (Voice on 5052)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HoloMed AI] Failed to bind Gesture UDP port {udpPort}: {ex.Message}");
        }
    }

    private void ReceiveDatagramLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                byte[] rawData = udpClient.Receive(ref remoteEP);
                if (rawData != null && rawData.Length > 0)
                {
                    string jsonString = Encoding.UTF8.GetString(rawData);
                    incomingQueue.Enqueue(jsonString);
                }
            }
            catch (SocketException)
            {
                // Normal on socket shutdown
            }
            catch (Exception ex)
            {
                if (isRunning) Debug.LogError($"[GestureReceiver] Read Exception: {ex.Message}");
            }
        }
    }

    void Update()
    {
        // 1. Process queued UDP packets on Unity Main Thread
        while (incomingQueue.TryDequeue(out string rawJson))
        {
            if (logIncomingPackets)
            {
                Debug.Log("Gesture Received: " + rawJson);
            }
            ParseIncomingJson(rawJson);
        }

        // 2. Timeout Check: If no fresh packet received within timeout, reset to IDLE
        if (currentGesture != "IDLE" && (Time.time - lastPacketTime) > gestureTimeout)
        {
            currentGesture = "IDLE";
            currentDeltaX = 0f;
            currentDeltaY = 0f;
        }

        // 3. Continuous State Machine: Apply motion math every frame
        ApplyContinuousStateMachineMotion();
    }

    private void ParseIncomingJson(string jsonText)
    {
        if (string.IsNullOrEmpty(jsonText)) return;

        try
        {
            GesturePayload payload = JsonUtility.FromJson<GesturePayload>(jsonText);
            if (payload == null) return;

            string action = !string.IsNullOrEmpty(payload.action) ? payload.action : payload.command;
            if (string.IsNullOrEmpty(action)) return;

            action = action.ToUpperInvariant().Trim();
            currentGesture = action;
            currentConfidence = payload.confidence;
            lastPacketTime = Time.time;
            totalPacketsReceived++;

            // Extract real-time parameter deltas
            if (payload.@params != null)
            {
                currentDeltaX = payload.@params.delta_x;
                currentDeltaY = payload.@params.delta_y;
            }

            OnGestureReceived?.Invoke(payload);

            // One-shot discrete triggers
            if (action == "CROSS_SECTION_SPLIT" && previousTriggeredAction != "CROSS_SECTION_SPLIT")
            {
                ToggleCrossSection();
            }
            else if (action == "EMERGENCY_RESET" || action == "RESET_VIEW" || action == "RESET")
            {
                ExecuteReset();
                currentGesture = "IDLE";
            }
            else if (action == "AIR_TAP")
            {
                Debug.Log("<color=#00FFB4>[HoloMed AI]</color> AIR TAP Triggered (Raycast/Select)");
            }
            else if (action == "PALM_STOP" || action == "FIST_LOCK")
            {
                currentGesture = "IDLE";
                currentDeltaX = 0f;
                currentDeltaY = 0f;
            }

            previousTriggeredAction = action;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GestureReceiver] JSON Parse Error: {ex.Message}\nRaw: {jsonText}");
        }
    }

    /// <summary>
    /// Continuous State Machine applying continuous additive transformations
    /// every frame multiplied by rotationSpeed and Time.deltaTime.
    /// </summary>
    private void ApplyContinuousStateMachineMotion()
    {
        Transform target = targetObject != null ? targetObject : this.transform;
        float dt = Time.deltaTime;

        switch (currentGesture)
        {
            // -------------------------------------------------------------
            // 1. Continuous Rotation (Clockwise & Counter-Clockwise)
            // -------------------------------------------------------------
            case "ROTATE_WRIST_CW":
            case "TWO_HAND_ROTATE_CW":
            case "ROTATE_CW":
                target.Rotate(Vector3.up, -rotationSpeed * dt, Space.World);
                break;

            case "ROTATE_WRIST_CCW":
            case "TWO_HAND_ROTATE_CCW":
            case "ROTATE_CCW":
                target.Rotate(Vector3.up, rotationSpeed * dt, Space.World);
                break;

            // -------------------------------------------------------------
            // 2. Continuous Zoom Scale (Spread & Close)
            // -------------------------------------------------------------
            case "ZOOM_SPREAD":
            case "TWO_HAND_SCALE_UP":
            case "ZOOM_IN":
                float newScaleUp = Mathf.Clamp(target.localScale.x + (zoomSpeed * dt), minScale, maxScale);
                target.localScale = Vector3.one * newScaleUp;
                break;

            case "ZOOM_CLOSE":
            case "TWO_HAND_SCALE_DOWN":
            case "ZOOM_OUT":
                float newScaleDown = Mathf.Clamp(target.localScale.x - (zoomSpeed * dt), minScale, maxScale);
                target.localScale = Vector3.one * newScaleDown;
                break;

            // -------------------------------------------------------------
            // 3. Continuous Pinch Drag & Translation
            // -------------------------------------------------------------
            case "PINCH_DRAG":
            case "TWO_HAND_PAN":
                Vector3 dragStep = new Vector3(
                    currentDeltaX * dragSensitivity * dt * 60f,
                    -currentDeltaY * dragSensitivity * dt * 60f,
                    0f
                );
                target.position += dragStep;
                break;

            // -------------------------------------------------------------
            // 4. Stopped / Locked / Idle
            // -------------------------------------------------------------
            case "PALM_STOP":
            case "FIST_LOCK":
            case "IDLE":
            default:
                // No continuous motion applied
                break;
        }
    }

    /// <summary>
    /// Toggles the Slice / Cutaway internal mesh.
    /// </summary>
    public void ToggleCrossSection()
    {
        if (crossSectionMesh != null)
        {
            crossSectionMesh.SetActive(!crossSectionMesh.activeSelf);
            Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> Cross-Section Slice Toggled: {crossSectionMesh.activeSelf}");
        }
        else
        {
            Debug.LogWarning("[GestureReceiver] Slice Triggered but 'crossSectionMesh' is not assigned in Inspector!");
        }
    }

    public void ExecuteReset()
    {
        Transform target = targetObject != null ? targetObject : this.transform;

        target.localPosition = initialPosition;
        target.localRotation = initialRotation;
        target.localScale = initialScale;

        if (crossSectionMesh != null)
        {
            crossSectionMesh.SetActive(false);
        }

        Debug.Log("<color=#00FFB4>[HoloMed AI]</color> Viewport Reset to Baseline Coordinates.");
    }

    private void StopUDPServer()
    {
        isRunning = false;
        if (udpClient != null)
        {
            try { udpClient.Close(); } catch { }
            udpClient = null;
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            try { receiveThread.Abort(); } catch { }
            receiveThread = null;
        }
    }

    void OnDisable() => StopUDPServer();
    void OnDestroy() => StopUDPServer();
    void OnApplicationQuit() => StopUDPServer();

    // -------------------------------------------------------------
    // ON-SCREEN DEBUG TELEMETRY HUD
    // -------------------------------------------------------------
    void OnGUI()
    {
        if (!showOnScreenHUD) return;

        bool hasRecentSignal = (Time.time - lastPacketTime) < 0.6f;
        Color statusColor = hasRecentSignal ? new Color(0f, 1f, 0.7f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f);

        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.fontSize = 12;
        boxStyle.alignment = TextAnchor.UpperLeft;
        boxStyle.normal.textColor = Color.white;

        GUILayout.BeginArea(new Rect(20, 20, 330, 185), boxStyle);
        GUILayout.Label("<size=13><b>HOLOMED AI // GESTURE & VOICE PIPELINE</b></size>");
        GUILayout.Label($"Gesture Port: {udpPort} | Voice Port: 5052");

        GUI.color = statusColor;
        GUILayout.Label($"Telemetry: {(hasRecentSignal ? "CONNECTED / STREAMING" : "WAITING FOR GESTURE...")}");
        GUI.color = Color.white;

        GUILayout.Label($"Current Gesture: <b>{currentGesture}</b> ({currentConfidence * 100:F0}%)");
        Transform target = targetObject != null ? targetObject : this.transform;
        GUILayout.Label($"Scale: {target.localScale.x:F2}x | Rot Y: {target.localEulerAngles.y:F0}°");

        if (GUILayout.Button("Reset Viewport (Emergency)"))
        {
            ExecuteReset();
        }

        GUILayout.EndArea();
    }
}