using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Thread-Safe UDP Receiver for Unity 3D
/// Listens for JSON packets streamed from Python modules and dispatches them on the Unity Main Thread.
/// </summary>
public class UDPReceiver : MonoBehaviour
{
    [Header("Network Configuration")]
    [Tooltip("UDP Port matching Python vision HUD (default: 5052)")]
    public int port = 5052;

    [Header("Rotation Calibration")]
    [SerializeField] private float rotationSpeed = 1.0f;

    [Header("Debug")]
    public bool logIncomingPackets = false;

    // Events
    public static event Action<string> OnDataReceived;

    // 1. Thread-safe execution queue for Main Thread Dispatching
    private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

    private Thread receiveThread;
    private UdpClient client;
    private bool isRunning = false;

    [System.Serializable]
    private class InteractPayload
    {
        public string action;
        public string command;
        public string state;
        public float dx;
        public float dy;
    }

    void Start()
    {
        StartReceiver();
    }

    public void StartReceiver()
    {
        if (isRunning) return;

        isRunning = true;
        receiveThread = new Thread(new ThreadStart(ReceiveLoop))
        {
            IsBackground = true
        };
        receiveThread.Start();
        Debug.Log($"[HoloMed UDPReceiver] Started listening on port {port}");
    }

    private void ReceiveLoop()
    {
        try
        {
            client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    byte[] data = client.Receive(ref remoteEndPoint);
                    if (data == null || data.Length == 0) continue;

                    string text = Encoding.UTF8.GetString(data);

                    // 1. Queue Main Thread Dispatching
                    _executionQueue.Enqueue(() =>
                    {
                        if (logIncomingPackets)
                        {
                            Debug.Log($"[HoloMed UDPReceiver] Dispatched: {text}");
                        }
                        OnDataReceived?.Invoke(text);

                        // Parse and rotate Heart on Main Thread
                        ProcessPacketOnMainThread(text);
                    });
                }
                catch (SocketException ex)
                {
                    if (isRunning) Debug.LogWarning($"[HoloMed UDPReceiver] SocketException: {ex.Message}");
                }
                catch (Exception err)
                {
                    if (isRunning) Debug.LogError($"[HoloMed UDPReceiver] Read Error: {err.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HoloMed UDPReceiver] Could not bind port {port}: {ex.Message}");
        }
    }

    // 2. Execute in Update(): dequeue actions on the Unity Main Render Thread
    void Update()
    {
        while (_executionQueue.TryDequeue(out Action action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HoloMed UDPReceiver] ExecutionQueue error: {ex.Message}");
            }
        }
    }

    private void ProcessPacketOnMainThread(string packet)
    {
        if (string.IsNullOrEmpty(packet)) return;
        string trimmed = packet.Trim();
        if (!trimmed.StartsWith("{")) return;

        try
        {
            InteractPayload payload = JsonUtility.FromJson<InteractPayload>(trimmed);
            if (payload == null) return;

            string actionType = !string.IsNullOrEmpty(payload.action) ? payload.action : payload.command;
            if (string.IsNullOrEmpty(actionType)) return;

            if (actionType.Equals("sys.input.interact", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("INTERACT", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("PINCH_DRAG", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("DRAG", StringComparison.OrdinalIgnoreCase) ||
                actionType.Equals("ROTATE", StringComparison.OrdinalIgnoreCase))
            {
                float dx = payload.dx;
                float dy = payload.dy;

                // Fallback substring extraction if flat JSON keys were not mapped
                if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                {
                    ExtractDeltas(trimmed, out dx, out dy);
                }

                GameObject heart = GameObject.Find("Heart");
                if (heart != null)
                {
                    // 4. Debug Logs inside the Update loop right before rotation
                    Debug.Log($"[HoloMed] Rotating Heart dx:{dx} dy:{dy}");

                    heart.transform.Rotate(Vector3.up, -dx * rotationSpeed, Space.World);
                    heart.transform.Rotate(Vector3.right, dy * rotationSpeed, Space.World);
                }
            }
        }
        catch { }
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

    private void StopReceiver()
    {
        isRunning = false;
        if (client != null)
        {
            try { client.Close(); } catch { }
            client = null;
        }
        if (receiveThread != null && receiveThread.IsAlive)
        {
            try { receiveThread.Abort(); } catch { }
            receiveThread = null;
        }
        Debug.Log("[HoloMed UDPReceiver] Receiver stopped.");
    }

    void OnDisable()
    {
        StopReceiver();
    }

    void OnApplicationQuit()
    {
        StopReceiver();
    }
}
