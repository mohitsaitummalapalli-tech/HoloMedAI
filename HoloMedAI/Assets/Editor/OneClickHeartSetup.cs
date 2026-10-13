using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using HoloMedAI.Gestures;

/// <summary>
/// HoloMed AI - 1-Click Master Setup Utility
/// Provides a single consolidated menu item [HoloMed/1-Click Master Setup]
/// that executes the entire end-to-end setup and wiring pipeline:
/// 1. Destroys any existing Heart, Light, or UI objects from previous runs.
/// 2. Spawns the Heart root and Human Heart child.
/// 3. Calculates bounds and fixes the pivot point to the bottom base.
/// 4. Positions the Heart root at (0, -0.5f, 0).
/// 5. Loads, uncompresses, and assigns the 4K texture and URP Lit material.
/// 6. Attaches GestureReceiver, ModelManipulator, OrganSlicer, and VoiceCommandReceiver.
/// 7. Destroys MouseRotator completely.
/// 8. Enables GestureReceiver, ModelManipulator, and OrganSlicer.
/// 9. Disables VoiceCommandReceiver.
/// 10. Wires GestureReceiver UnityEvents directly to ModelManipulator and OrganSlicer.
/// 11. Configures the Main Camera with a dark void background (CameraClearFlags.SolidColor).
/// 12. Adjusts Directional Light and creates HoloProjectorLight for bottom-up holographic glow.
/// 13. Generates base UI Canvas (HoloMed_UI) with futuristic header text.
/// </summary>
[InitializeOnLoad]
public static class OneClickHeartSetup
{
    private const string RefreshSessionKey = "HoloMed_ClearedObsoleteMenus_Done";

