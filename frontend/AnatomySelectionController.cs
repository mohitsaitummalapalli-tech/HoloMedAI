using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    public class AnatomySelectionController : MonoBehaviour, IAnatomySelectionController
    {
        [Header("Model Root")]
        [Tooltip("Root Transform of the anatomy model. If empty, uses this GameObject.")]
        [SerializeField] private Transform modelRoot;

        [Header("Selection Stability")]
        [Tooltip("Minimum time (seconds) required before changing selection to prevent flickering")]
        [SerializeField] private float selectionDebounceTime = 0.35f;

        [Header("Highlight Visual Settings")]
        [ColorUsage(true, true)]
        [SerializeField] private Color highlightEmissionColor = new Color(0f, 1.8f, 2.5f, 1f);

        [SerializeField] private Color dimAlbedoColor = new Color(0.3f, 0.35f, 0.4f, 0.5f);

        public event Action<string, string, GameObject> OnStructureSelected;
        public event Action OnSelectionCleared;

        private class CachedRendererState
        {
            public Renderer renderer;
            public Color originalColor;
            public Color originalEmission;
            public bool hadEmission;
        }

        private readonly Dictionary<string, GameObject> structureLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private readonly List<CachedRendererState> cachedRenderers = new List<CachedRendererState>();
        private GameObject currentSelectedObject = null;
        private string currentSelectedStructureName = "";
        private float lastSelectionTimestamp = -10f;

        private void Awake()
        {
            if (modelRoot == null)
            {
                modelRoot = transform;
            }
            BuildStructureIndex();
        }

        public void BuildStructureIndex()
        {
            structureLookup.Clear();
            cachedRenderers.Clear();

            if (modelRoot == null) return;

            foreach (Transform child in modelRoot.GetComponentsInChildren<Transform>(true))
            {
                string cleanName = child.name.Trim().ToUpperInvariant();
                if (!structureLookup.ContainsKey(cleanName))
                {
                    structureLookup[cleanName] = child.gameObject;
                }
            }

            foreach (Renderer r in modelRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial == null) continue;

                CachedRendererState state = new CachedRendererState
                {
                    renderer = r,
                    originalColor = r.material.HasProperty("_BaseColor") ? r.material.GetColor("_BaseColor") : (r.material.HasProperty("_Color") ? r.material.color : Color.white),
                    originalEmission = r.material.HasProperty("_EmissionColor") ? r.material.GetColor("_EmissionColor") : Color.black,
                    hadEmission = r.material.IsKeywordEnabled("_EMISSION")
                };
                cachedRenderers.Add(state);
            }
        }

        public bool SelectStructure(string organ, string structure)
        {
            if (string.IsNullOrEmpty(structure))
            {
                return false;
            }

            string targetName = structure.Trim().ToUpperInvariant();

            if (targetName.Equals(currentSelectedStructureName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Time.time - lastSelectionTimestamp < selectionDebounceTime)
            {
                return false;
            }

            GameObject targetObj = null;
            if (structureLookup.TryGetValue(targetName, out GameObject directMatch))
            {
                targetObj = directMatch;
            }
            else
            {
                foreach (var kvp in structureLookup)
                {
                    if (kvp.Key.Contains(targetName) || targetName.Contains(kvp.Key))
                    {
                        targetObj = kvp.Value;
                        break;
                    }
                }
            }

            if (targetObj == null)
            {
                return false;
            }

            ApplyHighlight(targetObj);
            currentSelectedObject = targetObj;
            currentSelectedStructureName = targetName;
            lastSelectionTimestamp = Time.time;
            OnStructureSelected?.Invoke(organ, structure, targetObj);
            return true;
        }

        public void ClearSelection()
        {
            foreach (var state in cachedRenderers)
            {
                if (state.renderer == null) continue;

                if (state.renderer.material.HasProperty("_BaseColor"))
                    state.renderer.material.SetColor("_BaseColor", state.originalColor);
                else if (state.renderer.material.HasProperty("_Color"))
                    state.renderer.material.color = state.originalColor;

                if (state.renderer.material.HasProperty("_EmissionColor"))
                    state.renderer.material.SetColor("_EmissionColor", state.originalEmission);

                if (!state.hadEmission)
                    state.renderer.material.DisableKeyword("_EMISSION");
            }

            currentSelectedObject = null;
            currentSelectedStructureName = "";
            OnSelectionCleared?.Invoke();
        }

        private void ApplyHighlight(GameObject targetObj)
        {
            Renderer targetRenderer = targetObj.GetComponent<Renderer>();

            foreach (var state in cachedRenderers)
            {
                if (state.renderer == null) continue;

                if (targetRenderer != null && state.renderer == targetRenderer)
                {
                    state.renderer.material.EnableKeyword("_EMISSION");
                    if (state.renderer.material.HasProperty("_EmissionColor"))
                        state.renderer.material.SetColor("_EmissionColor", highlightEmissionColor);
                    if (state.renderer.material.HasProperty("_BaseColor"))
                        state.renderer.material.SetColor("_BaseColor", Color.white);
                }
                else
                {
                    if (state.renderer.material.HasProperty("_BaseColor"))
                        state.renderer.material.SetColor("_BaseColor", dimAlbedoColor);
                    else if (state.renderer.material.HasProperty("_Color"))
                        state.renderer.material.color = dimAlbedoColor;
                }
            }
        }
    }
}
