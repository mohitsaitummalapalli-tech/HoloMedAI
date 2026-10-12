using System;
using System.Collections.Concurrent;
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
    /// Connects to Python Perception / Vision IPC via UDP (port 5052) and WebSocket (port 8080).
    /// Uses Main Thread Action Dispatching to rotate the GameObject named "Heart".
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

        [Header("Networking Configuration")]
        [Tooltip("UDP Port matching Python vision HUD (default: 5052)")]
        [SerializeField] private int udpPort = 5052;
        [SerializeField] private bool enableUdp = true;

        [Header("WebSocket Configuration")]
        [SerializeField] private bool enableWebSocket = true;
        [SerializeField] private string websocketUri = "ws://127.0.0.1:8080";
        [SerializeField] private float reconnectIntervalSeconds = 3.0f;

        [Header("Diagnostics")]
        [SerializeField] private bool verboseLogging = false;

        // 1. Thread-safe execution queue for Main Thread Dispatching
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();

        // Network state
        private UdpClient udpClient;
        private Thread udpThread;
        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
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

            // 3. Port Alignment: Explicitly listen on UDP 5052
            if (enableUdp)
            {
                StartUdpListener();
            }

            if (enableWebSocket)
            {
                Task.Run(() => WebSocketWorker(cts.Token));
            }
        }

        private void LocateTarget()
        {
            if (targetHeart == null)
            {
                targetHeart = GameObject.Find(targetGameObjectName) ?? GameObject.Find("Heart");
            }
        }

        // 2. Execute in Update(): dequeue actions on the Unity Main Render Thread
        private void Update()
        {
            while (_executionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AnatomyIpcClient] Main Thread execution error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called from background networking threads. Enqueues actions to _executionQueue.
        /// </summary>
        public void EnqueueIncomingPayload(string rawJson)
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
                    actionType.Equals("DRAG", StringComparison.OrdinalIgnoreCase) ||
                    actionType.Equals("ROTATE", StringComparison.OrdinalIgnoreCase))
                {
                    float dx = msg.dx;
                    float dy = msg.dy;

                    if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                    {
                        ExtractDeltasFallback(trimmed, out dx, out dy);
                    }

                    // 1. Implement Main Thread Dispatching via _executionQueue
                    _executionQueue.Enqueue(() =>
                    {
                        if (targetHeart == null)
                        {
                            LocateTarget();
                        }

                        if (targetHeart != null)
                        {
                            // 4. Debug Logs inside the Update loop right before rotating the object
                            Debug.Log($"[HoloMed] Rotating Heart dx:{dx} dy:{dy}");

                            targetHeart.transform.Rotate(Vector3.up, -dx * rotationSpeed, Space.World);
                            targetHeart.transform.Rotate(Vector3.right, dy * rotationSpeed, Space.World);
                        }
                    });
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
                Debug.Log($"[AnatomyIpcClient] Listening on UDP port {udpPort}");
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
                        EnqueueIncomingPayload(text);
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
                            EnqueueIncomingPayload(text);
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
