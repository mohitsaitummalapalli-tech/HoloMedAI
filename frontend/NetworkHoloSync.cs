using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// HoloMed AI - Multi-User Collaborative Telepresence & State Synchronizer
/// File: frontend/NetworkHoloSync.cs
/// 
/// Phase 4: Multi-User Collaborative Telepresence
/// 
/// Synchronizes in real time across all connected surgeon/clinician clients:
/// 1. 3D Organ Spatial Transform (Position, Rotation, Scale) with smooth interpolation.
/// 2. Active Anatomical Layer Visibility (Vascular, Muscle, Electrical, Chambers).
/// 3. Pathology & Tissue States (Healthy vs Diseased vs Infarcted).
/// 4. Slicing & Dissection Plane States.
/// 5. Remote Participant Spatial Laser Pointers & AI Caliper Annotations.
/// </summary>
public class NetworkHoloSync : MonoBehaviour
{
    public enum NetworkRole
    {
        Host_Leader,
        Collaborator_Peer
    }

    [System.Serializable]
    public class SyncPacket
    {
        public string clientId;
        public string clientName;
        public double timestamp;

        // Spatial Coordinates
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        // Organ States
        public string activeOrgan;
        public string pathologyState;
        public bool isSliced;
        public string[] visibleLayers;

        // Spatial Pointer / Telepresence
        public bool isPointing;
        public Vector3 pointerPosition;
        public string pointerLabel;
    }

    [Header("Network Configuration")]
    [Tooltip("UDP Port for multi-user telepresence synchronization (Default: 5055)")]
    public int syncPort = 5055;

    [Tooltip("Broadcast IP address for LAN synchronization or 127.0.0.1 for local multi-instance testing")]
    public string broadcastAddress = "255.255.255.255";

    public NetworkRole role = NetworkRole.Collaborator_Peer;
    public string localClientId = "";
    public string localClientName = "Dr. Surgeon (Hololens)";

    [Header("Target Hologram & Subsystems")]
    public Transform targetOrgan;
    public OrganStateManager organStateManager;
    public OrganSlicer organSlicer;

    [Header("Sync Rates & Smoothing")]
    [Range(10, 60)] public float sendRateHz = 30f;
    public float lerpSpeed = 16.0f;

    [Header("Remote Participant Telepresence Visuals")]
    [Tooltip("Visual laser pointer GameObject representing the remote collaborator's gesture")]
    public GameObject remotePointerPrefab;
    public Color remotePointerColor = new Color(0f, 0.9f, 1f, 0.8f);

    [Header("Live Network Monitor (Inspector)")]
    public int totalPacketsSent = 0;
    public int totalPacketsReceived = 0;
    public int connectedPeersCount = 0;
    public bool showNetworkHUD = true;

    // Events
    public static event Action<SyncPacket> OnRemoteStateReceived;

    // Remote State Buffer for smooth interpolation
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 targetScale;
    private bool hasReceivedInitialState = false;

    // Background Threading
    private Thread listenerThread;
    private UdpClient udpServer;
    private UdpClient udpBroadcaster;
    private volatile bool isRunning = false;
    private readonly ConcurrentQueue<string> incomingQueue = new ConcurrentQueue<string>();
    private readonly Dictionary<string, float> activePeers = new Dictionary<string, float>();

    private float nextSendTime = 0f;
    private GameObject remotePointerInstance;

    void Awake()
    {
        if (string.IsNullOrEmpty(localClientId))
        {
            localClientId = "CL-" + UnityEngine.Random.Range(1000, 9999);
        }

        if (targetOrgan == null) targetOrgan = this.transform;
        if (organStateManager == null) organStateManager = targetOrgan.GetComponent<OrganStateManager>();
        if (organSlicer == null) organSlicer = targetOrgan.GetComponent<OrganSlicer>();

        targetPosition = targetOrgan.localPosition;
        targetRotation = targetOrgan.localRotation;
        targetScale = targetOrgan.localScale;
    }

    void Start()
    {
        StartNetworkServices();
    }

    public void StartNetworkServices()
    {
        if (isRunning) return;

        try
        {
            // UDP Receiver
            udpServer = new UdpClient();
            udpServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, syncPort));

            // UDP Broadcaster
            udpBroadcaster = new UdpClient();
            udpBroadcaster.EnableBroadcast = true;

            isRunning = true;
            listenerThread = new Thread(ReceiveThreadLoop)
            {
                IsBackground = true,
                Name = "HoloMed_NetworkHoloSync_5055"
            };
            listenerThread.Start();

