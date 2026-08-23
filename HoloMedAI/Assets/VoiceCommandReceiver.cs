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
/// HoloMed AI - Voice Command Receiver for Unity 3D
/// File: Assets/VoiceCommandReceiver.cs
/// 
/// Runs a non-blocking background UDP listener on Port 5052 (independent from Gestures on 5053).
/// Receives voice command payloads (ROTATE, HIDE, HIGHLIGHT, RESET) and executes them
/// concurrently alongside gesture manipulation.
/// </summary>
public class VoiceCommandReceiver : MonoBehaviour
{
    [Serializable]
    private class VoiceCommandPayload
    {
        public string command;
        public string organ;
        public string target_part;
        public string direction;
    }

    private class RendererSnapshot
    {
        public Renderer renderer;
        public Color[] colors;
        public bool[] hasColor;
        public bool[] baseColor;
    }

    [Header("Network")]
    [Tooltip("UDP Port for Voice Commands (Default: 5052, Gestures use 5053)")]
    public int port = 5052;

    [Header("Target")]
    [Tooltip("Heart model root. Defaults to this GameObject.")]
    public Transform targetRoot;

    [Header("Visual Feedback")]
    public Color highlightColor = Color.yellow;
    [Min(0.1f)] public float highlightDuration = 2f;
    [Min(0.01f)] public float rotateDuration = 0.75f;

    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();
    private readonly List<RendererSnapshot> rendererSnapshots = new List<RendererSnapshot>();
    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool isRunning;
    private Coroutine rotationCoroutine;
    private Coroutine highlightCoroutine;
    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private Vector3 initialScale;

    private Transform TargetTransform => targetRoot != null ? targetRoot : transform;

    private void Awake()
    {
        initialPosition = TargetTransform.localPosition;
        initialRotation = TargetTransform.localRotation;
        initialScale = TargetTransform.localScale;
        CacheRendererStates();
    }

    private void Start() => StartListener();

    private void Update()
    {
        // Unity main thread: all Transform and Renderer modifications happen here.
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

        Debug.Log($"<color=#00FFB4>[HoloMed AI]</color> VoiceCommandReceiver listening on 127.0.0.1:{port}");
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
                catch (SocketException error)
                {
                    if (isRunning)
                    {
                        Debug.LogWarning($"[VoiceCommandReceiver] UDP error: {error.Message}");
                    }
                }
            }
        }
        catch (Exception error)
        {
            if (isRunning)
            {
                Debug.LogError(
                    $"[VoiceCommandReceiver] Cannot bind UDP port {port}: {error.Message}"
                );
            }
        }
    }

    private void ProcessCommand(string json)
    {
        try
        {
            VoiceCommandPayload payload = JsonUtility.FromJson<VoiceCommandPayload>(json);

            if (payload == null || string.IsNullOrWhiteSpace(payload.command))
            {
                Debug.LogWarning($"[VoiceCommandReceiver] Invalid JSON: {json}");
                return;
            }

            Debug.Log($"[VoiceCommandReceiver] Received: {json}");

            switch (payload.command.Trim().ToUpperInvariant())
            {
                case "ROTATE":
                    HandleRotate(payload);
                    break;
                case "HIDE":
                    HandleHide(payload);
                    break;
                case "HIGHLIGHT":
                    HandleHighlight(payload);
                    break;
                case "RESET":
                    HandleReset();
                    break;
                default:
                    Debug.LogWarning(
                        $"[VoiceCommandReceiver] Unsupported command: {payload.command}"
                    );
                    break;
            }
        }
        catch (Exception error)
        {
            Debug.LogError(
                $"[VoiceCommandReceiver] JSON parse error: {error.Message}\n{json}"
            );
        }
    }

    private void HandleRotate(VoiceCommandPayload payload)
    {
        float angle = string.Equals(
            payload.direction,
            "RIGHT",
            StringComparison.OrdinalIgnoreCase
        )
            ? 180f
            : -180f;

        Quaternion endRotation =
            TargetTransform.localRotation * Quaternion.Euler(0f, angle, 0f);

        if (rotationCoroutine != null)
        {
            StopCoroutine(rotationCoroutine);
        }

        rotationCoroutine = StartCoroutine(SmoothRotate(endRotation));
    }

    private IEnumerator SmoothRotate(Quaternion endRotation)
    {
        Quaternion startRotation = TargetTransform.localRotation;
        float elapsed = 0f;

        while (elapsed < rotateDuration)
        {
            elapsed += Time.deltaTime;
            TargetTransform.localRotation = Quaternion.Slerp(
                startRotation,
                endRotation,
                elapsed / rotateDuration
            );
            yield return null;
        }

        TargetTransform.localRotation = endRotation;
        rotationCoroutine = null;
    }

    private void HandleHide(VoiceCommandPayload payload)
    {
        if (!TryGetTargetRenderers(payload.target_part, out List<Renderer> renderers))
        {
            return;
        }

        foreach (Renderer renderer in renderers)
        {
            renderer.enabled = false;
        }
    }

    private void HandleHighlight(VoiceCommandPayload payload)
    {
        if (!TryGetTargetRenderers(payload.target_part, out List<Renderer> renderers))
        {
            return;
        }

        foreach (Renderer renderer in renderers)
        {
            renderer.enabled = true;

            foreach (Material material in renderer.materials)
            {
                SetMaterialColor(material, highlightColor);
            }
        }

        if (highlightCoroutine != null)
        {
            StopCoroutine(highlightCoroutine);
        }

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
        if (rotationCoroutine != null)
        {
            StopCoroutine(rotationCoroutine);
        }

        if (highlightCoroutine != null)
        {
            StopCoroutine(highlightCoroutine);
        }

        rotationCoroutine = null;
        highlightCoroutine = null;

        TargetTransform.localPosition = initialPosition;
        TargetTransform.localRotation = initialRotation;
        TargetTransform.localScale = initialScale;

        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer != null)
            {
                snapshot.renderer.enabled = true;
            }
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
                snapshot.hasColor[i] =
                    snapshot.baseColor[i] || materials[i].HasProperty("_Color");

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

    private bool TryGetTargetRenderers(
        string targetPart,
        out List<Renderer> renderers
    )
    {
        renderers = new List<Renderer>();
        string target = Normalize(targetPart);

        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer == null)
            {
                continue;
            }

            string name = Normalize(snapshot.renderer.gameObject.name);

            if (string.IsNullOrEmpty(target) ||
                target == "all" ||
                name.Contains(target) ||
                target.Contains(name))
            {
                renderers.Add(snapshot.renderer);
            }
        }

        if (renderers.Count == 0)
        {
            Debug.LogWarning(
                $"[VoiceCommandReceiver] Target mesh '{targetPart}' was not found."
            );
            return false;
        }

        return true;
    }

    private void RestoreMaterialColors()
    {
        foreach (RendererSnapshot snapshot in rendererSnapshots)
        {
            if (snapshot.renderer == null)
            {
                continue;
            }

            Material[] materials = snapshot.renderer.materials;
            int count = Mathf.Min(materials.Length, snapshot.colors.Length);

            for (int i = 0; i < count; i++)
            {
                if (!snapshot.hasColor[i])
                {
                    continue;
                }

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
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
    }

    private void StopListener()
    {
        isRunning = false;

        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(100);
        }

        receiveThread = null;
    }

    private void OnDisable() => StopListener();
    private void OnDestroy() => StopListener();
    private void OnApplicationQuit() => StopListener();
}
