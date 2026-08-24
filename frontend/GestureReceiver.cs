using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace HoloMedAI.Gestures
{
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
                targetModel = transform;
            }

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
                    break;
                }
                catch (Exception)
                {
                }
            }
        }

        private void Update()
        {
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

        private void ProcessIncomingPayload(string rawJson)
        {
            string trimmed = rawJson.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
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
                if (parsedCommand == null || string.IsNullOrEmpty(parsedCommand.command))
                {
                    Log(GestureLogLevel.WARNING, $"Failed to parse command from JSON: {trimmed}");
                    return;
                }

                commandRouter.RouteCommand(parsedCommand);
            }
            catch (Exception ex)
            {
                Log(GestureLogLevel.ERROR, $"JSON Deserialization exception: {ex.Message} | Payload: {trimmed}");
            }
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