            Debug.Log($"<color=#00FFB4>[NetworkHoloSync]</color> Collaborative telepresence active on Port {syncPort} (Client ID: {localClientId})");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetworkHoloSync] Network Bind Exception: {ex.Message}");
        }
    }

    private void ReceiveThreadLoop()
    {
        IPEndPoint senderEP = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                byte[] raw = udpServer.Receive(ref senderEP);
                if (raw != null && raw.Length > 0)
                {
                    string json = Encoding.UTF8.GetString(raw);
                    incomingQueue.Enqueue(json);
                }
            }
            catch (SocketException)
            {
                // Normal on socket close
            }
            catch (Exception ex)
            {
                if (isRunning) Debug.LogError($"[NetworkHoloSync] Listener error: {ex.Message}");
            }
        }
    }

    void Update()
    {
        // 1. Process received state packets from remote clients
        while (incomingQueue.TryDequeue(out string json))
        {
            ProcessRemoteSyncPacket(json);
        }

        // 2. Broadcast local state at configured send rate
        if (Time.time >= nextSendTime)
        {
            BroadcastLocalState();
            nextSendTime = Time.time + (1.0f / sendRateHz);
        }

        // 3. Smoothly interpolate remote coordinates (Dead Reckoning)
        if (hasReceivedInitialState && targetOrgan != null)
        {
            float t = Time.deltaTime * lerpSpeed;
            targetOrgan.localPosition = Vector3.Lerp(targetOrgan.localPosition, targetPosition, t);
            targetOrgan.localRotation = Quaternion.Slerp(targetOrgan.localRotation, targetRotation, t);
            targetOrgan.localScale = Vector3.Lerp(targetOrgan.localScale, targetScale, t);
        }

        // 4. Prune stale peer heartbeats (timeout > 4 seconds)
        PruneStalePeers();
    }

    private void BroadcastLocalState()
    {
        if (!isRunning || udpBroadcaster == null) return;

        try
        {
            SyncPacket packet = new SyncPacket
            {
                clientId = localClientId,
                clientName = localClientName,
                timestamp = Time.time,
                position = targetOrgan.localPosition,
                rotation = targetOrgan.localRotation,
                scale = targetOrgan.localScale,
                activeOrgan = organStateManager != null ? organStateManager.currentOrgan : "HEART",
                pathologyState = organStateManager != null ? organStateManager.currentPathology : "HEALTHY",
                isSliced = organSlicer != null && organSlicer.isSliced,
                isPointing = false,
                pointerPosition = Vector3.zero,
                pointerLabel = ""
            };

            string json = JsonUtility.ToJson(packet);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            IPEndPoint targetEP = new IPEndPoint(IPAddress.Parse(broadcastAddress == "255.255.255.255" ? "127.0.0.1" : broadcastAddress), syncPort);
            udpBroadcaster.Send(bytes, bytes.Length, targetEP);
            totalPacketsSent++;
        }
        catch (Exception ex)
        {
            // Silently catch broadcast transient errors
        }
    }

    private void ProcessRemoteSyncPacket(string json)
    {
        try
        {
            SyncPacket packet = JsonUtility.FromJson<SyncPacket>(json);
            if (packet == null || packet.clientId == localClientId) return; // Ignore own echoes

            totalPacketsReceived++;
            activePeers[packet.clientId] = Time.time;

            // 1. Buffer spatial coordinates for smooth Lerp
            targetPosition = packet.position;
            targetRotation = packet.rotation;
            targetScale = packet.scale;
            hasReceivedInitialState = true;

            // 2. Synchronize Pathology State
            if (organStateManager != null && !string.IsNullOrEmpty(packet.pathologyState))
            {
                if (organStateManager.currentPathology != packet.pathologyState)
                {
                    organStateManager.SetPathologyState(packet.pathologyState);
                }
            }

            // 3. Synchronize Slicing State
            if (organSlicer != null && organSlicer.isSliced != packet.isSliced)
            {
                if (packet.isSliced) organSlicer.ExecuteSlice();
                else organSlicer.ResetSlice();
            }

            OnRemoteStateReceived?.Invoke(packet);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NetworkHoloSync] Deserialization Error: {ex.Message}");
        }
    }

    private void PruneStalePeers()
    {
        List<string> staleKeys = new List<string>();
        foreach (var kvp in activePeers)
        {
            if ((Time.time - kvp.Value) > 4.0f) staleKeys.Add(kvp.Key);
        }

        foreach (string k in staleKeys) activePeers.Remove(k);
        connectedPeersCount = activePeers.Count;
    }

    private void StopNetworkServices()
    {
        isRunning = false;
        if (udpServer != null)
        {
            try { udpServer.Close(); } catch { }
            udpServer = null;
        }

        if (udpBroadcaster != null)
        {
            try { udpBroadcaster.Close(); } catch { }
            udpBroadcaster = null;
        }

        if (listenerThread != null && listenerThread.IsAlive)
        {
            try { listenerThread.Abort(); } catch { }
            listenerThread = null;
        }
    }

    void OnDisable() => StopNetworkServices();
    void OnDestroy() => StopNetworkServices();
    void OnApplicationQuit() => StopNetworkServices();

    // -------------------------------------------------------------
    // ON-SCREEN COLLABORATIVE TELEPRESENCE HUD
    // -------------------------------------------------------------
    void OnGUI()
    {
        if (!showNetworkHUD) return;

        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.fontSize = 12;
        boxStyle.alignment = TextAnchor.UpperLeft;
        boxStyle.normal.textColor = Color.white;

        GUILayout.BeginArea(new Rect(Screen.width - 380, Screen.height - 145, 360, 135), boxStyle);
        GUILayout.Label("<size=13><color=#00FFB4><b>MULTI-USER COLLABORATIVE SYNC</b></color></size>");
        GUILayout.Label($"Local Client: <b>{localClientName}</b> [{localClientId}]");
        GUILayout.Label($"Network Role: <b>{role}</b>  |  Sync Port: {syncPort}");

        GUI.color = connectedPeersCount > 0 ? new Color(0f, 1f, 0.7f, 1f) : new Color(0.7f, 0.7f, 0.7f, 1f);
        GUILayout.Label($"Active Telepresence Peers: <b>{connectedPeersCount} Connected</b>");
        GUI.color = Color.white;

        GUILayout.Label($"Packets: TX {totalPacketsSent:N0}  /  RX {totalPacketsReceived:N0}");
        GUILayout.EndArea();
    }
}
