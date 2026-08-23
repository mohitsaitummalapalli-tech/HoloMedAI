using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Category 2: Gesture Subsystem & Command Bus
/// File: GestureReceiver.cs
/// 
/// 1. Target Binding: Assign 3D Organ (e.g. Heart) directly in the Unity Inspector.
/// 2. Zero UI Clutter: No OnGUI() overlays or debug text boxes rendered on screen.
/// 3. UDP Port 5052 Listener: Background thread capturing JSON and text payloads from MediaPipe/Python.
/// 4. Temporal Smoothing: Uses interpolation (Lerp/Slerp) with Time.deltaTime for butter-smooth motion.
/// 5. Command Bus Parsing:
///    - ROTATE        -> Multi-axis delta rotation (dX, dY, axis values) in Space.World
///    - PINCH / DRAG  -> Smooth translation movement across the camera/screen plane
///    - ZOOM          -> Dynamic localScale adjustment with min/max bounds
///    - RESET         -> Restores baseline position, rotation, and scale
///    - SELECT        -> Triggers highlight/selection event
/// </summary>
public class GestureReceiver : MonoBehaviour
{
    [Header("Target 3D Hologram Binding")]
    [Tooltip("Drag and drop your 3D Organ (Heart) GameObject here")]
    public Transform targetModel;

    [Header("Motion Sensitivities")]
    [Tooltip("Translation movement multiplier")]
    public float moveSpeed = 5.0f;

    [Tooltip("Rotation sensitivity multiplier")]
    public float rotateSpeed = 100.0f;

    [Tooltip("Zoom / Scale scaling multiplier")]
    public float zoomSpeed = 2.5f;

    [Header("Temporal Smoothing")]
    [Tooltip("Smoothing speed for position, rotation, and scale interpolation (higher = faster, lower = smoother)")]
    [Range(1f, 30f)]
    public float smoothingFactor = 12.0f;

    [Header("Network Configuration")]
    [Tooltip("UDP Port matching Python MediaPipe stream (Default: 5052)")]
    public int udpPort = 5052;

    [Header("Debug Options")]
    [Tooltip("Set to true to log incoming UDP packets to Unity Console")]
    public bool logIncomingPackets = false;

    // UDP Background Threading
    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool isRunning = false;
    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();

    // Baseline Reference Transform
    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private Vector3 initialScale;

    // Target States for Temporal Smoothing
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 targetScale;

    // Scale Constraints
    private const float MinScale = 0.2f;
    private const float MaxScale = 5.0f;

    private void Awake()
    {
        // Auto-assign to self if targetModel is not assigned in Inspector
        if (targetModel == null)
        {
            targetModel = transform;
        }

        // Cache initial reference transform
        initialPosition = targetModel.localPosition;
        initialRotation = targetModel.localRotation;
        initialScale = targetModel.localScale;

        // Initialize target states
        targetPosition = initialPosition;
        targetRotation = initialRotation;
        targetScale = initialScale;
    }

    private void Start()
    {
        StartUDPServer();
    }

