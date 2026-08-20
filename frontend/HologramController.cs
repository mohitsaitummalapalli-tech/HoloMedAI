using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HoloMed AI - Frontend Module: Unity 3D Hologram Controller
/// File: frontend/HologramController.cs
/// 
/// Parses incoming JSON commands from Python and orchestrates:
/// - 3D dynamic mesh rotations and zoom scaling.
/// - Anatomical structure highlighting with URP emission / glow shader effects.
/// - Internal cross-section / cutaway visualization.
/// - Smooth thread-safe interpolation on the Unity main rendering thread.
/// </summary>
public class HologramController : MonoBehaviour
{
    [System.Serializable]
    public class HoloCommandPayload
    {
        public string command;
        public string action; // Alias for command
        public string organ;
        public string target;
        public string target_part;
        public string axis;
        public float value;
        public float axis_x_value;
        public bool auto_zoom;
        public string ai_speech_response;
        public float distance_cm;
        public bool active;
    }

    [Header("Target Hologram")]
    [Tooltip("Root Transform of the 3D Anatomical Model (e.g. Heart root)")]
    public Transform hologramRoot;

    [Tooltip("Optional internal cross-section / cutaway mesh GameObject")]
    public GameObject sectionCutawayObject;

    [Header("Smoothing & Motion Speeds")]
    public float rotationDamping = 10f;
    public float zoomDamping = 8f;
    public float minScale = 0.3f;
    public float maxScale = 3.5f;

    [Header("Highlight Shader Settings")]
    [ColorUsage(true, true)]
    public Color highlightEmissionColor = new Color(0f, 1.5f, 2.5f, 1f); // Neon Cyan HDR
    public Color dimAlbedoColor = new Color(0.2f, 0.25f, 0.3f, 0.4f);

    // Internal State
    private Vector3 targetRotationEuler;
    private Vector3 targetScale;
    private Quaternion initialRotation;
    private Vector3 initialScale;

    // Material Caching for Clean Reset & Dimming
    private class MeshState
    {
        public Renderer renderer;
        public Material originalMaterial;
        public Color originalColor;
        public Color originalEmission;
        public bool hadEmission;
    }
    private readonly List<MeshState> cachedMeshStates = new List<MeshState>();
    private Renderer currentlyHighlighted = null;

    void Awake()
    {
        if (hologramRoot == null)
        {
            hologramRoot = this.transform;
        }

        initialRotation = hologramRoot.localRotation;
        initialScale = hologramRoot.localScale;
        targetRotationEuler = hologramRoot.localEulerAngles;
        targetScale = initialScale;

        CacheMeshHierarchy();
    }

    void OnEnable()
    {
        UDPReceiver.OnDataReceived += HandleIncomingData;
    }

    void OnDisable()
    {
        UDPReceiver.OnDataReceived -= HandleIncomingData;
    }

