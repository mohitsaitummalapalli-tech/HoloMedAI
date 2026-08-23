using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Voice Command & AI Intent Receiver
/// File: frontend/VoiceCommandReceiver.cs
/// 
/// Phase 1: The AI & Visual Core
/// 
/// Listens on UDP Port 5052 (Voice & AI Core) and routes commands to:
/// 1. OrganStateManager: Layer visibility toggles and Pathology (Healthy vs Diseased) states.
/// 2. Clinical Queries: Captures and displays "ai_speech_response" for medical questions.
/// 3. Transform Animations: Additive rotations, highlights, and reset triggers.
/// </summary>
public class VoiceCommandReceiver : MonoBehaviour
{
    [Serializable]
    public class VoiceCommandPayload
    {
        public string type;
        public string command;
        public string action;
        public string organ;
        public string target_part;
        public string direction;
        public string ai_speech_response;
        public string query_text;
        public string layer_state;
        public string target_layer;
        public bool layer_visible;
        public string pathology;
        public float value;
        public double timestamp;
    }

    private class RendererSnapshot
    {
        public Renderer renderer;
        public Color[] colors;
        public bool[] hasColor;
        public bool[] baseColor;
    }

    [Header("Network Configuration")]
    [Tooltip("UDP Port for Voice Commands (Default: 5052, Gestures use 5053)")]
    public int port = 5052;

    [Header("Target Hologram & State Manager")]
    [Tooltip("Target Organ Transform. Defaults to this GameObject.")]
    public Transform targetRoot;

    [Tooltip("Reference to OrganStateManager for Layer & Pathology toggles. Auto-resolved if empty.")]
    public OrganStateManager organStateManager;

    [Header("Visual Feedback Settings")]
    public Color highlightColor = Color.yellow;
    [Min(0.1f)] public float highlightDuration = 2.5f;
    [Min(0.01f)] public float rotateDuration = 0.75f;

    [Header("AI Speech & Diagnostic Monitor (Inspector)")]
    [TextArea(2, 5)]
    public string lastAiSpeechResponse = "";
    public string lastVoiceCommand = "IDLE";
    public int totalVoicePacketsReceived = 0;
    [Tooltip("Toggle with F1 or H key. Defaults to false for clean viewport.")]
    public bool showOnScreenHUD = false;

    // Events for external UI/Audio TTS subscribers
    public static event Action<VoiceCommandPayload> OnVoiceCommandReceived;
    public static event Action<string> OnAiSpeechGenerated;

    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();
    private readonly List<RendererSnapshot> rendererSnapshots = new List<RendererSnapshot>();
    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool isRunning;
    private Coroutine rotationCoroutine;
    private Coroutine highlightCoroutine;
    private Vector3 initialPosition;
    [Tooltip("Reference to VitalsSimulationManager for patient vitals simulation and trauma events")]
    public VitalsSimulationManager vitalsManager;

    [Tooltip("Reference to DICOMVolumeLoader for live 3D scan rendering and window leveling")]
    public DICOMVolumeLoader dicomLoader;

    private Quaternion initialRotation;
    private Vector3 initialScale;
    private float lastPacketTime = 0f;

    private Transform TargetTransform => targetRoot != null ? targetRoot : transform;

    private void Awake()
    {
        initialPosition = TargetTransform.localPosition;
        initialRotation = TargetTransform.localRotation;
        initialScale = TargetTransform.localScale;

        if (organStateManager == null)
        {
            organStateManager = TargetTransform.GetComponent<OrganStateManager>();
            if (organStateManager == null) organStateManager = FindFirstObjectByType<OrganStateManager>();
        }

        if (organSlicer == null)
        {
            organSlicer = TargetTransform.GetComponent<OrganSlicer>();
            if (organSlicer == null) organSlicer = FindFirstObjectByType<OrganSlicer>();
        }

        if (vitalsManager == null)
        {
            vitalsManager = FindFirstObjectByType<VitalsSimulationManager>();
        }

        if (dicomLoader == null)
        {
            dicomLoader = TargetTransform.GetComponent<DICOMVolumeLoader>();
            if (dicomLoader == null) dicomLoader = FindFirstObjectByType<DICOMVolumeLoader>();
        }

        CacheRendererStates();
    }

    private void Start() => StartListener();

    private void Update()
    {
        // 0. Toggle HUD on demand via F1 or H key
        if (Input.GetKeyDown(KeyCode.F1) || Input.GetKeyDown(KeyCode.H))
        {
            showOnScreenHUD = !showOnScreenHUD;
        }

        // Process queued UDP packets on Unity Main Thread
        while (incomingQueue.TryDequeue(out string json))
        {
            ProcessCommand(json);
        }
    }