    public void StartUDPServer()
    {
        if (isRunning) return;

        try
        {
            udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

            isRunning = true;
            receiveThread = new Thread(new ThreadStart(ReceiveDatagramLoop))
            {
                IsBackground = true,
                Name = "HoloMed_Gesture_UDP_5052"
            };
            receiveThread.Start();
            Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> Gesture Command Bus active on port {udpPort}. Target: {targetModel.name}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HoloMed AI] Failed to bind UDP port {udpPort}: {ex.Message}");
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
                    string payload = Encoding.UTF8.GetString(rawData);
                    incomingQueue.Enqueue(payload);
                }
            }
            catch (SocketException)
            {
                // Expected when socket closes
            }
            catch (Exception ex)
            {
                if (isRunning) Debug.LogError($"[GestureReceiver] Socket error: {ex.Message}");
            }
        }
    }

    private void Update()
    {
        if (targetModel == null) return;

        // Step 1: Process incoming queued UDP packets from Python
        while (incomingQueue.TryDequeue(out string rawPayload))
        {
            if (string.IsNullOrEmpty(rawPayload)) continue;

            if (logIncomingPackets)
            {
                Debug.Log($"[GestureReceiver] RX: {rawPayload}");
            }

            ProcessGestureCommand(rawPayload);
        }

        // Step 2: Apply Temporal Smoothing to prevent hand tremor / jitter
        ApplyTemporalSmoothing();
    }

    /// <summary>
    /// Command Bus: Parses incoming commands and updates target transformation goals.
    /// </summary>
    private void ProcessGestureCommand(string payload)
    {
        string upper = payload.ToUpperInvariant();
        string command = ExtractCommand(upper);

        // Extract potential float parameters from JSON / text
        float dx = ExtractFloat(payload, "delta_x", "dx", "x");
        float dy = ExtractFloat(payload, "delta_y", "dy", "y");
        float val = ExtractFloat(payload, "value", "angle_delta", "rot", "angle", "axis_x_value");
        float axisXVal = ExtractFloat(payload, "axis_x_value", "rot_x");

        switch (command)
        {
            // -------------------------------------------------------------
            // ROTATE: Apply smooth delta rotation (dX & dY) in World Space
            // -------------------------------------------------------------
            case "ROTATE":
            case "SPIN":
            {
                float rotY = 0f;
                float rotX = 0f;

                // Explicit Axis commands (e.g. from ESP32 Rotary or NLP)
                if (upper.Contains("\"AXIS\": \"Y\"") || upper.Contains("'AXIS': 'Y'") || upper.Contains("AXIS: Y"))
                {
                    rotY = (val != 0f ? val : rotateSpeed * Time.deltaTime);
                    if (axisXVal != 0f) rotX = axisXVal;
                }
                else if (upper.Contains("\"AXIS\": \"X\"") || upper.Contains("'AXIS': 'X'") || upper.Contains("AXIS: X"))
                {
                    rotX = (val != 0f ? val : rotateSpeed * Time.deltaTime);
                }
                else if (upper.Contains("CW") || upper.Contains("RIGHT"))
                {
                    rotY = -(val != 0f ? Mathf.Abs(val) : rotateSpeed * Time.deltaTime);
                }
                else if (upper.Contains("CCW") || upper.Contains("LEFT"))
                {
                    rotY = (val != 0f ? Mathf.Abs(val) : rotateSpeed * Time.deltaTime);
                }
                else
                {
                    // Multi-axis delta from MediaPipe HandLandmarker
                    rotY = (dx != 0f) ? -dx * rotateSpeed : (val != 0f ? val : rotateSpeed * Time.deltaTime);
                    rotX = (dy != 0f) ? -dy * rotateSpeed : axisXVal;
                }

                Quaternion deltaRot = Quaternion.Euler(rotX, rotY, 0f);
                targetRotation = deltaRot * targetRotation;
                break;
            }

            // -------------------------------------------------------------
            // PINCH / DRAG / TRANSLATE: Smooth movement across screen plane
            // -------------------------------------------------------------
            case "PINCH":
            case "DRAG":
            case "TRANSLATE":
            case "MOVE":
            {
                if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                {
                    dx = 0.02f; // Fallback nudge if binary gesture is received
                }

                Vector3 deltaPos = new Vector3(dx * moveSpeed * 0.1f, -dy * moveSpeed * 0.1f, 0f);
                targetPosition += deltaPos;
                break;
            }

            // -------------------------------------------------------------
            // ZOOM / SCALE: Adjust localScale within safe medical bounds
            // -------------------------------------------------------------
            case "ZOOM":
            case "SCALE":
            case "ZOOM_SPREAD":
            case "ZOOM_CLOSE":
            {
                float scaleFactor = 0f;

                if (command == "ZOOM_SPREAD" || upper.Contains("ZOOM_IN") || upper.Contains("SCALE_UP"))
                {
                    scaleFactor = zoomSpeed * 0.05f;
                }
                else if (command == "ZOOM_CLOSE" || upper.Contains("ZOOM_OUT") || upper.Contains("SCALE_DOWN"))
                {
                    scaleFactor = -zoomSpeed * 0.05f;
                }
                else if (val != 0f)
                {
                    scaleFactor = val * zoomSpeed * 0.1f;
                }

                Vector3 newScale = targetScale + Vector3.one * scaleFactor;
                float clampedX = Mathf.Clamp(newScale.x, MinScale, MaxScale);
                targetScale = new Vector3(clampedX, clampedX, clampedX);
                break;
            }

            // -------------------------------------------------------------
            // RESET: Restore Initial Transform Coordinates
            // -------------------------------------------------------------
            case "RESET":
            {
                targetPosition = initialPosition;
                targetRotation = initialRotation;
                targetScale = initialScale;
                break;
            }

            // -------------------------------------------------------------
            // SELECT: Trigger Component Highlight or Feedback
            // -------------------------------------------------------------
            case "SELECT":
            {
                // Optional: Pulse or trigger visual feedback on selected organ
                break;
            }

            default:
                break;
        }
    }

    /// <summary>
    /// Smoothly interpolates the targetModel transform towards targetPosition, targetRotation, and targetScale.
    /// </summary>
    private void ApplyTemporalSmoothing()
    {
        float t = Mathf.Clamp01(Time.deltaTime * smoothingFactor);

        // Smooth Position
        targetModel.localPosition = Vector3.Lerp(targetModel.localPosition, targetPosition, t);

        // Smooth Rotation
        targetModel.localRotation = Quaternion.Slerp(targetModel.localRotation, targetRotation, t);

        // Smooth Scale
        targetModel.localScale = Vector3.Lerp(targetModel.localScale, targetScale, t);
    }

    /// <summary>
    /// Identifies the primary command name from incoming payloads.
    /// </summary>
    private string ExtractCommand(string upperPayload)
    {
        if (upperPayload.Contains("ROTATE") || upperPayload.Contains("SPIN")) return "ROTATE";
        if (upperPayload.Contains("PINCH") || upperPayload.Contains("DRAG") || upperPayload.Contains("TRANSLATE") || upperPayload.Contains("MOVE")) return "PINCH";
        if (upperPayload.Contains("ZOOM_SPREAD")) return "ZOOM_SPREAD";
        if (upperPayload.Contains("ZOOM_CLOSE")) return "ZOOM_CLOSE";
        if (upperPayload.Contains("ZOOM") || upperPayload.Contains("SCALE")) return "ZOOM";
        if (upperPayload.Contains("RESET")) return "RESET";
        if (upperPayload.Contains("SELECT")) return "SELECT";
        return "UNKNOWN";
    }

    /// <summary>
    /// Regex helper to extract numeric values from JSON keys or string representations.
    /// </summary>
    private float ExtractFloat(string text, params string[] keys)
    {
        foreach (string key in keys)
        {
            string pattern = $"[\"']?{key}[\"']?\\s*[:=]\\s*([+-]?[0-9]*\\.?[0-9]+)";
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
            {
                return val;
            }
        }
        return 0f;
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

    private void OnDisable() => StopUDPServer();
    private void OnDestroy() => StopUDPServer();
    private void OnApplicationQuit() => StopUDPServer();
}