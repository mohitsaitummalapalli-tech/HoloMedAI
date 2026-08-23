using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HoloMed AI - DICOM 3D Volume & Volumetric Slice Loader
/// File: Assets/DICOMVolumeLoader.cs
/// 
/// Phase 4: Live DICOM Scans & Telepresence
/// 
/// Responsibilities:
/// 1. Receives DICOM volumetric metadata and slice descriptions via UDP.
/// 2. Generates procedural 3D Textures (Texture3D) and volumetric slice cross-sections.
/// 3. Manages Radiological Window Leveling (Window Center WL / Window Width WW in Hounsfield Units).
/// 4. Provides voice & gesture hooks to switch contrast presets (Angiography, Bone, Soft Tissue, Lung).
/// </summary>
public class DICOMVolumeLoader : MonoBehaviour
{
    [System.Serializable]
    public class PatientInfo
    {
        public string patient_name = "Anonymous Patient";
        public string patient_id = "HM-CARDIO-8821";
        public string modality = "CT";
        public string study_description = "Cardiovascular CT Scan";
        public string series_description = "Coronary 3D Angiography";
        public string acquisition_time = "";
    }

    [System.Serializable]
    public class VolumeGeometry
    {
        public int dim_x = 64;
        public int dim_y = 64;
        public int dim_z = 48;
        public int total_voxels = 196608;
        public float spacing_x_mm = 0.75f;
        public float spacing_y_mm = 0.75f;
        public float spacing_z_mm = 1.25f;
    }

    [System.Serializable]
    public class RadiologicalMetrics
    {
        public float window_center = 40f;
        public float window_width = 400f;
        public int min_hounsfield = -950;
        public int max_hounsfield = 880;
        public float mean_hounsfield = 42.5f;
        public float[] slice_density_profile;
    }

    [System.Serializable]
    public class DICOMPayload
    {
        public string command;
        public string type;
        public PatientInfo patient_info;
        public VolumeGeometry volume_geometry;
        public RadiologicalMetrics radiological_metrics;
        public string preset;
        public float window_center;
        public float window_width;
    }

    [Header("Active DICOM Scan Metadata")]
    public PatientInfo activePatient = new PatientInfo();
    public VolumeGeometry activeGeometry = new VolumeGeometry();
    public RadiologicalMetrics activeMetrics = new RadiologicalMetrics();

    [Header("Radiological Window Leveling (HU)")]
    [Tooltip("Window Center (Brightness / Density mid-point in Hounsfield Units)")]
    [Range(-1000, 1000)] public float windowCenter = 40f;

    [Tooltip("Window Width (Contrast range in Hounsfield Units)")]
    [Range(1, 2500)] public float windowWidth = 400f;

    [Header("Volume Rendering Components")]
    [Tooltip("Target Volume Renderer or Material displaying the DICOM voxel volume")]
    public Renderer volumeRenderer;

    [Tooltip("Procedural 3D Texture holding reconstructed volumetric density data")]
    public Texture3D generatedVolumeTexture;

    [Header("HUD Display Options")]
    public bool showDICOMHUD = true;

    // Events
    public static event Action<DICOMPayload> OnDICOMScanLoaded;
    public static event Action<float, float> OnWindowLevelChanged;

    private Material volumeMaterial;
    private bool isScanLoaded = false;
    private float lastAdjustmentTime = -10f;

    void Awake()
    {
        if (volumeRenderer == null)
        {
            volumeRenderer = GetComponent<Renderer>();
        }

        if (volumeRenderer != null)
        {
            volumeMaterial = volumeRenderer.material;
        }

        GenerateProcedural3DTexture(activeGeometry.dim_x, activeGeometry.dim_y, activeGeometry.dim_z);
    }

    /// <summary>
    /// Loads incoming DICOM Series metadata payload from UDP.
    /// </summary>
    public void LoadDICOMSeries(DICOMPayload payload)
    {
        if (payload == null) return;

        if (payload.patient_info != null) activePatient = payload.patient_info;
        if (payload.volume_geometry != null) activeGeometry = payload.volume_geometry;
        if (payload.radiological_metrics != null)
        {
            activeMetrics = payload.radiological_metrics;
            windowCenter = payload.radiological_metrics.window_center;
            windowWidth = payload.radiological_metrics.window_width;
        }

        isScanLoaded = true;
        lastAdjustmentTime = Time.time;

        // Reconstruct or update 3D texture
        GenerateProcedural3DTexture(activeGeometry.dim_x, activeGeometry.dim_y, activeGeometry.dim_z);
        ApplyWindowLevelToShader();

        OnDICOMScanLoaded?.Invoke(payload);
        Debug.Log($"<color=#00E5FF><b>[DICOM Loader]</b></color> Loaded Scan: {activePatient.patient_id} ({activePatient.modality}) | Dim: {activeGeometry.dim_x}x{activeGeometry.dim_y}x{activeGeometry.dim_z} | WL: {windowCenter} WW: {windowWidth} HU");
    }

