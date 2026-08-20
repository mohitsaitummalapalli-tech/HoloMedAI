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
    [Tooltip("UDP Port matching Python sender (default: 5052)")]
    public int port = 5052;

    [Header("Debug")]
    public bool logIncomingPackets = false;

    // Events
    public static event Action<string> OnDataReceived;

    private Thread receiveThread;
    private UdpClient client;
    private bool isRunning = false;
    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

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
            client = new UdpClient(port);
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    byte[] data = client.Receive(ref remoteEndPoint);
                    string text = Encoding.UTF8.GetString(data);
                    messageQueue.Enqueue(text);
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

    void Update()
    {
        // Dequeue and process messages on Unity's Main Render Thread
        while (messageQueue.TryDequeue(out string packet))
        {
            if (logIncomingPackets)
            {
                Debug.Log($"[HoloMed UDPReceiver] Dispatched: {packet}");
            }
            OnDataReceived?.Invoke(packet);
        }
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
