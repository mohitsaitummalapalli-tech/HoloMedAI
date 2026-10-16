using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Phase 3: Virtual Slicing & Dissection Controller
    /// File: Assets/Scripts/VirtualSlicingController.cs
    /// 
    /// Features:
    /// - Interactive multi-planar cross-section dissection (Coronal, Sagittal, Axial, Custom).
    /// - Continuous depth tracking driven by single-hand pinch depth stream from MediaPipe vision.
    /// - Real-time calculation and global/material transmission of slicing plane equation to Custom/CrossSectionHeart shader.
    /// - Seamlessly synchronizes with Phase 2 ExplodedViewController procedural sub-meshes.
    /// - Coordinates surgical trauma and incision feedback with VitalsSimulationManager.
    /// - Provides holographic cutting laser and plane gizmo orientation.
    /// - Direct API for UDP receivers on port 5052 and Ultron AI surgical commands.
    /// - Keyboard debugging hotkeys (S: Step slice, C: Cycle orientation, I: Invert, R: Reset).
    /// </summary>
    [DisallowMultipleComponent]
    public class VirtualSlicingController : MonoBehaviour
    {
        public static VirtualSlicingController Instance { get; private set; }

        public enum SliceOrientation
        {
            Coronal,   // Frontal cut: Normal along Z (anterior-posterior)
            Sagittal,  // Lateral cut: Normal along X (left-right)
            Axial,     // Transverse cut: Normal along Y (superior-inferior)
            Custom     // Arbitrary angle
        }

        [Header("Target Anatomical Model")]
        [Tooltip("Assign the root organ Transform (e.g. Heart). Defaults to this transform.")]
        [SerializeField] private Transform targetModel;

        public Transform TargetModel
        {
            get => targetModel != null ? targetModel : transform;
            set => targetModel = value;
        }

        [Header("Organ & Slicer References")]
        [Tooltip("Assign the OrganSlicer component. Defaults to searching on this GameObject or Heart.")]
        [SerializeField] private OrganSlicer organSlicer;

        [Tooltip("Transform representing the mathematical cutting plane (SlicePlane).")]
        [SerializeField] private Transform slicePlaneTransform;

        [Tooltip("Target heart material using the Custom/CrossSectionHeart shader.")]
        [SerializeField] private Material crossSectionMaterial;

        [Header("Surgical Trauma & Vitals")]
        [Tooltip("Vitals Manager for patient physiological response during cardiac incision.")]
        [SerializeField] private VitalsSimulationManager vitalsManager;

        [Header("Slicing Calibration")]
        [Tooltip("Current anatomical cutting plane orientation.")]
        [SerializeField] private SliceOrientation orientation = SliceOrientation.Coronal;

        [Tooltip("Speed multiplier for pinch depth deltas.")]
        [Range(0.5f, 25f)]
        [SerializeField] private float depthSensitivity = 8.0f;

        [Tooltip("Minimum allowable plane position along active cutting axis.")]
        [SerializeField] private float minDepth = -1.5f;

        [Tooltip("Maximum allowable plane position along active cutting axis.")]
        [SerializeField] private float maxDepth = 1.5f;

        [Tooltip("Smooth interpolation speed for plane depth motion.")]
        [Range(1f, 30f)]
        [SerializeField] private float smoothingSpeed = 12.0f;

        [Header("Visual Feedback & Shader Parameters")]
        [Tooltip("Color of the surgical incision cutline glow.")]
        [SerializeField] private Color cutEdgeGlowColor = Color.cyan;

        [Tooltip("Width of the illuminated cut edge on the shader.")]
        [Range(0.001f, 0.05f)]
        [SerializeField] private float cutEdgeWidth = 0.005f;

        [Tooltip("Solid tissue color for the cut interior.")]
        [SerializeField] private Color interiorMuscleColor = new Color(0.55f, 0.05f, 0.08f, 1.0f);

        [Header("Events")]
        [SerializeField] private UnityEvent<float> onDepthChanged = new UnityEvent<float>();
        [SerializeField] private UnityEvent<bool> onSliceToggled = new UnityEvent<bool>();

        // C# Action delegates
        public static event Action<float, float> OnSliceDepthUpdated;
        public static event Action<bool> OnSliceCommitted;
        public static event Action<SliceOrientation> OnOrientationChanged;

        private float currentDepth = -1.5f;
        private float targetDepth = -1.5f;
        private bool isCuttingActive = false;
        private bool isInverted = false;
        private Vector3 initialPlanePosition = new Vector3(0f, -0.5f, -1.5f);
        private Quaternion initialPlaneRotation = Quaternion.identity;

        // Cached list of child renderers (including procedural segmented parts)
        private readonly List<Renderer> cachedRenderers = new List<Renderer>();

        public SliceOrientation CurrentOrientation => orientation;
        public float NormalizedDepth => Mathf.InverseLerp(minDepth, maxDepth, currentDepth);
        public bool IsCuttingActive => isCuttingActive;
        public bool IsInverted => isInverted;
        public float CurrentDepth => currentDepth;
        public float TargetDepth => targetDepth;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            ResolveReferences();
        }

        private void OnEnable()
        {
            // Subscribe to UDP receiver broadcast as fail-safe listener
            UDPReceiver.OnDataReceived += HandleUdpPacket;
        }

        private void OnDisable()
        {
            UDPReceiver.OnDataReceived -= HandleUdpPacket;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Start()
        {
            ResolveReferences();
            ApplyOrientation(orientation);
            UpdateShaderPlane();
        }

        /// <summary>
        /// Proactively auto-detects and links all necessary scene references.
        /// </summary>
        public void ResolveReferences()
        {
            if (targetModel == null)
            {
                GameObject heartObj = GameObject.Find("Heart") ?? GameObject.Find("Human Heart");
                targetModel = heartObj != null ? heartObj.transform : transform;
            }

            if (organSlicer == null)
            {
                organSlicer = GetComponent<OrganSlicer>() ?? FindAnyObjectByType<OrganSlicer>();
            }

            if (slicePlaneTransform == null)
            {
                if (organSlicer != null && organSlicer.slicePlaneTransform != null)
                {
                    slicePlaneTransform = organSlicer.slicePlaneTransform;
                }
                else
                {
                    GameObject planeObj = GameObject.Find("SlicePlane");
                    if (planeObj != null)
                    {
                        slicePlaneTransform = planeObj.transform;
                    }
                    else
                    {
                        // Auto-create SlicePlane GameObject if missing
                        GameObject newPlane = new GameObject("SlicePlane");
                        newPlane.transform.position = initialPlanePosition;
                        newPlane.transform.rotation = initialPlaneRotation;
                        slicePlaneTransform = newPlane.transform;
                        Debug.Log("[VirtualSlicingController] Created default SlicePlane GameObject.");
                    }
                }
            }

            if (slicePlaneTransform != null)
            {
                initialPlanePosition = slicePlaneTransform.position;
                initialPlaneRotation = slicePlaneTransform.rotation;
                currentDepth = slicePlaneTransform.position.z;
                targetDepth = currentDepth;
            }

            if (vitalsManager == null)
            {
                vitalsManager = FindAnyObjectByType<VitalsSimulationManager>();
            }

            if (crossSectionMaterial == null)
            {
                if (organSlicer != null && organSlicer.targetMaterial != null)
                {
                    crossSectionMaterial = organSlicer.targetMaterial;
                }
                else if (targetModel != null)
                {
                    MeshRenderer mr = targetModel.GetComponentInChildren<MeshRenderer>(true);
                    if (mr != null)
                    {
                        crossSectionMaterial = mr.sharedMaterial;
                    }
                }
            }

            RefreshChildRenderers();
        }

        /// <summary>
        /// Refreshes the cached list of organ and procedural sub-mesh renderers.
        /// </summary>
        public void RefreshChildRenderers()
        {
            cachedRenderers.Clear();
            Transform root = TargetModel;
            if (root != null)
            {
                Renderer[] rends = root.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rends)
                {
                    if (r != null && !r.name.StartsWith("SliceHandle"))
                    {
                        cachedRenderers.Add(r);
                    }
                }
            }
        }

        private void Update()
        {
            // Keyboard shortcuts for rapid testing in Unity Editor
            HandleKeyboardHotkeys();

            if (slicePlaneTransform == null) return;

            // Smoothly interpolate current depth towards target depth
            if (Mathf.Abs(currentDepth - targetDepth) > 0.0001f)
            {
                currentDepth = Mathf.Lerp(currentDepth, targetDepth, Time.deltaTime * smoothingSpeed);
                if (Mathf.Abs(currentDepth - targetDepth) < 0.0005f)
                {
                    currentDepth = targetDepth;
                }

                ApplyDepthToTransform(currentDepth);
                UpdateShaderPlane();

                float norm = NormalizedDepth;
                onDepthChanged?.Invoke(norm);
                OnSliceDepthUpdated?.Invoke(norm, currentDepth);
            }
        }

        private void HandleKeyboardHotkeys()
        {
            // S: Step slicing plane into the organ
            if (Input.GetKey(KeyCode.S))
            {
                ApplyDepthDelta(0.015f);
            }

            // C: Cycle slicing orientation (Coronal -> Sagittal -> Axial)
            if (Input.GetKeyDown(KeyCode.C))
            {
                CycleOrientation();
            }

            // I: Invert cutting plane normal
            if (Input.GetKeyDown(KeyCode.I))
            {
                InvertSliceNormal();
            }

            // R: Reset slicing plane to standby
            if (Input.GetKeyDown(KeyCode.R) && !Input.GetKey(KeyCode.LeftControl))
            {
                ResetSlicing();
            }

            // Return / Space: Commit incision
            if (Input.GetKeyDown(KeyCode.Return))
            {
                CommitIncision();
            }
        }

        /// <summary>
        /// Updates slicing depth based on continuous delta stream from single-hand pinch depth tracking.
        /// </summary>
        /// <param name="delta">Depth change delta from perception</param>
        public void ApplyDepthDelta(float delta)
        {
            if (float.IsNaN(delta) || float.IsInfinity(delta)) return;

            targetDepth = Mathf.Clamp(targetDepth + (delta * depthSensitivity), minDepth, maxDepth);
            isCuttingActive = true;
        }

        /// <summary>
        /// Directly sets the target cutting depth in world units.
        /// </summary>
        public void SetDepth(float depth)
        {
            if (float.IsNaN(depth) || float.IsInfinity(depth)) return;

            targetDepth = Mathf.Clamp(depth, minDepth, maxDepth);
            isCuttingActive = true;
        }

        /// <summary>
        /// Sets absolute normalized depth [0.0 = standby/outside, 1.0 = deep cut].
        /// </summary>
        public void SetNormalizedDepth(float normalized01)
        {
            float norm = Mathf.Clamp01(normalized01);
            targetDepth = Mathf.Lerp(minDepth, maxDepth, norm);
            isCuttingActive = true;
        }

        /// <summary>
        /// Cycles through standard multi-planar orientations: Coronal -> Sagittal -> Axial -> Coronal.
        /// </summary>
        public void CycleOrientation()
        {
            SliceOrientation next = orientation switch
            {
                SliceOrientation.Coronal => SliceOrientation.Sagittal,
                SliceOrientation.Sagittal => SliceOrientation.Axial,
                SliceOrientation.Axial => SliceOrientation.Coronal,
                _ => SliceOrientation.Coronal
            };
            SetOrientation(next);
        }

        /// <summary>
        /// Sets the anatomical orientation of the cutting plane.
        /// </summary>
        public void SetOrientation(SliceOrientation newOrientation)
        {
            orientation = newOrientation;
            ApplyOrientation(orientation);
            OnOrientationChanged?.Invoke(orientation);
            Debug.Log($"<color=#00FFAA><b>[VirtualSlicingController]</b></color> Cutting plane orientation set to: {orientation}");
        }

        /// <summary>
        /// Sets orientation via string identifier (e.g. from UDP/voice commands).
        /// </summary>
        public void SetOrientationByName(string orientationName)
        {
            if (string.IsNullOrEmpty(orientationName)) return;

            string upper = orientationName.Trim().ToUpperInvariant();
            if (upper.Contains("CORONAL") || upper.Contains("FRONTAL"))
            {
                SetOrientation(SliceOrientation.Coronal);
            }
            else if (upper.Contains("SAGITTAL") || upper.Contains("LATERAL"))
            {
                SetOrientation(SliceOrientation.Sagittal);
            }
            else if (upper.Contains("AXIAL") || upper.Contains("TRANSVERSE") || upper.Contains("HORIZONTAL"))
            {
                SetOrientation(SliceOrientation.Axial);
            }
        }

        private void ApplyOrientation(SliceOrientation orient)
        {
            if (slicePlaneTransform == null) return;

            Vector3 euler = Vector3.zero;
            switch (orient)
            {
                case SliceOrientation.Coronal:   // Normal points forward (Z axis)
                    euler = new Vector3(0f, 0f, 0f);
                    break;
                case SliceOrientation.Sagittal:  // Normal points lateral (X axis)
                    euler = new Vector3(0f, 90f, 0f);
                    break;
                case SliceOrientation.Axial:     // Normal points vertical (Y axis)
                    euler = new Vector3(90f, 0f, 0f);
                    break;
                case SliceOrientation.Custom:
                    break;
            }

            if (isInverted) euler += new Vector3(180f, 0f, 0f);
            slicePlaneTransform.rotation = Quaternion.Euler(euler);
            UpdateShaderPlane();
        }

        /// <summary>
        /// Translates the slice plane along its active cutting axis.
        /// </summary>
        private void ApplyDepthToTransform(float depth)
        {
            if (slicePlaneTransform == null) return;

            Vector3 pos = slicePlaneTransform.position;
            switch (orientation)
            {
                case SliceOrientation.Coronal:
                    pos.z = depth;
                    break;
                case SliceOrientation.Sagittal:
                    pos.x = depth;
                    break;
                case SliceOrientation.Axial:
                    pos.y = depth;
                    break;
                case SliceOrientation.Custom:
                    pos = slicePlaneTransform.forward * depth;
                    break;
            }
            slicePlaneTransform.position = pos;
        }

        /// <summary>
        /// Computes the 4D plane equation and transmits it both globally and to target materials:
        /// Plane Equation: dot(worldPos, normal) - distance = 0
        /// </summary>
        public void UpdateShaderPlane()
        {
            if (slicePlaneTransform == null) return;

            Vector3 normal = slicePlaneTransform.forward.normalized;
            if (isInverted) normal = -normal;

            float distance = Vector3.Dot(normal, slicePlaneTransform.position);
            Vector4 planeVector = new Vector4(normal.x, normal.y, normal.z, distance);

            // 1. Broadcast globally to all shaders listening to _SlicePlane
            Shader.SetGlobalVector("_SlicePlane", planeVector);
            Shader.SetGlobalColor("_CutEdgeColor", cutEdgeGlowColor);
            Shader.SetGlobalFloat("_CutEdgeWidth", cutEdgeWidth);
            Shader.SetGlobalColor("_InteriorColor", interiorMuscleColor);

            // 2. Set on primary cross-section material if assigned
            if (crossSectionMaterial != null)
            {
                crossSectionMaterial.SetVector("_SlicePlane", planeVector);
                crossSectionMaterial.SetColor("_CutEdgeColor", cutEdgeGlowColor);
                crossSectionMaterial.SetFloat("_CutEdgeWidth", cutEdgeWidth);
                crossSectionMaterial.SetColor("_InteriorColor", interiorMuscleColor);
            }

            // 3. Set on all child renderers (including ExplodedParts_Procedural sub-meshes)
            if (cachedRenderers.Count == 0) RefreshChildRenderers();
            for (int i = 0; i < cachedRenderers.Count; i++)
            {
                Renderer rend = cachedRenderers[i];
                if (rend != null && rend.sharedMaterial != null)
                {
                    if (rend.sharedMaterial.HasProperty("_SlicePlane"))
                    {
                        rend.sharedMaterial.SetVector("_SlicePlane", planeVector);
                        rend.sharedMaterial.SetColor("_CutEdgeColor", cutEdgeGlowColor);
                        rend.sharedMaterial.SetFloat("_CutEdgeWidth", cutEdgeWidth);
                        rend.sharedMaterial.SetColor("_InteriorColor", interiorMuscleColor);
                    }
                }
            }

            if (organSlicer != null)
            {
                organSlicer.UpdateSlicePlaneShader();
            }
        }

        /// <summary>
        /// Inverts cutting normal to view opposite anatomical hemisphere.
        /// </summary>
        public void InvertSliceNormal()
        {
            isInverted = !isInverted;
            ApplyOrientation(orientation);
            Debug.Log($"<color=#00FFAA><b>[VirtualSlicingController]</b></color> Cutting normal inverted: {isInverted}");
        }

        /// <summary>
        /// Commits the active surgical incision and triggers physiological trauma response.
        /// </summary>
        public void CommitIncision()
        {
            isCuttingActive = true;

            if (organSlicer != null)
            {
                organSlicer.ExecuteSlice();
            }

            if (vitalsManager != null)
            {
                vitalsManager.TriggerTraumaSpike("Virtual Cardiac Planar Dissection", 1.30f);
            }

            onSliceToggled?.Invoke(true);
            OnSliceCommitted?.Invoke(true);
            Debug.Log("<color=#FF3366><b>[VirtualSlicingController]</b></color> Surgical Incision Committed -> Patient Vitals Trauma Triggered!");
        }

        /// <summary>
        /// Toggles between standby outside position and active cross-section dissection.
        /// </summary>
        public void ToggleSlice()
        {
            if (isCuttingActive)
            {
                ResetSlicing();
            }
            else
            {
                targetDepth = 0f;
                isCuttingActive = true;
                CommitIncision();
            }
        }

        /// <summary>
        /// Resets the slicing plane back to its starting standby position outside the organ.
        /// </summary>
        public void ResetSlicing()
        {
            targetDepth = minDepth;
            currentDepth = minDepth;
            isCuttingActive = false;
            isInverted = false;

            if (slicePlaneTransform != null)
            {
                slicePlaneTransform.position = initialPlanePosition;
                slicePlaneTransform.rotation = initialPlaneRotation;
            }

            if (organSlicer != null)
            {
                organSlicer.ResetSlice();
            }

            ApplyOrientation(SliceOrientation.Coronal);
            UpdateShaderPlane();

            onDepthChanged?.Invoke(0f);
            onSliceToggled?.Invoke(false);
            OnSliceCommitted?.Invoke(false);
            Debug.Log("<color=#00FFAA><b>[VirtualSlicingController]</b></color> Slicing plane reset to standby.");
        }

        /// <summary>
        /// Fall-back direct UDP packet listener for port 5052 packets.
        /// </summary>
        private void HandleUdpPacket(string rawText)
        {
            if (string.IsNullOrEmpty(rawText) || !rawText.TrimStart().StartsWith("{")) return;

            try
            {
                string upper = rawText.ToUpperInvariant();

                // Check for Slicing / Dissection command tokens
                if (upper.Contains("\"COMMAND\":\"SLICE\"") ||
                    upper.Contains("\"COMMAND\":\"SLICING\"") ||
                    upper.Contains("\"ACTION\":\"SLICE\"") ||
                    upper.Contains("\"GESTURE\":\"SLICING\""))
                {
                    float delta = ExtractFloat(rawText, "\"value\":") ?? ExtractFloat(rawText, "\"dz\":") ?? ExtractFloat(rawText, "\"delta\":") ?? 0f;
                    if (Mathf.Abs(delta) > 0.0001f)
                    {
                        ApplyDepthDelta(delta);
                    }
                }
                else if (upper.Contains("SLICE_CORONAL"))
                {
                    SetOrientation(SliceOrientation.Coronal);
                }
                else if (upper.Contains("SLICE_SAGITTAL"))
                {
                    SetOrientation(SliceOrientation.Sagittal);
                }
                else if (upper.Contains("SLICE_AXIAL"))
                {
                    SetOrientation(SliceOrientation.Axial);
                }
                else if (upper.Contains("INVERT_SLICE"))
                {
                    InvertSliceNormal();
                }
                else if (upper.Contains("RESET_SLICE"))
                {
                    ResetSlicing();
                }
            }
            catch { }
        }

        private float? ExtractFloat(string json, string key)
        {
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = idx + key.Length;
                int end = json.IndexOfAny(new char[] { ',', '}', ' ' }, start);
                if (end > start)
                {
                    if (float.TryParse(json.Substring(start, end - start).Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float result))
                    {
                        return result;
                    }
                }
            }
            return null;
        }
    }
}