    /// <summary>
    /// Adjusts Window Center (WL) and Window Width (WW) dynamically.
    /// </summary>
    public void SetWindowLevel(float center, float width)
    {
        windowCenter = center;
        windowWidth = Mathf.Max(1f, width);
        lastAdjustmentTime = Time.time;

        ApplyWindowLevelToShader();
        OnWindowLevelChanged?.Invoke(windowCenter, windowWidth);
        Debug.Log($"<color=#0FFB4>[DICOM Window Level]</color> WL: {windowCenter:F0} HU, WW: {windowWidth:F0} HU");
    }

    /// <summary>
    /// Applies named clinical window presets.
    /// </summary>
    public void ApplyPreset(string presetName)
    {
        string p = presetName.Trim().ToUpperInvariant();
        if (p.Contains("ANGIO") || p.Contains("CORONARY") || p.Contains("CONTRAST"))
        {
            SetWindowLevel(160f, 600f);
        }
        else if (p.Contains("BONE") || p.Contains("CHEST") || p.Contains("RIBS"))
        {
            SetWindowLevel(400f, 1800f);
        }
        else if (p.Contains("LUNG") || p.Contains("AIRWAY"))
        {
            SetWindowLevel(-600f, 1500f);
        }
        else if (p.Contains("BRAIN") || p.Contains("NEURO"))
        {
            SetWindowLevel(40f, 80f);
        }
        else
        {
            SetWindowLevel(40f, 400f); // Default Cardiac Soft Tissue
        }
    }

    /// <summary>
    /// Generates a procedural 3D texture representing the reconstructed density field.
    /// </summary>
    private void GenerateProcedural3DTexture(int width, int height, int depth)
    {
        width = Mathf.Clamp(width, 16, 128);
        height = Mathf.Clamp(height, 16, 128);
        depth = Mathf.Clamp(depth, 16, 128);

        generatedVolumeTexture = new Texture3D(width, height, depth, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "HoloMed_Reconstructed_DICOM_Volume3D"
        };

        Color[] colorArray = new Color[width * height * depth];
        int idx = 0;

        for (int z = 0; z < depth; z++)
        {
            float nz = (z / (float)depth) * 2f - 1f;
            for (int y = 0; y < height; y++)
            {
                float ny = (y / (float)height) * 2f - 1f;
                for (int x = 0; x < width; x++)
                {
                    float nx = (x / (float)width) * 2f - 1f;

                    // Simulated cardiac density field
                    float dist = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    float density = Mathf.Clamp01(1.0f - dist * 1.2f);

                    // Map density to voxel color
                    float r = Mathf.SmoothStep(0.2f, 0.9f, density);
                    float g = Mathf.SmoothStep(0.1f, 0.4f, density);
                    float b = Mathf.SmoothStep(0.1f, 0.3f, density);
                    float alpha = density > 0.15f ? density * 0.8f : 0f;

                    colorArray[idx++] = new Color(r, g, b, alpha);
                }
            }
        }

        generatedVolumeTexture.SetPixels(colorArray);
        generatedVolumeTexture.Apply();

        if (volumeMaterial != null)
        {
            if (volumeMaterial.HasProperty("_VolumeTex")) volumeMaterial.SetTexture("_VolumeTex", generatedVolumeTexture);
            if (volumeMaterial.HasProperty("_MainTex")) volumeMaterial.SetTexture("_MainTex", generatedVolumeTexture);
        }
    }

    private void ApplyWindowLevelToShader()
    {
        if (volumeMaterial == null && volumeRenderer != null)
        {
            volumeMaterial = volumeRenderer.material;
        }

        if (volumeMaterial != null)
        {
            if (volumeMaterial.HasProperty("_WindowCenter")) volumeMaterial.SetFloat("_WindowCenter", windowCenter);
            if (volumeMaterial.HasProperty("_WindowWidth")) volumeMaterial.SetFloat("_WindowWidth", windowWidth);
            if (volumeMaterial.HasProperty("_MinHU")) volumeMaterial.SetFloat("_MinHU", activeMetrics.min_hounsfield);
            if (volumeMaterial.HasProperty("_MaxHU")) volumeMaterial.SetFloat("_MaxHU", activeMetrics.max_hounsfield);
        }
    }

    // -------------------------------------------------------------
    // ON-SCREEN RADIOLOGICAL DICOM HUD (Disabled for clean viewport)
    // -------------------------------------------------------------
    void OnGUI()
    {
        return; // Permanently disabled for clean 3D viewport
    }
}