    /// <summary>
    /// Caches all child renderers and their default shader parameters for clean resets.
    /// </summary>
    private void CacheMeshHierarchy()
    {
        cachedMeshStates.Clear();
        Renderer[] renderers = hologramRoot.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            if (r.sharedMaterial == null) continue;

            Material matInstance = r.material; // instantiate unique instance
            Color origCol = matInstance.HasProperty("_BaseColor") ? matInstance.GetColor("_BaseColor") :
                           (matInstance.HasProperty("_Color") ? matInstance.GetColor("_Color") : Color.white);
            Color origEmiss = matInstance.HasProperty("_EmissionColor") ? matInstance.GetColor("_EmissionColor") : Color.black;
            bool hasEmiss = matInstance.IsKeywordEnabled("_EMISSION");

            cachedMeshStates.Add(new MeshState
            {
                renderer = r,
                originalMaterial = matInstance,
                originalColor = origCol,
                originalEmission = origEmiss,
                hadEmission = hasEmiss
            });
        }
    }

    void Update()
    {
        // Smooth Transform Interpolation
        if (hologramRoot != null)
        {
            hologramRoot.localRotation = Quaternion.Slerp(
                hologramRoot.localRotation,
                Quaternion.Euler(targetRotationEuler),
                Time.deltaTime * rotationDamping
            );

            hologramRoot.localScale = Vector3.Lerp(
                hologramRoot.localScale,
                targetScale,
                Time.deltaTime * zoomDamping
            );
        }
    }

    /// <summary>
    /// Main entry point for JSON UDP dispatching.
    /// </summary>
    public void HandleIncomingData(string jsonString)
    {
        if (string.IsNullOrEmpty(jsonString)) return;

        try
        {
            HoloCommandPayload cmd = JsonUtility.FromJson<HoloCommandPayload>(jsonString);
            string commandType = !string.IsNullOrEmpty(cmd.command) ? cmd.command : cmd.action;

            if (string.IsNullOrEmpty(commandType)) return;

            switch (commandType.ToUpper())
            {
                case "ROTATE":
                    ExecuteRotate(cmd);
                    break;
                case "ZOOM":
                    ExecuteZoom(cmd);
                    break;
                case "HIGHLIGHT":
                case "SELECT":
                    ExecuteHighlight(cmd);
                    break;
                case "SECTION_VIEW":
                    ExecuteSectionView(cmd);
                    break;
                case "RESET":
                    ExecuteReset();
                    break;
                case "PRESENCE_UPDATE":
                    ExecutePresenceUpdate(cmd);
                    break;
                default:
                    Debug.Log($"[HologramController] Unhandled command: {commandType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HologramController] JSON Parse Exception: {ex.Message} on data: {jsonString}");
        }
    }

    private void ExecuteRotate(HoloCommandPayload cmd)
    {
        float rotDeltaY = 0f;
        float rotDeltaX = 0f;

        string axis = (cmd.axis ?? "Y").ToUpper();
        if (axis == "Y")
        {
            rotDeltaY = cmd.value;
            rotDeltaX = cmd.axis_x_value;
        }
        else if (axis == "X")
        {
            rotDeltaX = cmd.value;
        }

        targetRotationEuler.y += rotDeltaY;
        targetRotationEuler.x += rotDeltaX;
    }

    private void ExecuteZoom(HoloCommandPayload cmd)
    {
        float zoomDelta = cmd.value * 0.1f;
        float newScaleFactor = Mathf.Clamp(targetScale.x + zoomDelta, minScale, maxScale);
        targetScale = Vector3.one * newScaleFactor;
    }

    private void ExecuteHighlight(HoloCommandPayload cmd)
    {
        string targetPart = cmd.target_part ?? cmd.target;
        if (string.IsNullOrEmpty(targetPart)) return;

        Renderer match = FindMatchingRenderer(targetPart);
        if (match == null)
        {
            Debug.LogWarning($"[HologramController] Anatomical structure '{targetPart}' not found in hierarchy.");
            return;
        }

        currentlyHighlighted = match;

        // Apply Emission Glow & Dim surrounding parts
        foreach (var state in cachedMeshStates)
        {
            if (state.renderer == match)
            {
                // Highlighted object: Bright glow
                Material mat = state.renderer.material;
                mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", highlightEmissionColor);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            }
            else
            {
                // Dim surround meshes
                Material mat = state.renderer.material;
                mat.DisableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", dimAlbedoColor);
                else if (mat.HasProperty("_Color")) mat.SetColor("_Color", dimAlbedoColor);
            }
        }

        // Optional Auto-Zoom
        if (cmd.auto_zoom)
        {
            targetScale = Vector3.one * Mathf.Clamp(initialScale.x * 1.35f, minScale, maxScale);
        }

        Debug.Log($"[HologramController] Highlighted: {match.gameObject.name}");
    }

    private void ExecuteSectionView(HoloCommandPayload cmd)
    {
        if (sectionCutawayObject != null)
        {
            bool nextState = !sectionCutawayObject.activeSelf;
            sectionCutawayObject.SetActive(nextState);
            Debug.Log($"[HologramController] Section view toggled: {nextState}");
        }
        else
        {
            Debug.Log("[HologramController] Section Cutaway Object not assigned.");
        }
    }

    private void ExecuteReset()
    {
        targetRotationEuler = initialRotation.eulerAngles;
        targetScale = initialScale;

        if (sectionCutawayObject != null)
        {
            sectionCutawayObject.SetActive(false);
        }

        // Restore original materials and colors
        foreach (var state in cachedMeshStates)
        {
            if (state.renderer != null)
            {
                Material mat = state.renderer.material;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", state.originalColor);
                else if (mat.HasProperty("_Color")) mat.SetColor("_Color", state.originalColor);

                if (state.hadEmission)
                {
                    mat.EnableKeyword("_EMISSION");
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", state.originalEmission);
                }
                else
                {
                    mat.DisableKeyword("_EMISSION");
                    if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
                }
            }
        }

        currentlyHighlighted = null;
        Debug.Log("[HologramController] View and materials reset to default.");
    }

    private void ExecutePresenceUpdate(HoloCommandPayload cmd)
    {
        if (!cmd.active)
        {
            // Dim hologram or return to idle sleep
            targetScale = initialScale * 0.85f;
        }
        else
        {
            targetScale = initialScale;
        }
    }

    /// <summary>
    /// Searches child hierarchy with case-insensitive, sanitized substring matching.
    /// </summary>
    private Renderer FindMatchingRenderer(string targetName)
    {
        string normalized = targetName.Replace("_", "").Replace(" ", "").ToLowerInvariant();

        foreach (var state in cachedMeshStates)
        {
            string objName = state.renderer.gameObject.name.Replace("_", "").Replace(" ", "").ToLowerInvariant();
            if (objName.Contains(normalized) || normalized.Contains(objName))
            {
                return state.renderer;
            }
        }

        return null;
    }
}