    private void StartListener()
    {
        if (isRunning) return;

        isRunning = true;
        receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "HoloMedVoiceUdpListener_5052"
        };
        receiveThread.Start();

        Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> VoiceCommandReceiver listening on UDP Port {port}");
    }

    private void ReceiveLoop()
    {
        try
        {
            udpClient = new UdpClient(port);
            IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref sender);
                    if (data != null && data.Length > 0)
                    {
                        incomingQueue.Enqueue(Encoding.UTF8.GetString(data));
                    }
                }
                catch (SocketException)
                {
                    // Socket closing
                }
            }
        }
        catch (Exception error)
        {
            if (isRunning)
            {
                Debug.LogError($"[VoiceCommandReceiver] Cannot bind UDP port {port}: {error.Message}");
            }
        }
    }

    private void ProcessCommand(string json)
    {
        try
        {
            VoiceCommandPayload payload = JsonUtility.FromJson<VoiceCommandPayload>(json);
            if (payload == null) return;

            string cmd = !string.IsNullOrEmpty(payload.command) ? payload.command : payload.action;
            if (string.IsNullOrWhiteSpace(cmd)) return;

            cmd = cmd.Trim().ToUpperInvariant();
            lastVoiceCommand = cmd;
            lastPacketTime = Time.time;
            totalVoicePacketsReceived++;

            // 1. Capture AI Speech Clinical Response
            if (!string.IsNullOrEmpty(payload.ai_speech_response))
            {
                lastAiSpeechResponse = payload.ai_speech_response;
                Debug.Log($"<color=#00E5FF><b>[Ultron AI Clinical Core]</b></color> {payload.ai_speech_response}");
                OnAiSpeechGenerated?.Invoke(payload.ai_speech_response);
            }

            OnVoiceCommandReceived?.Invoke(payload);

            // 2. Route Pathology State (Healthy vs Diseased)
            if (!string.IsNullOrEmpty(payload.pathology) && organStateManager != null)
            {
                organStateManager.SetPathologyState(payload.pathology);
            }

            // 3. Route Layer Toggles
            if (!string.IsNullOrEmpty(payload.target_layer) && organStateManager != null)
            {
                organStateManager.SetLayerVisibility(payload.target_layer, payload.layer_visible);
            }

            // 4. Dispatch Command Actions
            switch (cmd)
            {
                case "MEDICAL_QUERY":
                case "QUERY_RESPONSE":
                    // Speech response already captured and logged above
                    break;

                case "LAYER_TOGGLE":
                    if (organStateManager != null && !string.IsNullOrEmpty(payload.target_layer))
                    {
                        organStateManager.SetLayerVisibility(payload.target_layer, payload.layer_visible);
                    }
                    break;

                case "SET_PATHOLOGY":
                case "PATHOLOGY_TOGGLE":
                    if (organStateManager != null && !string.IsNullOrEmpty(payload.pathology))
                    {
                        organStateManager.SetPathologyState(payload.pathology);
                    }
                    if (vitalsManager != null && !string.IsNullOrEmpty(payload.pathology))
                    {
                        vitalsManager.OnPathologyStateChanged(payload.pathology);
                    }
                    break;

                case "ROTATE":
                    HandleRotate(payload);
                    break;

                case "HIDE":
                    HandleHide(payload);
                    break;

                case "HIGHLIGHT":
                    HandleHighlight(payload);
                    break;

                case "SECTION_VIEW":
                case "SLICE":
                case "CUT":
                    if (organSlicer != null)
                    {
                        organSlicer.ExecuteSlice();
                    }
                    else if (organStateManager != null)
                    {
                        organStateManager.isSectionCutawayActive = !organStateManager.isSectionCutawayActive;
                    }
                    if (vitalsManager != null)
                    {
                        vitalsManager.TriggerTraumaSpike("Voice Surgical Incision", 1.2f);
                    }
                    break;

                case "LOAD_DICOM_SERIES":
                    if (dicomLoader != null)
                    {
                        DICOMVolumeLoader.DICOMPayload dicomPayload = JsonUtility.FromJson<DICOMVolumeLoader.DICOMPayload>(json);
                        dicomLoader.LoadDICOMSeries(dicomPayload);
                    }
                    break;

                case "SET_WINDOW_LEVEL":
                case "WINDOW_LEVEL":
                    if (dicomLoader != null)
                    {
                        DICOMVolumeLoader.DICOMPayload wp = JsonUtility.FromJson<DICOMVolumeLoader.DICOMPayload>(json);
                        if (!string.IsNullOrEmpty(wp.preset)) dicomLoader.ApplyPreset(wp.preset);
                        else if (wp.window_width > 0) dicomLoader.SetWindowLevel(wp.window_center, wp.window_width);
                    }
                    break;

                case "RESET":
                    HandleReset();
                    if (vitalsManager != null) vitalsManager.ResetVitals();
                    if (organSlicer != null) organSlicer.ResetSlice();
                    break;

                default:
                    Debug.LogWarning($"[VoiceCommandReceiver] Unhandled command: {cmd}");
                    break;
            }
        }
        catch (Exception error)
        {
            Debug.LogError($"[VoiceCommandReceiver] JSON parse error: {error.Message}\nRaw: {json}");
        }
    }

    private void HandleRotate(VoiceCommandPayload payload)
    {
        float angle = string.Equals(payload.direction, "RIGHT", StringComparison.OrdinalIgnoreCase) ? 180f : -180f;
        Quaternion endRotation = TargetTransform.localRotation * Quaternion.Euler(0f, angle, 0f);

        if (rotationCoroutine != null) StopCoroutine(rotationCoroutine);
        rotationCoroutine = StartCoroutine(SmoothRotate(endRotation));
    }

    private IEnumerator SmoothRotate(Quaternion endRotation)
    {
        Quaternion startRotation = TargetTransform.localRotation;
        float elapsed = 0f;

        while (elapsed < rotateDuration)
        {
            elapsed += Time.deltaTime;
            TargetTransform.localRotation = Quaternion.Slerp(startRotation, endRotation, elapsed / rotateDuration);
            yield return null;
        }

        TargetTransform.localRotation = endRotation;
        rotationCoroutine = null;
    }

    private void HandleHide(VoiceCommandPayload payload)
    {
        if (organStateManager != null && !string.IsNullOrEmpty(payload.target_part))
        {
            organStateManager.SetLayerVisibility(payload.target_part, false);
        }
        else if (TryGetTargetRenderers(payload.target_part, out List<Renderer> renderers))
        {
            foreach (Renderer r in renderers) r.enabled = false;
        }
    }

    private void HandleHighlight(VoiceCommandPayload payload)
    {
        if (!TryGetTargetRenderers(payload.target_part, out List<Renderer> renderers)) return;

        foreach (Renderer renderer in renderers)
        {
            renderer.enabled = true;
            foreach (Material material in renderer.materials)
            {
                SetMaterialColor(material, highlightColor);
            }
        }

        if (highlightCoroutine != null) StopCoroutine(highlightCoroutine);
        highlightCoroutine = StartCoroutine(RestoreHighlightedColorsAfterDelay());
    }

    private IEnumerator RestoreHighlightedColorsAfterDelay()
    {
        yield return new WaitForSeconds(highlightDuration);
        RestoreMaterialColors();
        highlightCoroutine = null;
    }

    private void HandleReset()
    {
        if (rotationCoroutine != null) StopCoroutine(rotationCoroutine);
        if (highlightCoroutine != null) StopCoroutine(highlightCoroutine);

        rotationCoroutine = null;
        highlightCoroutine = null;

        TargetTransform.localPosition = initialPosition;
        TargetTransform.localRotation = initialRotation;
        TargetTransform.localScale = initialScale;

        if (organStateManager != null)
        {
            organStateManager.ResetOrganState();
        }

        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer != null) snapshot.renderer.enabled = true;
        }

        RestoreMaterialColors();
    }

    private void CacheRendererStates()
    {
        rendererSnapshots.Clear();
        foreach (Renderer renderer in TargetTransform.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = renderer.materials;
            RendererSnapshot snapshot = new RendererSnapshot
            {
                renderer = renderer,
                colors = new Color[materials.Length],
                hasColor = new bool[materials.Length],
                baseColor = new bool[materials.Length]
            };

            for (int i = 0; i < materials.Length; i++)
            {
                snapshot.baseColor[i] = materials[i].HasProperty("_BaseColor");
                snapshot.hasColor[i] = snapshot.baseColor[i] || materials[i].HasProperty("_Color");

                if (snapshot.hasColor[i])
                {
                    snapshot.colors[i] = snapshot.baseColor[i]
                        ? materials[i].GetColor("_BaseColor")
                        : materials[i].GetColor("_Color");
                }
            }

            rendererSnapshots.Add(snapshot);
        }
    }

    private bool TryGetTargetRenderers(string targetPart, out List<Renderer> renderers)
    {
        renderers = new List<Renderer>();
        string target = Normalize(targetPart);

        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer == null) continue;
            string name = Normalize(snapshot.renderer.gameObject.name);

            if (string.IsNullOrEmpty(target) || target == "all" || name.Contains(target) || target.Contains(name))
            {
                renderers.Add(snapshot.renderer);
            }
        }

        return renderers.Count > 0;
    }

    private void RestoreMaterialColors()
    {
        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer == null) continue;

            Material[] materials = snapshot.renderer.materials;
            int count = Mathf.Min(materials.Length, snapshot.colors.Length);

            for (int i = 0; i < count; i++)
            {
                if (!snapshot.hasColor[i]) continue;
                if (snapshot.baseColor[i])
                {
                    materials[i].SetColor("_BaseColor", snapshot.colors[i]);
                }
                else
                {
                    materials[i].SetColor("_Color", snapshot.colors[i]);
                }
            }
        }
    }

    private static void SetMaterialColor(Material material, Color color)
    {
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }

    private void StopListener()
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

    private void OnDisable() => StopListener();
    private void OnDestroy() => StopListener();
    private void OnApplicationQuit() => StopListener();

    // -------------------------------------------------------------
    // ON-SCREEN DIAGNOSTIC & AI SPEECH HUD (Disabled for clean viewport)
    // -------------------------------------------------------------
    void OnGUI()
    {
        return; // Permanently disabled for clean 3D viewport
    }
}