    static OneClickHeartSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (!SessionState.GetBool(RefreshSessionKey, false))
            {
                SessionState.SetBool(RefreshSessionKey, true);
                AssetDatabase.Refresh();
            }
        };
    }

    private const string ModelPath = "Assets/Models/Human Heart.obj";
    private const string TexturePath = "Assets/Models/Heart_Diffuse.png";
    private const string MaterialPath = "Assets/Models/HeartMaterial.mat";
    private const string SliceHandleMaterialPath = "Assets/Models/SliceHandleMaterial.mat";
    private const string CrossSectionShaderName = "Custom/CrossSectionHeart";
    private const string UrpShaderName = "Universal Render Pipeline/Lit";

    [MenuItem("HoloMed/1-Click Master Setup")]
    public static void ExecuteMasterSetup()
    {
        Debug.Log("[OneClickHeartSetup] Starting HoloMed 1-Click Master Setup pipeline...");

        // ── 1. Destroy any existing objects from previous runs ────────────
        string[] targetsToDestroy = { "Heart", "Human Heart", "HEART_MOCK", "HoloProjectorLight", "HoloMed_UI", "SlicePlane", "SliceHandle" };
        var activeScene = EditorSceneManager.GetActiveScene();
        GameObject[] rootObjects = activeScene.GetRootGameObjects();
        List<GameObject> toDestroy = new List<GameObject>();

        foreach (var root in rootObjects)
        {
            if (root == null) continue;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                foreach (string target in targetsToDestroy)
                {
                    if (string.Equals(t.name, target, StringComparison.OrdinalIgnoreCase) ||
                        t.name.StartsWith(target + " (Clone)"))
                    {
                        if (!toDestroy.Contains(t.gameObject))
                            toDestroy.Add(t.gameObject);
                        break;
                    }
                }
            }
        }

        for (int i = toDestroy.Count - 1; i >= 0; i--)
        {
            if (toDestroy[i] != null)
            {
                Undo.DestroyObjectImmediate(toDestroy[i]);
            }
        }

        foreach (string target in targetsToDestroy)
        {
            GameObject obj;
            while ((obj = GameObject.Find(target)) != null)
            {
                Undo.DestroyObjectImmediate(obj);
            }
        }
        Debug.Log("[OneClickHeartSetup] Destroyed existing Heart and Human Heart objects.");

        // ── 2. Spawn the 'Heart' root and 'Human Heart' child ─────────────
        GameObject heartRoot = new GameObject("Heart");
        heartRoot.transform.position = Vector3.zero;
        heartRoot.transform.rotation = Quaternion.identity;
        heartRoot.transform.localScale = Vector3.one;
        Undo.RegisterCreatedObjectUndo(heartRoot, "Create Heart Root");

        GameObject heartAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (heartAsset == null)
        {
            Debug.LogError($"[OneClickHeartSetup] Could not load model asset at '{ModelPath}'.");
            return;
        }

        GameObject heartChild = PrefabUtility.InstantiatePrefab(heartAsset, heartRoot.transform) as GameObject;
        if (heartChild == null)
        {
            heartChild = UnityEngine.Object.Instantiate(heartAsset, heartRoot.transform);
        }

        heartChild.name = "Human Heart";
        heartChild.transform.localPosition = Vector3.zero;
        heartChild.transform.localScale = Vector3.one;
        heartChild.transform.localRotation = Quaternion.identity;
        Undo.RegisterCreatedObjectUndo(heartChild, "Instantiate Human Heart");
        Debug.Log("[OneClickHeartSetup] Spawned 'Heart' root and 'Human Heart' child.");

        // ── 3. Calculate bounds and fix the pivot point to the bottom base ─
        Renderer[] childRenderers = heartChild.GetComponentsInChildren<Renderer>(true);
        if (childRenderers.Length > 0)
        {
            Bounds combinedBounds = childRenderers[0].bounds;
            for (int i = 1; i < childRenderers.Length; i++)
            {
                combinedBounds.Encapsulate(childRenderers[i].bounds);
            }

            // Shift child localPosition UP so the lowest mesh point sits at parent Y = 0
            float lowestYInParent = combinedBounds.min.y - heartRoot.transform.position.y;
            heartChild.transform.localPosition = new Vector3(
                heartChild.transform.localPosition.x,
                heartChild.transform.localPosition.y - lowestYInParent,
                heartChild.transform.localPosition.z
            );
            Debug.Log($"[OneClickHeartSetup] Fixed pivot point to bottom base (child localPosition: {heartChild.transform.localPosition}).");
        }

        // ── 4. Position the 'Heart' root at (0, -0.5f, 0) ──────────────────
        heartRoot.transform.position = new Vector3(0f, -0.5f, 0f);
        Debug.Log("[OneClickHeartSetup] Positioned 'Heart' root at (0, -0.5, 0).");

        // ── 5. Load, uncompress, and assign the 4K texture/URP material ───
        TextureImporter importer = AssetImporter.GetAtPath(TexturePath) as TextureImporter;
        if (importer != null)
        {
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            Debug.Log($"[OneClickHeartSetup] Configured TextureImporter for '{TexturePath}': maxTextureSize = 4096, uncompressed.");
        }
        else
        {
            Debug.LogWarning($"[OneClickHeartSetup] TextureImporter not found for '{TexturePath}'.");
        }

        Texture2D diffuseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);

        Shader targetShader = Shader.Find(CrossSectionShaderName);
        if (targetShader == null)
        {
            targetShader = Shader.Find(UrpShaderName) ?? Shader.Find("Standard");
        }

        Material heartMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (heartMaterial == null)
        {
            heartMaterial = new Material(targetShader);
            AssetDatabase.CreateAsset(heartMaterial, MaterialPath);
            Debug.Log($"[OneClickHeartSetup] Created new material with CrossSectionHeart shader at '{MaterialPath}'.");
        }
        else
        {
            if (targetShader != null)
            {
                heartMaterial.shader = targetShader;
            }
        }

        if (diffuseTex != null)
        {
            heartMaterial.SetTexture("_BaseMap", diffuseTex);
            heartMaterial.SetTexture("_MainTex", diffuseTex);
            heartMaterial.SetColor("_BaseColor", Color.white);
            heartMaterial.SetColor("_Color", Color.white);
        }
        heartMaterial.SetColor("_InteriorColor", new Color(0.55f, 0.05f, 0.08f, 1f));
        heartMaterial.SetColor("_CutEdgeColor", Color.cyan);
        heartMaterial.SetFloat("_CutEdgeWidth", 0.005f);

        EditorUtility.SetDirty(heartMaterial);
        AssetDatabase.SaveAssets();

        MeshRenderer[] meshRenderers = heartChild.GetComponentsInChildren<MeshRenderer>(true);
        foreach (MeshRenderer mr in meshRenderers)
        {
            Material[] mats = new Material[mr.sharedMaterials.Length > 0 ? mr.sharedMaterials.Length : 1];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = heartMaterial;
            }
            mr.sharedMaterials = mats;
            EditorUtility.SetDirty(mr);
        }
        Debug.Log($"[OneClickHeartSetup] Assigned 4K CrossSection material to {meshRenderers.Length} MeshRenderer(s).");

        // ── 6. Attach components to the 'Heart' root ─────────────────────
        GestureReceiver gestureReceiver = heartRoot.GetComponent<GestureReceiver>();
        if (gestureReceiver == null)
        {
            gestureReceiver = heartRoot.AddComponent<GestureReceiver>();
            Undo.RegisterCreatedObjectUndo(gestureReceiver, "Add GestureReceiver");
        }

        ModelManipulator modelManipulator = heartRoot.GetComponent<ModelManipulator>();
        if (modelManipulator == null)
        {
            modelManipulator = heartRoot.AddComponent<ModelManipulator>();
            Undo.RegisterCreatedObjectUndo(modelManipulator, "Add ModelManipulator");
        }

        OrganSlicer organSlicer = heartRoot.GetComponent<OrganSlicer>();
        if (organSlicer == null)
        {
            organSlicer = heartRoot.AddComponent<OrganSlicer>();
            Undo.RegisterCreatedObjectUndo(organSlicer, "Add OrganSlicer");
        }

        // Programmatically spawn an invisible "SlicePlane" GameObject
        GameObject slicePlaneObj = new GameObject("SlicePlane");
        Undo.RegisterCreatedObjectUndo(slicePlaneObj, "Create SlicePlane");
        slicePlaneObj.transform.position = new Vector3(0f, -0.5f, -1.5f);
        slicePlaneObj.transform.rotation = Quaternion.identity;
        EditorUtility.SetDirty(slicePlaneObj);

        // Assign the "SlicePlane" to the OrganSlicer component on the Heart root
        organSlicer.slicePlaneTransform = slicePlaneObj.transform;
        organSlicer.targetMaterial = heartMaterial;
        organSlicer.UpdateSlicePlaneShader();
        EditorUtility.SetDirty(organSlicer);
        Debug.Log("[OneClickHeartSetup] Spawned invisible SlicePlane and assigned to OrganSlicer.");

        // Programmatically create a child GameObject named "SliceHandle" (holographic pinpoint dot)
        GameObject sliceHandle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sliceHandle.name = "SliceHandle";
        Undo.RegisterCreatedObjectUndo(sliceHandle, "Create SliceHandle");
        sliceHandle.transform.SetParent(slicePlaneObj.transform, false);
        sliceHandle.transform.localPosition = Vector3.zero;
        sliceHandle.transform.localRotation = Quaternion.identity;
        sliceHandle.transform.localScale = new Vector3(0.06f, 0.06f, 0.06f);

        // Destroy the sphere's Collider component so it doesn't interfere
        Collider sphereCollider = sliceHandle.GetComponent<Collider>();
        if (sphereCollider != null)
        {
            Undo.DestroyObjectImmediate(sphereCollider);
        }

        // Create a new material using "Universal Render Pipeline/Unlit" with Color.cyan
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        Material handleMat = AssetDatabase.LoadAssetAtPath<Material>(SliceHandleMaterialPath);
        if (handleMat == null)
        {
            handleMat = new Material(unlitShader);
            AssetDatabase.CreateAsset(handleMat, SliceHandleMaterialPath);
        }
        else if (unlitShader != null)
        {
            handleMat.shader = unlitShader;
        }

        if (handleMat.HasProperty("_BaseColor")) handleMat.SetColor("_BaseColor", Color.cyan);
        if (handleMat.HasProperty("_Color")) handleMat.SetColor("_Color", Color.cyan);
        EditorUtility.SetDirty(handleMat);
        AssetDatabase.SaveAssets();

        MeshRenderer handleRenderer = sliceHandle.GetComponent<MeshRenderer>();
        if (handleRenderer != null)
        {
            handleRenderer.sharedMaterial = handleMat;
            EditorUtility.SetDirty(handleRenderer);
        }
        EditorUtility.SetDirty(sliceHandle);
        Debug.Log("[OneClickHeartSetup] Attached holographic SliceHandle sphere indicator to SlicePlane.");

        VoiceCommandReceiver voiceReceiver = heartRoot.GetComponent<VoiceCommandReceiver>();
        if (voiceReceiver == null)
        {
            voiceReceiver = heartRoot.AddComponent<VoiceCommandReceiver>();
            Undo.RegisterCreatedObjectUndo(voiceReceiver, "Add VoiceCommandReceiver");
        }
        Debug.Log("[OneClickHeartSetup] Attached GestureReceiver, ModelManipulator, OrganSlicer, and VoiceCommandReceiver.");

        // ── 7. Destroy MouseRotator completely & disable HologramController ─
        MouseRotator[] existingMouseRotators = heartRoot.GetComponentsInChildren<MouseRotator>(true);
        foreach (MouseRotator mr in existingMouseRotators)
        {
            if (mr != null)
            {
                mr.enabled = false;
                EditorUtility.SetDirty(mr);
                Undo.DestroyObjectImmediate(mr);
            }
        }

        HologramController[] existingHoloControllers = UnityEngine.Object.FindObjectsByType<HologramController>();
        foreach (var hc in existingHoloControllers)
        {
            if (hc != null)
            {
                hc.enabled = false;
                EditorUtility.SetDirty(hc);
            }
        }
        Debug.Log("[OneClickHeartSetup] Destroyed MouseRotator and disabled HologramController components.");

        // ── 8. Enable GestureReceiver, ModelManipulator, and OrganSlicer ──
        gestureReceiver.enabled = true;
        modelManipulator.enabled = true;
        organSlicer.enabled = true;
        EditorUtility.SetDirty(gestureReceiver);
        EditorUtility.SetDirty(modelManipulator);
        EditorUtility.SetDirty(organSlicer);

        // ── 9. Enable VoiceCommandReceiver & Wire ULTRON Nervous System ──
        voiceReceiver.enabled = true;
        voiceReceiver.port = 5053;
        voiceReceiver.targetRoot = heartRoot.transform;
        voiceReceiver.organSlicer = organSlicer;

        while (voiceReceiver.OnResetModel.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(voiceReceiver.OnResetModel, 0);

        UnityEventTools.AddPersistentListener(voiceReceiver.OnResetModel, modelManipulator.ResetTransform);
        voiceReceiver.OnResetModel.AddListener(modelManipulator.ResetTransform);

        EditorUtility.SetDirty(voiceReceiver);
        Debug.Log("[OneClickHeartSetup] Enabled VoiceCommandReceiver on Port 5053 and wired OnResetModel to ModelManipulator.ResetTransform.");

        // ── 10. Wire GestureReceiver UnityEvents directly ─────────────────
        modelManipulator.TargetTransform = heartRoot.transform;
        EditorUtility.SetDirty(modelManipulator);

        while (gestureReceiver.OnRotateEvent.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(gestureReceiver.OnRotateEvent, 0);
        while (gestureReceiver.OnZoomEvent.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(gestureReceiver.OnZoomEvent, 0);
        while (gestureReceiver.OnPanEvent.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(gestureReceiver.OnPanEvent, 0);
        while (gestureReceiver.OnSliceEvent.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(gestureReceiver.OnSliceEvent, 0);
        while (gestureReceiver.OnPinchEvent.GetPersistentEventCount() > 0)
            UnityEventTools.RemovePersistentListener(gestureReceiver.OnPinchEvent, 0);

        // Wire UnityEvents directly to ModelManipulator and OrganSlicer
        UnityEventTools.AddPersistentListener<float, float>(gestureReceiver.OnRotateEvent, modelManipulator.Rotate);
        UnityEventTools.AddPersistentListener<float>(gestureReceiver.OnZoomEvent, modelManipulator.Zoom);
        UnityEventTools.AddPersistentListener<float, float>(gestureReceiver.OnPanEvent, modelManipulator.Move);
        UnityEventTools.AddPersistentListener(gestureReceiver.OnSliceEvent, organSlicer.ToggleSlice);
        UnityEventTools.AddPersistentListener(gestureReceiver.OnPinchEvent, organSlicer.ExecuteSlice);

        // Wire runtime delegates and component references
        gestureReceiver.Manipulator = modelManipulator;
        gestureReceiver.Slicer = organSlicer;
        gestureReceiver.WireCategory1Pipeline(modelManipulator, organSlicer);

        EditorUtility.SetDirty(gestureReceiver);
        Debug.Log("[OneClickHeartSetup] Wired GestureReceiver UnityEvents to ModelManipulator (Rotate, Zoom, Move) and OrganSlicer (ToggleSlice, ExecuteSlice).");

        // ── 11. Configure the Main Camera ──────────────────────────────────
        GameObject mainCameraObj = GameObject.Find("Main Camera");
        if (mainCameraObj == null && Camera.main != null)
        {
            mainCameraObj = Camera.main.gameObject;
        }

        if (mainCameraObj != null)
        {
            Undo.RecordObject(mainCameraObj.transform, "Position Main Camera");
            mainCameraObj.transform.position = new Vector3(0f, 0f, -1.5f);
            mainCameraObj.transform.LookAt(Vector3.zero);

            Camera cam = mainCameraObj.GetComponent<Camera>();
            if (cam != null)
            {
                Undo.RecordObject(cam, "Configure Camera Visuals");
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.01f, 0.015f, 0.03f);
                EditorUtility.SetDirty(cam);
            }

            EditorUtility.SetDirty(mainCameraObj);
            Debug.Log("[OneClickHeartSetup] Configured Main Camera: solid deep navy void background, positioned at (0, 0, -1.5).");
        }
        else
        {
            Debug.LogWarning("[OneClickHeartSetup] Main Camera not found in the scene.");
        }

        // ── 12. Configure Holographic Lighting ────────────────────────────
        // 12a. Find existing Directional Light, lower intensity to 0.5, tint pale blue
        Light dirLight = null;
        GameObject dirLightObj = GameObject.Find("Directional Light");
        if (dirLightObj != null)
        {
            dirLight = dirLightObj.GetComponent<Light>();
        }
        if (dirLight == null)
        {
            foreach (var light in GameObject.FindObjectsByType<Light>())
            {
                if (light.type == LightType.Directional)
                {
                    dirLight = light;
                    break;
                }
            }
        }

        if (dirLight != null)
        {
            Undo.RecordObject(dirLight, "Update Directional Light");
            dirLight.intensity = 0.5f;
            dirLight.color = new Color(0.75f, 0.85f, 1.0f); // Pale blue tint
            EditorUtility.SetDirty(dirLight);
            Debug.Log("[OneClickHeartSetup] Updated Directional Light: intensity = 0.5, color = pale blue.");
        }
        else
        {
            Debug.LogWarning("[OneClickHeartSetup] Directional Light not found in the scene.");
        }

        // 12b. Create HoloProjectorLight at (0, -3.0, 0) pointing straight UP as a Spot light
        GameObject projectorLightObj = new GameObject("HoloProjectorLight");
        Undo.RegisterCreatedObjectUndo(projectorLightObj, "Create HoloProjectorLight");
        projectorLightObj.transform.position = new Vector3(0f, -3.0f, 0f);
        projectorLightObj.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

        Light projectorLight = projectorLightObj.AddComponent<Light>();
        projectorLight.type = LightType.Spot;
        projectorLight.spotAngle = 60f;
        projectorLight.color = Color.cyan;
        projectorLight.intensity = 3.5f;
        projectorLight.range = 10f;

        EditorUtility.SetDirty(projectorLightObj);
        Debug.Log("[OneClickHeartSetup] Created HoloProjectorLight at (0, -3.0, 0) pointing UP with cyan Spot Light (intensity=3.5, spotAngle=60, range=10).");

        // ── 13. Generate Base UI (Canvas) ──────────────────────────────────
        GameObject canvasObj = new GameObject("HoloMed_UI");
        Undo.RegisterCreatedObjectUndo(canvasObj, "Create HoloMed_UI Canvas");

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        canvasObj.AddComponent<GraphicRaycaster>();

        // Ensure EventSystem exists if not already present
        if (GameObject.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            GameObject eventSystemObj = new GameObject("EventSystem");
            eventSystemObj.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystemObj.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            Undo.RegisterCreatedObjectUndo(eventSystemObj, "Create EventSystem");
        }

        // Create child Header GameObject
        GameObject headerObj = new GameObject("Header");
        Undo.RegisterCreatedObjectUndo(headerObj, "Create Header Text");
        headerObj.transform.SetParent(canvasObj.transform, false);

        Text headerText = headerObj.AddComponent<Text>();
        headerText.text = "HOLOMED AI // ANATOMY VISUALIZER";
        headerText.color = Color.cyan;
        headerText.fontStyle = FontStyle.Bold;
        headerText.fontSize = 28;
        headerText.alignment = TextAnchor.UpperLeft;

        // Load built-in font for Unity UI Text
        Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (defaultFont == null)
            defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (defaultFont != null)
            headerText.font = defaultFont;

        // Anchor Header to Top-Left
        RectTransform headerRect = headerText.rectTransform;
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(0f, 1f);
        headerRect.pivot = new Vector2(0f, 1f);
        headerRect.anchoredPosition = new Vector2(40f, -40f);
        headerRect.sizeDelta = new Vector2(800f, 60f);

        EditorUtility.SetDirty(headerObj);
        EditorUtility.SetDirty(canvasObj);
        Debug.Log("[OneClickHeartSetup] Created HoloMed_UI Canvas with Top-Left Header: 'HOLOMED AI // ANATOMY VISUALIZER'.");

        // Save scene & refresh assets
        if (!Application.isPlaying)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("[OneClickHeartSetup] ✔ HoloMed 1-Click Master Setup completed successfully!");
    }
}
