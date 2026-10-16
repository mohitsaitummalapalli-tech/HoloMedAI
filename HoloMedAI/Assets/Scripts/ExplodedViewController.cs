using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Phase 2: Exploded View Controller (Option A: Procedural Bounding-Box Segmentation)
    /// File: Assets/Scripts/ExplodedViewController.cs
    /// 
    /// Features:
    /// - Procedurally partitions a single-mesh anatomical organ (e.g. Human Heart OBJ) into 5 logical
    ///   anatomical zones using 3D bounding-box spatial geometry:
    ///     1. Aorta & Great Vessels (Superior Zone: Top 38% of mesh height)
    ///     2. Left Ventricle & Apex (Inferior-Left Zone: Bottom 38% height, Left of septum)
    ///     3. Right Ventricle (Inferior-Right Zone: Bottom 38% height, Right of septum)
    ///     4. Left Atrium & Pulmonary Inflow (Mid-Left Zone: 38% - 62% height, Left of septum)
    ///     5. Right Atrium & Vena Cava (Mid-Right Zone: 38% - 62% height, Right of septum)
    /// - Smooth continuous interpolation (Lerp / SmoothDamp) between assembled (0.0) and exploded (1.0).
    /// - Zero visual seams when assembled (original intact mesh active when progress <= 0.001).
    /// - Layer isolation mode (isolates a specific chamber while expanding or ghosting others).
    /// - Accurate 1:1 return to baseline on reset.
    /// - Full integration with UDP receiver (port 5052) and Ultron AI voice commands.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExplodedViewController : MonoBehaviour
    {
        public static ExplodedViewController Instance { get; private set; }

        [System.Serializable]
        public class AnatomicalLayer
        {
            [Tooltip("Identifiable name of the anatomical layer or chamber.")]
            public string layerName;

            [Tooltip("Transform of the anatomical component.")]
            public Transform layerTransform;

            [Tooltip("Vector along which this layer separates from the organ center.")]
            public Vector3 explosionDirection = Vector3.forward;

            [Tooltip("Relative distance multiplier for this layer.")]
            [Range(0.2f, 3.0f)]
            public float distanceMultiplier = 1.0f;

            [HideInInspector] public Vector3 initialLocalPos;
            [HideInInspector] public Quaternion initialLocalRot;
            [HideInInspector] public bool isIsolated = false;
        }

        [Header("Target Anatomical Model")]
        [Tooltip("Assign the root organ Transform (e.g. Heart). Defaults to this transform.")]
        [SerializeField] private Transform targetModel;

        public Transform TargetModel
        {
            get => targetModel != null ? targetModel : transform;
            set => targetModel = value;
        }

        [Header("Procedural Bounding-Box Segmentation (Option A)")]
        [Tooltip("Automatically segment single-mesh heart into anatomical sub-structures using spatial bounds.")]
        [SerializeField] private bool useProceduralSegmentation = true;

        [Tooltip("Superior split ratio for Aorta & Great Vessels (top boundary) [0.5 - 0.75].")]
        [Range(0.50f, 0.75f)]
        [SerializeField] private float superiorSplitRatio = 0.62f;

        [Tooltip("Inferior split ratio for Ventricles vs Atria (bottom boundary) [0.25 - 0.50].")]
        [Range(0.25f, 0.50f)]
        [SerializeField] private float inferiorSplitRatio = 0.38f;

        [Tooltip("Horizontal septum split offset along local X axis [-0.1 to 0.1].")]
        [Range(-0.1f, 0.1f)]
        [SerializeField] private float septumSplitOffset = 0.0f;

        [Header("Explosion Motion Parameters")]
        [Tooltip("Maximum displacement distance (in Unity units) at 100% explosion.")]
        [Range(0.1f, 2.5f)]
        [SerializeField] private float maxExplosionDistance = 0.65f;

        [Tooltip("Speed of transition interpolation.")]
        [Range(1f, 20f)]
        [SerializeField] private float transitionSpeed = 6.0f;

        [Tooltip("Current explosion factor [0.0 = Assembled, 1.0 = Fully Exploded].")]
        [Range(0f, 1f)]
        [SerializeField] private float explosionProgress = 0f;

        [Header("Anatomical Layers")]
        [Tooltip("Anatomical layers (auto-generated via procedural segmentation or manual inspector assignment).")]
        [SerializeField] private List<AnatomicalLayer> anatomicalLayers = new List<AnatomicalLayer>();

        [Header("Unity Events")]
        [SerializeField] private UnityEvent<float> onProgressChanged = new UnityEvent<float>();
        [SerializeField] private UnityEvent<bool> onStateToggled = new UnityEvent<bool>();

        // C# Action delegates
        public static event Action<float> OnExplosionProgressChanged;
        public static event Action<bool> OnExplosionStateChanged;
        public static event Action<string> OnLayerIsolated;

        private float targetProgress = 0f;
        private bool isExploded = false;
        private bool isInitialized = false;

        // References for procedural seamless mesh swapping
        private MeshRenderer originalMeshRenderer;
        private GameObject proceduralContainer;

        public float CurrentProgress => explosionProgress;
        public bool IsExploded => isExploded;
        public List<AnatomicalLayer> Layers => anatomicalLayers;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }

            if (targetModel == null)
            {
                GameObject heartObj = GameObject.Find("Heart");
                targetModel = heartObj != null ? heartObj.transform : transform;
            }

            InitializeLayers();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Initializes anatomical layer baseline transforms and auto-detects child meshes or segments a single mesh.
        /// </summary>
        public void InitializeLayers()
        {
            Transform root = TargetModel;
            if (root == null) return;

            // Cache original MeshRenderer
            originalMeshRenderer = root.GetComponentInChildren<MeshRenderer>();

            // Check if user already provided multi-part layers
            if (anatomicalLayers != null && anatomicalLayers.Count > 1)
            {
                CacheLayerBaselines(root);
                isInitialized = true;
                return;
            }

            // Check if the heart has pre-existing child parts
            int childCount = 0;
            foreach (Transform child in root)
            {
                if (child.name != "ExplodedParts_Procedural" && !child.name.StartsWith("SlicePlane"))
                {
                    childCount++;
                }
            }

            if (childCount > 1 && !useProceduralSegmentation)
            {
                // Multi-mesh asset: discover child layers
                anatomicalLayers = new List<AnatomicalLayer>();
                DiscoverChildLayers(root);
                CacheLayerBaselines(root);
                isInitialized = true;
            }
            else if (useProceduralSegmentation)
            {
                // Option A: Single-mesh asset -> Procedural Bounding-Box Segmentation
                PerformProceduralSegmentation(root);
                isInitialized = true;
            }
        }

        /// <summary>
        /// Procedurally partitions the single heart mesh into 5 anatomical zones using 3D bounding box coordinates.
        /// </summary>
        public void PerformProceduralSegmentation(Transform root)
        {
            MeshFilter mf = root.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Debug.LogWarning("[ExplodedViewController] No MeshFilter found on target model for procedural segmentation.");
                return;
            }

            Mesh src = mf.sharedMesh;
            if (!src.isReadable)
            {
                Debug.LogWarning($"[ExplodedViewController] Mesh '{src.name}' is marked non-readable in ModelImporter. Fallback active.");
                return;
            }

            // Clean up any existing procedural container from previous runs
            Transform existingContainer = mf.transform.Find("ExplodedParts_Procedural");
            if (existingContainer != null)
            {
                DestroyImmediate(existingContainer.gameObject);
            }

            Bounds b = src.bounds;
            float yMin = b.min.y;
            float ySize = b.size.y;
            float xCenter = b.center.x + septumSplitOffset;
            float ySuperior = yMin + (ySize * superiorSplitRatio);
            float yInferior = yMin + (ySize * inferiorSplitRatio);

            string[] zoneNames = new string[]
            {
                "Heart_Aorta_and_Great_Vessels",
                "Heart_Left_Ventricle_and_Apex",
                "Heart_Right_Ventricle",
                "Heart_Left_Atrium",
                "Heart_Right_Atrium"
            };

            string[] displayNames = new string[]
            {
                "Aorta & Great Vessels",
                "Left Ventricle & Apex",
                "Right Ventricle",
                "Left Atrium",
                "Right Atrium"
            };

            Vector3[] directions = new Vector3[]
            {
                new Vector3(0.0f, 1.0f, 0.30f).normalized,    // Aorta: Up & slight forward
                new Vector3(-0.95f, -0.65f, 0.40f).normalized, // Left Ventricle: Down-left anterior
                new Vector3(0.95f, -0.65f, 0.40f).normalized,  // Right Ventricle: Down-right anterior
                new Vector3(-0.85f, 0.50f, -0.65f).normalized, // Left Atrium: Up-left posterior
                new Vector3(0.85f, 0.50f, -0.65f).normalized   // Right Atrium: Up-right posterior
            };

            float[] multipliers = new float[] { 1.40f, 1.25f, 1.25f, 1.15f, 1.15f };

            Vector3[] srcVerts = src.vertices;
            Vector3[] srcNorms = src.normals;
            Vector2[] srcUVs = src.uv;
            int[] srcTris = src.triangles;

            int numZones = zoneNames.Length;
            List<Vector3>[] zoneVerts = new List<Vector3>[numZones];
            List<Vector3>[] zoneNorms = new List<Vector3>[numZones];
            List<Vector2>[] zoneUVs = new List<Vector2>[numZones];
            List<int>[] zoneTris = new List<int>[numZones];
            Dictionary<int, int>[] remap = new Dictionary<int, int>[numZones];

            for (int z = 0; z < numZones; z++)
            {
                zoneVerts[z] = new List<Vector3>();
                zoneNorms[z] = new List<Vector3>();
                zoneUVs[z] = new List<Vector2>();
                zoneTris[z] = new List<int>();
                remap[z] = new Dictionary<int, int>();
            }

            // Single fast pass over all triangles: classify by triangle centroid
            for (int i = 0; i < srcTris.Length; i += 3)
            {
                int i0 = srcTris[i];
                int i1 = srcTris[i + 1];
                int i2 = srcTris[i + 2];

                Vector3 v0 = srcVerts[i0];
                Vector3 v1 = srcVerts[i1];
                Vector3 v2 = srcVerts[i2];

                Vector3 centroid = (v0 + v1 + v2) * (1.0f / 3.0f);
                int zone = 0;

                if (centroid.y >= ySuperior)
                {
                    zone = 0; // Aorta & Great Vessels
                }
                else if (centroid.y < yInferior)
                {
                    zone = (centroid.x <= xCenter) ? 1 : 2; // 1: Left Ventricle, 2: Right Ventricle
                }
                else
                {
                    zone = (centroid.x <= xCenter) ? 3 : 4; // 3: Left Atrium, 4: Right Atrium
                }

                // Append triangle to target zone
                int[] triangleIndices = { i0, i1, i2 };
                for (int k = 0; k < 3; k++)
                {
                    int srcIdx = triangleIndices[k];
                    if (!remap[zone].TryGetValue(srcIdx, out int newIdx))
                    {
                        newIdx = zoneVerts[zone].Count;
                        remap[zone][srcIdx] = newIdx;
                        zoneVerts[zone].Add(srcVerts[srcIdx]);
                        if (srcNorms != null && srcNorms.Length > srcIdx) zoneNorms[zone].Add(srcNorms[srcIdx]);
                        if (srcUVs != null && srcUVs.Length > srcIdx) zoneUVs[zone].Add(srcUVs[srcIdx]);
                    }
                    zoneTris[zone].Add(newIdx);
                }
            }

            // Create procedural container
            proceduralContainer = new GameObject("ExplodedParts_Procedural");
            proceduralContainer.transform.SetParent(mf.transform, false);
            proceduralContainer.transform.localPosition = Vector3.zero;
            proceduralContainer.transform.localRotation = Quaternion.identity;
            proceduralContainer.transform.localScale = Vector3.one;

            anatomicalLayers = new List<AnatomicalLayer>();

            Material sharedHeartMaterial = originalMeshRenderer != null ? originalMeshRenderer.sharedMaterial : null;

            for (int z = 0; z < numZones; z++)
            {
                if (zoneTris[z].Count == 0) continue;

                Mesh subMesh = new Mesh();
                subMesh.name = zoneNames[z];
                subMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                subMesh.SetVertices(zoneVerts[z]);
                if (zoneNorms[z].Count > 0) subMesh.SetNormals(zoneNorms[z]);
                if (zoneUVs[z].Count > 0) subMesh.SetUVs(0, zoneUVs[z]);
                subMesh.SetTriangles(zoneTris[z], 0);
                subMesh.RecalculateBounds();

                GameObject zoneObj = new GameObject(zoneNames[z]);
                zoneObj.transform.SetParent(proceduralContainer.transform, false);
                zoneObj.transform.localPosition = Vector3.zero;
                zoneObj.transform.localRotation = Quaternion.identity;
                zoneObj.transform.localScale = Vector3.one;

                MeshFilter subMf = zoneObj.AddComponent<MeshFilter>();
                subMf.sharedMesh = subMesh;

                MeshRenderer subMr = zoneObj.AddComponent<MeshRenderer>();
                if (sharedHeartMaterial != null)
                {
                    subMr.sharedMaterial = sharedHeartMaterial;
                }

                AnatomicalLayer layer = new AnatomicalLayer
                {
                    layerName = displayNames[z],
                    layerTransform = zoneObj.transform,
                    explosionDirection = directions[z],
                    distanceMultiplier = multipliers[z],
                    initialLocalPos = Vector3.zero,
                    initialLocalRot = Quaternion.identity
                };

                anatomicalLayers.Add(layer);
            }

            // Initially hide procedural parts when assembled (0.0), showing intact original single mesh
            proceduralContainer.SetActive(false);
            if (originalMeshRenderer != null) originalMeshRenderer.enabled = true;

            Debug.Log($"<color=#00FFAA><b>[ExplodedViewController]</b></color> Procedural Bounding-Box Segmentation completed: Created {anatomicalLayers.Count} anatomical sub-parts.");
        }

        private void CacheLayerBaselines(Transform root)
        {
            Vector3 rootWorldPos = root.position;
            foreach (var layer in anatomicalLayers)
            {
                if (layer.layerTransform != null)
                {
                    layer.initialLocalPos = layer.layerTransform.localPosition;
                    layer.initialLocalRot = layer.layerTransform.localRotation;

                    if (layer.explosionDirection == Vector3.zero)
                    {
                        Vector3 outward = (layer.layerTransform.position - rootWorldPos);
                        if (outward.sqrMagnitude < 0.0001f) outward = Vector3.up;
                        layer.explosionDirection = root.InverseTransformDirection(outward.normalized);
                    }
                }
            }
        }

        private void DiscoverChildLayers(Transform parent)
        {
            Renderer[] renderers = parent.GetComponentsInChildren<Renderer>(true);
            Vector3 rootPos = parent.position;

            foreach (var rend in renderers)
            {
                Transform t = rend.transform;
                if (t == parent) continue;

                Vector3 outward = (t.position - rootPos);
                if (outward.sqrMagnitude < 0.001f)
                {
                    outward = (t.localPosition.sqrMagnitude > 0.001f) ? t.localPosition : Vector3.up;
                }

                AnatomicalLayer layer = new AnatomicalLayer
                {
                    layerName = t.name,
                    layerTransform = t,
                    explosionDirection = outward.normalized,
                    distanceMultiplier = 1.0f
                };

                string lower = t.name.ToLowerInvariant();
                if (lower.Contains("aorta") || lower.Contains("artery"))
                {
                    layer.explosionDirection = (Vector3.up + Vector3.forward * 0.5f).normalized;
                    layer.distanceMultiplier = 1.35f;
                }
                else if (lower.Contains("left_ventricle") || lower.Contains("lv"))
                {
                    layer.explosionDirection = (-Vector3.right + -Vector3.up * 0.5f).normalized;
                    layer.distanceMultiplier = 1.25f;
                }
                else if (lower.Contains("right_ventricle") || lower.Contains("rv"))
                {
                    layer.explosionDirection = (Vector3.right + -Vector3.up * 0.5f).normalized;
                    layer.distanceMultiplier = 1.25f;
                }
                else if (lower.Contains("atrium") || lower.Contains("ra") || lower.Contains("la"))
                {
                    layer.explosionDirection = (Vector3.up * 0.8f + (lower.Contains("left") ? -Vector3.right : Vector3.right)).normalized;
                    layer.distanceMultiplier = 1.15f;
                }

                anatomicalLayers.Add(layer);
            }
        }

        private void Update()
        {
            if (!isInitialized) return;

            // Keyboard shortcut for quick interactive testing in Unity Editor
            if (Input.GetKeyDown(KeyCode.X))
            {
                ToggleExplode();
            }

            // Smoothly interpolate explosionProgress towards targetProgress
            if (Mathf.Abs(explosionProgress - targetProgress) > 0.0001f)
            {
                explosionProgress = Mathf.Lerp(explosionProgress, targetProgress, Time.deltaTime * transitionSpeed);
                if (Mathf.Abs(explosionProgress - targetProgress) < 0.001f)
                {
                    explosionProgress = targetProgress;
                }

                ApplyExplosion(explosionProgress);
                onProgressChanged?.Invoke(explosionProgress);
                OnExplosionProgressChanged?.Invoke(explosionProgress);
            }
        }

        /// <summary>
        /// Applies the explosion displacement to all anatomical layers.
        /// Handles seamless swapping between intact single mesh and procedural multi-part view.
        /// </summary>
        public void ApplyExplosion(float factor)
        {
            factor = Mathf.Clamp01(factor);

            if (proceduralContainer != null && originalMeshRenderer != null)
            {
                if (factor <= 0.001f)
                {
                    // Fully assembled: show intact unified mesh for zero seams, hide procedural pieces
                    originalMeshRenderer.enabled = true;
                    proceduralContainer.SetActive(false);
                }
                else
                {
                    // Exploded: hide unified mesh, show and tween separated anatomical parts
                    originalMeshRenderer.enabled = false;
                    proceduralContainer.SetActive(true);
                }
            }

            foreach (var layer in anatomicalLayers)
            {
                if (layer.layerTransform == null) continue;

                Vector3 targetDisplacement = layer.explosionDirection.normalized * (maxExplosionDistance * layer.distanceMultiplier * factor);
                layer.layerTransform.localPosition = layer.initialLocalPos + targetDisplacement;
            }
        }

        /// <summary>
        /// Triggers full exploded view.
        /// </summary>
        public void Explode()
        {
            targetProgress = 1.0f;
            isExploded = true;
            onStateToggled?.Invoke(true);
            OnExplosionStateChanged?.Invoke(true);
            Debug.Log("[ExplodedViewController] Explode initiated.");
        }

        /// <summary>
        /// Collapses organ back into unified whole.
        /// </summary>
        public void Collapse()
        {
            targetProgress = 0f;
            isExploded = false;
            onStateToggled?.Invoke(false);
            OnExplosionStateChanged?.Invoke(false);
            Debug.Log("[ExplodedViewController] Collapse initiated.");
        }

        /// <summary>
        /// Toggles between exploded and collapsed state.
        /// </summary>
        public void ToggleExplode()
        {
            if (isExploded || targetProgress > 0.5f)
            {
                Collapse();
            }
            else
            {
                Explode();
            }
        }

        /// <summary>
        /// Sets a specific target explosion progress [0.0 - 1.0].
        /// </summary>
        public void SetExplosionProgress(float progress)
        {
            targetProgress = Mathf.Clamp01(progress);
            isExploded = targetProgress > 0.5f;
        }

        /// <summary>
        /// Updates progress incrementally (e.g. from continuous gesture pinch zoom or spread).
        /// </summary>
        public void UpdateExplosionDelta(float delta)
        {
            SetExplosionProgress(targetProgress + delta);
        }

        /// <summary>
        /// Isolates a specific anatomical layer by expanding all other layers further or highlighting the target.
        /// </summary>
        public void IsolateLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return;

            // Ensure procedural pieces are active
            if (proceduralContainer != null) proceduralContainer.SetActive(true);
            if (originalMeshRenderer != null) originalMeshRenderer.enabled = false;

            foreach (var layer in anatomicalLayers)
            {
                if (layer.layerTransform == null) continue;

                bool match = string.Equals(layer.layerName, layerName, StringComparison.OrdinalIgnoreCase) ||
                             layer.layerName.IndexOf(layerName, StringComparison.OrdinalIgnoreCase) >= 0;

                layer.isIsolated = match;
                if (!match)
                {
                    // Push non-isolated layers further outward
                    Vector3 displacement = layer.explosionDirection.normalized * (maxExplosionDistance * layer.distanceMultiplier * 1.6f);
                    layer.layerTransform.localPosition = layer.initialLocalPos + displacement;
                }
                else
                {
                    // Focus isolated layer at nominal viewing position
                    layer.layerTransform.localPosition = layer.initialLocalPos;
                }
            }

            OnLayerIsolated?.Invoke(layerName);
            Debug.Log($"[ExplodedViewController] Isolated anatomical layer: '{layerName}'.");
        }

        /// <summary>
        /// Resets all layers to their initial assembled transforms.
        /// </summary>
        public void ResetAll()
        {
            targetProgress = 0f;
            explosionProgress = 0f;
            isExploded = false;

            if (proceduralContainer != null) proceduralContainer.SetActive(false);
            if (originalMeshRenderer != null) originalMeshRenderer.enabled = true;

            foreach (var layer in anatomicalLayers)
            {
                if (layer.layerTransform != null)
                {
                    layer.layerTransform.localPosition = layer.initialLocalPos;
                    layer.layerTransform.localRotation = layer.initialLocalRot;
                    layer.isIsolated = false;
                }
            }

            onProgressChanged?.Invoke(0f);
            onStateToggled?.Invoke(false);
            OnExplosionStateChanged?.Invoke(false);
            Debug.Log("[ExplodedViewController] Reset all anatomical layers to baseline assembled state.");
        }
    }
}
