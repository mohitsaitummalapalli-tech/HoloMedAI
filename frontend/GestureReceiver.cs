using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Phase 3 Clinical Realism & Spatial Gesture Receiver
/// File: frontend/GestureReceiver.cs
/// 
/// 1. Real-Time Pinch & Drag: Parses "DRAG" / "PINCH_DRAG" and applies X, Y spatial deltas to 3D localPosition.
/// 2. Slicing & Dissection: Routes "SLICE" / "CUT" / "CROSS_SECTION_SPLIT" directly into OrganSlicer & VitalsSimulationManager.
/// 3. Point & AI Caliper: Routes "POINT" spatial measurements to OrganStateManager.
/// 4. Continuous Rotation & Zoom: Smooth rotation and scaling.
/// 5. Non-Blocking UDP: Listens on Port 5053 concurrently alongside Voice (Port 5052).
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
        public float point_x;
        public float point_y;
        public float estimated_distance_mm;
        public float estimated_volume_ml;
        public string target_structure;
        public string measurement_label;
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

    [Header("Target 3D Hologram & Subsystems")]
    [Tooltip("Target Transform to translate, rotate, and scale. Defaults to this GameObject if empty.")]
    public Transform targetObject;

    [Tooltip("Reference to OrganStateManager for spatial measurement display and layer toggles.")]
    public OrganStateManager organStateManager;

    [Tooltip("Reference to OrganSlicer for surgical dissection animations and trauma triggers.")]
    public OrganSlicer organSlicer;

    [Tooltip("Reference to VitalsSimulationManager for patient trauma and rhythm monitoring.")]
    public VitalsSimulationManager vitalsManager;

    [Header("Slice / Dissection Feature")]
    [Tooltip("Optional internal cutaway / cross-section mesh toggled via CROSS_SECTION_SPLIT")]
    public GameObject crossSectionMesh;

    [Header("Spatial Motion Sensitivities")]
    [Tooltip("Pinch drag translation multiplier (higher = faster response)")]
    public float dragSensitivity = 14.0f;

    [Tooltip("Continuous rotation speed in degrees per second")]
    public float rotationSpeed = 100.0f;

    [Tooltip("Continuous zoom scale speed per second")]
    public float zoomSpeed = 2.5f;

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

    // Events for external subscribers / HUD UI
    public static event Action<GesturePayload> OnGestureReceived;
    public static event Action<GestureParams> OnSpatialPointMeasurement;
    public static event Action OnSliceTriggered;

    // Background Threading
    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool isRunning = false;
    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();

    // Initial Transform Baselines (for Reset)
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

        if (organStateManager == null)
        {
            organStateManager = targetObject.GetComponent<OrganStateManager>();
            if (organStateManager == null) organStateManager = FindFirstObjectByType<OrganStateManager>();
        }

        if (organSlicer == null)
        {
            organSlicer = targetObject.GetComponent<OrganSlicer>();
            if (organSlicer == null) organSlicer = FindFirstObjectByType<OrganSlicer>();
        }

        if (vitalsManager == null)
        {
            vitalsManager = FindFirstObjectByType<VitalsSimulationManager>();
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
            Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> Phase 3 Spatial & Slicing GestureReceiver active on UDP port {udpPort}");
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

        // 3. Continuous State Machine: Apply spatial translation & rotation math
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

                // -------------------------------------------------------------
                // 1. POINT & AI SPATIAL MEASUREMENT ROUTING
                // -------------------------------------------------------------
                if (action == "POINT" || action == "POINT_SELECT")
                {
                    if (organStateManager != null)
                    {
                        organStateManager.DisplaySpatialMeasurement(
                            payload.@params.target_structure,
                            payload.@params.estimated_distance_mm,
                            payload.@params.estimated_volume_ml,
                            payload.@params.measurement_label
                        );
                    }
                    OnSpatialPointMeasurement?.Invoke(payload.@params);
                }
                // -------------------------------------------------------------
                // 2. IMMEDIATE DRAG TRANSLATION
                // -------------------------------------------------------------
                else if (action == "DRAG" || action == "PINCH_DRAG")
                {
                    ApplyPinchDragDelta(payload.@params.delta_x, payload.@params.delta_y);
                }
            }

            OnGestureReceived?.Invoke(payload);

            // -------------------------------------------------------------
            // 3. SLICE & DISSECTION ROUTING
            // -------------------------------------------------------------
            if ((action == "SLICE" || action == "CUT" || action == "CROSS_SECTION_SPLIT") && previousTriggeredAction != action)
            {
                ExecuteSliceAction();
            }
            else if (action == "EMERGENCY_RESET" || action == "RESET_VIEW" || action == "RESET")
            {
                ExecuteReset();
                currentGesture = "IDLE";
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
    /// Executes surgical slice on OrganSlicer and triggers vitals trauma spike.
    /// </summary>
    public void ExecuteSliceAction()
    {
        if (organSlicer != null)
        {
            organSlicer.ExecuteSlice();
        }
        else if (crossSectionMesh != null)
        {
            crossSectionMesh.SetActive(!crossSectionMesh.activeSelf);
        }

        if (vitalsManager != null)
        {
            vitalsManager.TriggerTraumaSpike("Gesture Surgical Slicing Incision", 1.2f);
        }

        OnSliceTriggered?.Invoke();
        Debug.Log("<color=#FF3366><b>[GestureReceiver]</b></color> SLICE gesture routed to OrganSlicer & VitalsSimulationManager!");
    }

    /// <summary>
    /// Applies real-time X, Y spatial delta values to the local position of the active 3D model.
    /// </summary>
    public void ApplyPinchDragDelta(float deltaX, float deltaY)
    {
        Transform target = targetObject != null ? targetObject : this.transform;
        Vector3 dragStep = new Vector3(
            deltaX * dragSensitivity,
            -deltaY * dragSensitivity,
            0f
        );
        target.localPosition += dragStep;
    }

    /// <summary>
    /// Continuous State Machine applying continuous additive transformations
    /// every frame multiplied by Time.deltaTime.
    /// </summary>
    private void ApplyContinuousStateMachineMotion()
    {
        Transform target = targetObject != null ? targetObject : this.transform;
        float dt = Time.deltaTime;

        switch (currentGesture)
        {
            // -------------------------------------------------------------
            // Real-Time Pinch Drag
            // -------------------------------------------------------------
            case "DRAG":
            case "PINCH_DRAG":
            case "TWO_HAND_PAN":
                Vector3 continuousStep = new Vector3(
                    currentDeltaX * dragSensitivity * dt * 45f,
                    -currentDeltaY * dragSensitivity * dt * 45f,
                    0f
                );
                target.localPosition += continuousStep;
                break;

            // -------------------------------------------------------------
            // Continuous Rotation (Clockwise & Counter-Clockwise)
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
            // Continuous Zoom Scale (Spread & Close)
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
            // Point / Stopped / Locked / Idle
            // -------------------------------------------------------------
            case "POINT":
            case "PALM_STOP":
            case "FIST_LOCK":
            case "IDLE":
            default:
                break;
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

        if (organSlicer != null)
        {
            organSlicer.ResetSlice();
        }

        if (organStateManager != null)
        {
            organStateManager.ResetOrganState();
        }

        if (vitalsManager != null)
        {
            vitalsManager.ResetVitals();
        }

        Debug.Log("<color=#00FFB4>[HoloMed AI]</color> Viewport, Slice, and Vitals Reset to Baseline Coordinates.");
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

        GUILayout.BeginArea(new Rect(20, 20, 340, 195), boxStyle);
        GUILayout.Label("<size=13><b>HOLOMED AI // SPATIAL & SLICE PIPELINE</b></size>");
        GUILayout.Label($"Gesture Port: {udpPort} | Voice Port: 5052");

        GUI.color = statusColor;
        GUILayout.Label($"Telemetry: {(hasRecentSignal ? "CONNECTED / STREAMING" : "WAITING FOR GESTURE...")}");
        GUI.color = Color.white;

        GUILayout.Label($"Active Gesture: <b>{currentGesture}</b> ({currentConfidence * 100:F0}%)");
        Transform target = targetObject != null ? targetObject : this.transform;
        GUILayout.Label($"Pos: ({target.localPosition.x:F2}, {target.localPosition.y:F2}, {target.localPosition.z:F2})");
        GUILayout.Label($"Scale: {target.localScale.x:F2}x | Rot Y: {target.localEulerAngles.y:F0}°");

        if (GUILayout.Button("Reset Scene & Vitals (Emergency)"))
        {
            ExecuteReset();
        }

        GUILayout.EndArea();
    }
}