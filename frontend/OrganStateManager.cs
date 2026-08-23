using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// HoloMed AI - Organ State, Pathology & Spatial Measurement Manager
/// File: frontend/OrganStateManager.cs
/// 
/// Phase 2: Advanced Spatial Interaction
/// 
/// Responsibilities:
/// 1. Displays real-time AI spatial measurement metrics (caliper distance mm, volume mL, anatomical structure)
///    triggered by the POINT gesture.
/// 2. Manages Anatomical Layer visibility (Vascular, Muscle, Electrical, Chambers).
/// 3. Simulates Healthy vs Diseased tissue material states and cutaway dissection.
/// </summary>
public class OrganStateManager : MonoBehaviour
{
    [System.Serializable]
    public class AnatomicalLayer
    {
        public string layerName = "VASCULAR";
        [Tooltip("Sub-mesh GameObjects or parent groups associated with this anatomical layer")]
        public List<GameObject> layerObjects = new List<GameObject>();
        [Tooltip("Direct renderers associated with this layer")]
        public List<Renderer> layerRenderers = new List<Renderer>();
        public bool isVisible = true;
    }

    [Header("Organ Reference Root")]
    [Tooltip("Root Transform of the active organ model. Defaults to this GameObject if empty.")]
    public Transform organRoot;

    [Header("Anatomical Layers")]
    public List<AnatomicalLayer> layers = new List<AnatomicalLayer>();

    [Header("Materials (Healthy vs Diseased)")]
    [Tooltip("Baseline healthy organ material")]
    public Material healthyMaterial;

    [Tooltip("Pathological / Ischemic / Infarcted tissue material")]
    public Material diseasedMaterial;

    [Header("Pathology Color Overrides (Fallback if material not assigned)")]
    public Color healthyTissueColor = new Color(0.85f, 0.25f, 0.25f, 1.0f);
    public Color diseasedTissueColor = new Color(0.28f, 0.18f, 0.22f, 1.0f);
    public Color ischemiaHighlightColor = new Color(0.15f, 0.05f, 0.05f, 1.0f);

    [Header("AI Spatial Measurement Readout (Point Gesture)")]
    public string currentTargetStructure = "None (Point to measure)";
    public float currentEstimatedDistanceMm = 0.0f;
    public float currentEstimatedVolumeMl = 0.0f;
    public string currentMeasurementLabel = "";
    public float measurementDisplayDuration = 3.5f;

    [Header("State Monitor (Inspector)")]
    public string currentOrgan = "HEART";
    public string currentPathology = "HEALTHY";
    public bool isSectionCutawayActive = false;
    public bool showMeasurementHUD = true;

    // Cache original material states
    private readonly Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
    private readonly List<Renderer> allCachedRenderers = new List<Renderer>();
    private float lastMeasurementTime = -10.0f;

    void Awake()
    {
        if (organRoot == null)
        {
            organRoot = this.transform;
        }

        CacheAllRenderers();
        AutoPopulateLayersIfEmpty();
    }

    /// <summary>
    /// Displays incoming AI spatial measurement metrics when the user points.
    /// </summary>
    public void DisplaySpatialMeasurement(string targetStructure, float distanceMm, float volumeMl, string label)
    {
        currentTargetStructure = !string.IsNullOrEmpty(targetStructure) ? targetStructure : "Cardiac Tissue Zone";
        currentEstimatedDistanceMm = distanceMm;
        currentEstimatedVolumeMl = volumeMl;
        currentMeasurementLabel = !string.IsNullOrEmpty(label) ? label : $"Distance: {distanceMm:F1} mm | Volume: {volumeMl:F1} mL";
        lastMeasurementTime = Time.time;

        Debug.Log($"<color=#00E5FF><b>[AI Spatial Caliper]</b></color> Target: {currentTargetStructure} | {currentMeasurementLabel}");
    }

    /// <summary>
    /// Caches all child renderers and their default healthy materials.
    /// </summary>
    private void CacheAllRenderers()
    {
        allCachedRenderers.Clear();
        originalMaterials.Clear();

        foreach (Renderer r in organRoot.GetComponentsInChildren<Renderer>(true))
        {
            allCachedRenderers.Add(r);
            originalMaterials[r] = r.sharedMaterials;
        }
    }

