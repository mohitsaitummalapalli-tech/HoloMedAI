using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Anatomy IPC Client (Frontend Mirror)
    /// Connects to Python Perception / Vision IPC via WebSocket and UDP.
    /// Extracts dx and dy float values on 'sys.input.interact' payloads.
    /// Targets the GameObject named exactly "Heart" and applies 3D rotation on the main thread.
    /// </summary>
    [DisallowMultipleComponent]
    public class AnatomyIpcClient : MonoBehaviour
    {
        [Header("Target Hologram")]
        [Tooltip("Target GameObject for spatial interaction. Defaults to GameObject named 'Heart'.")]
        [SerializeField] private GameObject targetHeart;
        [SerializeField] private string targetGameObjectName = "Heart";

        [Header("Interaction Calibration")]
        [Tooltip("Speed multiplier for delta rotation.")]
        [SerializeField] private float rotationSpeed = 1.0f;

        [Header("WebSocket Configuration")]
        [SerializeField] private bool enableWebSocket = true;
        [SerializeField] private string websocketUri = "ws://127.0.0.1:8080";
        [SerializeField] private float reconnectIntervalSeconds = 3.0f;

        [Header("UDP Fallback Configuration")]
        [SerializeField] private bool enableUdpFallback = true;
        [SerializeField] private int udpPort = 5052;

        [Header("Diagnostics")]
        [SerializeField] private bool verboseLogging = false;

        // Thread-Safe Inbound Queue
        private readonly ConcurrentQueue<string> inboundQueue = new ConcurrentQueue<string>();

        // Networking state
        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
        private UdpClient udpClient;
        private Thread udpThread;
        private volatile bool isRunning = false;

        [Serializable]
        public class InteractMessage
        {
            public string action;
            public string command;
            public string state;
            public string target;
            public float dx;
            public float dy;
            public double timestamp;
        }

        private void Awake()
        {
            LocateTarget();
        }

        private void Start()
        {
            isRunning = true;
            cts = new CancellationTokenSource();

            if (enableWebSocket)
            {
                Task.Run(() => WebSocketWorker(cts.Token));
            }

            if (enableUdpFallback)
            {
                StartUdpListener();
            }
        }

        private void LocateTarget()
        {
            if (targetHeart == null)
            {
                targetHeart = GameObject.Find(targetGameObjectName);
                if (targetHeart == null)
                {
                    targetHeart = GameObject.Find("Heart");
                }
            }
        }

        private void Update()
        {
            // Ensure target is bound if loaded dynamically
            if (targetHeart == null)
            {
                LocateTarget();
            }

            // Process all pending packets on Unity Main Render Thread
            while (inboundQueue.TryDequeue(out string rawJson))
            {
                ProcessPayload(rawJson);
            }
        }

        /// <summary>
        /// Parses incoming JSON payload, extracts dx and dy for sys.input.interact,
        /// and applies 3D rotation to the "Heart" GameObject on the main thread.
        /// </summary>
        public void ProcessPayload(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson)) return;

            string trimmed = rawJson.Trim();
            if (!trimmed.StartsWith("{")) return;

            try
            {
                InteractMessage msg = JsonUtility.FromJson<InteractMessage>(trimmed);
                if (msg == null) return;

                string actionType = !string.IsNullOrEmpty(msg.action) ? msg.action : msg.command;
                if (string.IsNullOrEmpty(actionType)) return;

                if (actionType.Equals("sys.input.interact", StringComparison.OrdinalIgnoreCase) ||
                    actionType.Equals("INTERACT", StringComparison.OrdinalIgnoreCase) ||
                    actionType.Equals("PINCH_DRAG", StringComparison.OrdinalIgnoreCase) ||
                    actionType.Equals("DRAG", StringComparison.OrdinalIgnoreCase))
                {
                    float dx = msg.dx;
                    float dy = msg.dy;

                    // Fallback parse if JsonUtility did not map flat fields
                    if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                    {
                        ExtractDeltasFallback(trimmed, out dx, out dy);
                    }

                    if (targetHeart != null)
                    {
                        // On the main thread, apply 3D rotation based on the deltas:
                        // Horizontal movement (dx) rotates around World Y axis
                        // Vertical movement (dy) rotates around World X axis
                        targetHeart.transform.Rotate(Vector3.up, -dx * rotationSpeed, Space.World);
                        targetHeart.transform.Rotate(Vector3.right, dy * rotationSpeed, Space.World);

                        if (verboseLogging)
                        {
                            Debug.Log($"[AnatomyIpcClient] Rotated Heart -> dx: {dx:F3}, dy: {dy:F3}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"[AnatomyIpcClient] JSON Parse exception: {ex.Message}");
                }
            }
        }

        private void ExtractDeltasFallback(string json, out float dx, out float dy)
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

        #region WebSocket Implementation

        private async Task WebSocketWorker(CancellationToken token)
        {
            while (isRunning && !token.IsCancellationRequested)
            {
                try
                {
                    using (webSocket = new ClientWebSocket())
                    {
                        Uri serverUri = new Uri(websocketUri);
                        await webSocket.ConnectAsync(serverUri, token);

                        if (verboseLogging)
                        {
                            Debug.Log($"[AnatomyIpcClient] Connected to WebSocket at {websocketUri}");
                        }

                        byte[] buffer = new byte[8192];
                        while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                                break;
                            }

                            string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            inboundQueue.Enqueue(text);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    try { await Task.Delay((int)(reconnectIntervalSeconds * 1000), token); } catch { }
                }
            }
        }

        #endregion

        #region UDP Implementation

        private void StartUdpListener()
        {
            try
            {
                udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

                udpThread = new Thread(UdpListenLoop)
                {
                    IsBackground = true,
                    Name = "AnatomyIpcClient_UDP_Worker"
                };
                udpThread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AnatomyIpcClient] UDP Bind warning on port {udpPort}: {ex.Message}");
            }
        }

        private void UdpListenLoop()
        {
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (isRunning)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref endPoint);
                    if (data != null && data.Length > 0)
                    {
                        string text = Encoding.UTF8.GetString(data);
                        inboundQueue.Enqueue(text);
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch { }
            }
        }

        #endregion

        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();
        private void OnApplicationQuit() => Cleanup();

        private void Cleanup()
        {
            isRunning = false;

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
                cts = null;
            }

            if (webSocket != null)
            {
                try { webSocket.Dispose(); } catch { }
                webSocket = null;
            }

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
        }
    }
}