    /// <summary>
    /// If layers are not configured manually in the Inspector, automatically groups sub-meshes by name keywords.
    /// </summary>
    private void AutoPopulateLayersIfEmpty()
    {
        if (layers.Count > 0) return;

        AnatomicalLayer vascular = new AnatomicalLayer { layerName = "VASCULAR", isVisible = true };
        AnatomicalLayer muscle = new AnatomicalLayer { layerName = "MUSCLE", isVisible = true };
        AnatomicalLayer electrical = new AnatomicalLayer { layerName = "ELECTRICAL", isVisible = true };
        AnatomicalLayer chambers = new AnatomicalLayer { layerName = "CHAMBERS", isVisible = true };

        foreach (Renderer r in allCachedRenderers)
        {
            string n = r.gameObject.name.ToLowerInvariant();
            if (n.Contains("artery") || n.Contains("vein") || n.Contains("vascular") || n.Contains("coronary") || n.Contains("vessel") || n.Contains("aorta"))
            {
                vascular.layerRenderers.Add(r);
            }
            else if (n.Contains("electrical") || n.Contains("node") || n.Contains("bundle") || n.Contains("conduction") || n.Contains("purkinje"))
            {
                electrical.layerRenderers.Add(r);
            }
            else if (n.Contains("chamber") || n.Contains("ventricle") || n.Contains("atrium") || n.Contains("valve") || n.Contains("cavity"))
            {
                chambers.layerRenderers.Add(r);
            }
            else
            {
                muscle.layerRenderers.Add(r);
            }
        }

        layers.Add(vascular);
        layers.Add(muscle);
        layers.Add(electrical);
        layers.Add(chambers);
    }

    /// <summary>
    /// Toggles or sets the visibility of an anatomical layer by name.
    /// </summary>
    public void SetLayerVisibility(string layerName, bool visible)
    {
        string target = layerName.Trim().ToUpperInvariant();
        bool matched = false;

        foreach (var layer in layers)
        {
            if (layer.layerName.ToUpperInvariant().Contains(target) || target.Contains(layer.layerName.ToUpperInvariant()) || target == "ALL")
            {
                layer.isVisible = visible;
                matched = true;

                foreach (var go in layer.layerObjects)
                {
                    if (go != null) go.SetActive(visible);
                }

                foreach (var rend in layer.layerRenderers)
                {
                    if (rend != null) rend.enabled = visible;
                }
            }
        }

        if (matched)
        {
            Debug.Log($"<color=#00FFB4>[OrganStateManager]</color> Layer '{layerName}' visibility set to: {visible}");
        }
        else
        {
            ToggleChildRenderersByName(layerName, visible);
        }
    }

    private void ToggleChildRenderersByName(string keyword, bool visible)
    {
        string k = keyword.ToLowerInvariant();
        int count = 0;

        foreach (Renderer r in allCachedRenderers)
        {
            if (r != null && r.gameObject.name.ToLowerInvariant().Contains(k))
            {
                r.enabled = visible;
                count++;
            }
        }

        Debug.Log($"<color=#00FFB4>[OrganStateManager]</color> Fallback: Toggled {count} renderers matching '{keyword}' to {visible}");
    }

    /// <summary>
    /// Swaps materials and color tints to simulate Healthy vs Diseased / Infarcted tissue states.
    /// </summary>
    public void SetPathologyState(string stateName)
    {
        string state = stateName.Trim().ToUpperInvariant();
        currentPathology = state;

        bool isDiseased = state.Contains("DISEASE") || state.Contains("INFARCT") || state.Contains("ISCHEMI") || state.Contains("PATHOLOGY");

        foreach (Renderer r in allCachedRenderers)
        {
            if (r == null) continue;

            if (isDiseased)
            {
                if (diseasedMaterial != null)
                {
                    Material[] mats = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = diseasedMaterial;
                    r.materials = mats;
                }
                else
                {
                    ApplyColorTint(r, diseasedTissueColor);
                }
            }
            else
            {
                if (originalMaterials.ContainsKey(r) && originalMaterials[r] != null)
                {
                    r.materials = originalMaterials[r];
                }
                else if (healthyMaterial != null)
                {
                    Material[] mats = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = healthyMaterial;
                    r.materials = mats;
                }
                else
                {
                    ApplyColorTint(r, healthyTissueColor);
                }
            }
        }

        Debug.Log($"<color=#00FFB4>[OrganStateManager]</color> Pathology state updated to: <b>{currentPathology}</b> (Diseased = {isDiseased})");
    }

    private void ApplyColorTint(Renderer r, Color color)
    {
        foreach (Material mat in r.materials)
        {
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", color);
            }
        }
    }

    /// <summary>
    /// Restores all layers to visible and all materials to baseline healthy states.
    /// </summary>
    public void ResetOrganState()
    {
        foreach (var layer in layers)
        {
            layer.isVisible = true;
            foreach (var go in layer.layerObjects) if (go != null) go.SetActive(true);
            foreach (var rend in layer.layerRenderers) if (rend != null) rend.enabled = true;
        }

        foreach (Renderer r in allCachedRenderers)
        {
            if (r != null)
            {
                r.enabled = true;
                if (originalMaterials.ContainsKey(r))
                {
                    r.materials = originalMaterials[r];
                }
            }
        }

        currentPathology = "HEALTHY";
        isSectionCutawayActive = false;
        currentTargetStructure = "None (Point to measure)";
        currentMeasurementLabel = "";
        Debug.Log("<color=#00FFB4>[OrganStateManager]</color> Organ layers, materials, and measurements reset.");
    }

    // -------------------------------------------------------------
    // ON-SCREEN SPATIAL MEASUREMENT & RETICLE HUD (Disabled for clean viewport)
    // -------------------------------------------------------------
    void OnGUI()
    {
        return; // Permanently disabled for clean 3D viewport
    }
}
