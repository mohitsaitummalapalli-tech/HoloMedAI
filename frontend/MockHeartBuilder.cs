#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using HoloMedAI.Gestures;

namespace HoloMedAI.EditorTools
{
    /// <summary>
    /// HoloMed AI - Editor Utility: Procedural Mock Heart Builder
    /// File: frontend/MockHeartBuilder.cs
    /// 
    /// Purpose:
    /// Provides an immediate anatomical placeholder in Unity scenes while the full
    /// 3D cardiac asset is in delivery. Builds labeled anatomical chambers matching
    /// the AI voice vocabulary and gesture raycasting requirements:
    /// - LEFT_VENTRICLE (Capsule, scaled & offset left, arterial crimson)
    /// - RIGHT_VENTRICLE (Capsule, scaled & offset right, venous blue)
    /// - LEFT_ATRIUM (Sphere, offset top-left, arterial pink)
    /// - RIGHT_ATRIUM (Sphere, offset top-right, venous cyan)
    /// - AORTA (Cylinder, bright arterial red, ascending from top)
    /// - VENA_CAVA (Cylinder, deep venous blue, posterior superior)
    /// 
    /// Connects automatically to GestureReceiver, ModelManipulationController,
    /// AnatomySelectionController, VoiceCommandReceiver, and OrganStateManager.
    /// Full Undo / Redo support via Undo.RegisterCreatedObjectUndo.
    /// </summary>
    public static class MockHeartBuilder
    {
        private const string RootGameObjectName = "HEART_MOCK";

        // Material Palette: Oxygenated (Red) vs Deoxygenated (Blue) contrast
        private static readonly Color ColorLeftVentricle  = new Color(0.72f, 0.11f, 0.11f, 1.0f); // Deep Crimson
        private static readonly Color ColorRightVentricle = new Color(0.08f, 0.40f, 0.75f, 1.0f); // Venous Blue
        private static readonly Color ColorLeftAtrium     = new Color(0.85f, 0.12f, 0.35f, 1.0f); // Pinkish Myocardium
        private static readonly Color ColorRightAtrium    = new Color(0.01f, 0.53f, 0.82f, 1.0f); // Venous Cyan
        private static readonly Color ColorAorta          = new Color(1.00f, 0.09f, 0.20f, 1.0f); // Bright Scarlet Arterial
        private static readonly Color ColorVenaCava       = new Color(0.05f, 0.28f, 0.63f, 1.0f); // Deep Royal Blue

        [MenuItem("HoloMed/Spawn Composite Mock Heart")]
        public static void SpawnCompositeMockHeart()
        {
            // 1. Clean up existing mockup if present to avoid duplicate clutter
            GameObject existingMock = GameObject.Find(RootGameObjectName);
            if (existingMock != null)
            {
                Undo.DestroyObjectImmediate(existingMock);
                Debug.Log($"[HoloMed] Replaced previous {RootGameObjectName} instance.");
            }

            // 2. Procedurally create root GameObject at (0, 0, 0)
            GameObject heartRoot = new GameObject(RootGameObjectName);
            heartRoot.transform.position = Vector3.zero;
            heartRoot.transform.rotation = Quaternion.identity;
            heartRoot.transform.localScale = Vector3.one;

            // Register Root creation with Undo system
            Undo.RegisterCreatedObjectUndo(heartRoot, "Spawn Composite Mock Heart");

            // 3. Procedurally assemble anatomical child chambers
            BuildChamber(
                parent: heartRoot.transform,
                name: "LEFT_VENTRICLE",
                primitiveType: PrimitiveType.Capsule,
                position: new Vector3(-0.35f, -0.38f, 0.0f),
                rotation: Quaternion.Euler(15f, 0f, -25f),
                scale: new Vector3(0.70f, 1.10f, 0.70f),
                color: ColorLeftVentricle,
                smoothness: 0.55f
            );

            BuildChamber(
                parent: heartRoot.transform,
                name: "RIGHT_VENTRICLE",
                primitiveType: PrimitiveType.Capsule,
                position: new Vector3(0.35f, -0.44f, 0.10f),
                rotation: Quaternion.Euler(10f, 0f, 20f),
                scale: new Vector3(0.65f, 0.95f, 0.65f),
                color: ColorRightVentricle,
                smoothness: 0.55f
            );

            BuildChamber(
                parent: heartRoot.transform,
                name: "LEFT_ATRIUM",
                primitiveType: PrimitiveType.Sphere,
                position: new Vector3(-0.40f, 0.45f, -0.10f),
                rotation: Quaternion.identity,
                scale: new Vector3(0.60f, 0.55f, 0.60f),
                color: ColorLeftAtrium,
                smoothness: 0.50f
            );

            BuildChamber(
                parent: heartRoot.transform,
                name: "RIGHT_ATRIUM",
                primitiveType: PrimitiveType.Sphere,
                position: new Vector3(0.40f, 0.40f, 0.05f),
                rotation: Quaternion.identity,
                scale: new Vector3(0.55f, 0.50f, 0.55f),
                color: ColorRightAtrium,
                smoothness: 0.50f
            );

            BuildChamber(
                parent: heartRoot.transform,
                name: "AORTA",
                primitiveType: PrimitiveType.Cylinder,
                position: new Vector3(-0.10f, 0.92f, -0.05f),
                rotation: Quaternion.Euler(12f, 0f, 18f),
                scale: new Vector3(0.35f, 0.65f, 0.35f),
                color: ColorAorta,
                smoothness: 0.65f
            );

            BuildChamber(
                parent: heartRoot.transform,
                name: "VENA_CAVA",
                primitiveType: PrimitiveType.Cylinder,
                position: new Vector3(0.45f, 0.88f, -0.20f),
                rotation: Quaternion.Euler(-8f, 0f, -6f),
                scale: new Vector3(0.30f, 0.70f, 0.30f),
                color: ColorVenaCava,
                smoothness: 0.65f
            );

            // 4. Auto-wire HEART_MOCK to scene controllers
            AutoWireControllers(heartRoot);

            // 5. Select newly spawned mock in the Hierarchy and mark active scene dirty
            Selection.activeGameObject = heartRoot;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log($"<color=#00E5FF><b>[HoloMed]</b></color> Successfully generated composite anatomical mockup: <b>{RootGameObjectName}</b> at (0, 0, 0) with 6 labeled chambers.");
        }

        [MenuItem("HoloMed/Clear Mock Heart")]
        public static void ClearCompositeMockHeart()
        {
            GameObject existingMock = GameObject.Find(RootGameObjectName);
            if (existingMock != null)
            {
                Undo.DestroyObjectImmediate(existingMock);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"[HoloMed] Cleared {RootGameObjectName} from the active scene.");
            }
            else
            {
                Debug.Log($"[HoloMed] No {RootGameObjectName} found to clear.");
            }
        }

        private static GameObject BuildChamber(
            Transform parent,
            string name,
            PrimitiveType primitiveType,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            float smoothness)
        {
            GameObject chamber = GameObject.CreatePrimitive(primitiveType);
            chamber.name = name;
            chamber.transform.SetParent(parent, false);
            chamber.transform.localPosition = position;
            chamber.transform.localRotation = rotation;
            chamber.transform.localScale = scale;

            // Ensure Collider is configured for spatial raycasting
            Collider col = chamber.GetComponent<Collider>();
            if (col != null)
            {
                col.isTrigger = false;
                col.enabled = true;
            }

            // Create contrast material (Supports both URP and Built-in Standard shaders)
            Material mat = CreateContrastMaterial(name, color, smoothness);
            MeshRenderer renderer = chamber.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = mat;
            }

            return chamber;
        }

        private static Material CreateContrastMaterial(string structureName, Color color, float smoothness)
        {
            // Resolve preferred shader: Universal Render Pipeline/Lit -> Standard -> Mobile/Diffuse
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Diffuse")
                         ?? Shader.Find("Legacy Shaders/Diffuse");

            Material mat = new Material(shader)
            {
                name = $"Mat_Mock_{structureName}"
            };

            // Set URP & Standard color properties
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;

            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.08f);

            // Ensure emission keyword is ready for UI/Ultron highlighting
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.SetColor("_EmissionColor", Color.black);
            }

            return mat;
        }

        private static void AutoWireControllers(GameObject heartRoot)
        {
            int wiredCount = 0;

            // 1. ModelManipulationController
            ModelManipulationController manipController = UnityEngine.Object.FindAnyObjectByType<ModelManipulationController>();
            if (manipController != null)
            {
                manipController.TargetModel = heartRoot.transform;
                SerializedObject so = new SerializedObject(manipController);
                SerializedProperty prop = so.FindProperty("targetModel");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(manipController);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK to ModelManipulationController on '{manipController.gameObject.name}'.");
            }

            // 2. GestureReceiver
            GestureReceiver gestureReceiver = UnityEngine.Object.FindAnyObjectByType<GestureReceiver>();
            if (gestureReceiver != null)
            {
                SerializedObject so = new SerializedObject(gestureReceiver);
                SerializedProperty prop = so.FindProperty("targetModel");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(gestureReceiver);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK to GestureReceiver on '{gestureReceiver.gameObject.name}'.");
            }

            // 3. AnatomySelectionController
            AnatomySelectionController anatomyController = UnityEngine.Object.FindAnyObjectByType<AnatomySelectionController>();
            if (anatomyController != null)
            {
                SerializedObject so = new SerializedObject(anatomyController);
                SerializedProperty prop = so.FindProperty("modelRoot");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                anatomyController.BuildStructureIndex();
                EditorUtility.SetDirty(anatomyController);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK and rebuilt structure index on AnatomySelectionController.");
            }

            // 4. VoiceCommandReceiver
            VoiceCommandReceiver voiceReceiver = UnityEngine.Object.FindAnyObjectByType<VoiceCommandReceiver>();
            if (voiceReceiver != null)
            {
                voiceReceiver.targetRoot = heartRoot.transform;
                SerializedObject so = new SerializedObject(voiceReceiver);
                SerializedProperty prop = so.FindProperty("targetRoot");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(voiceReceiver);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK to VoiceCommandReceiver.");
            }

            // 5. OrganStateManager
            OrganStateManager organState = UnityEngine.Object.FindAnyObjectByType<OrganStateManager>();
            if (organState != null)
            {
                organState.organRoot = heartRoot.transform;
                SerializedObject so = new SerializedObject(organState);
                SerializedProperty prop = so.FindProperty("organRoot");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(organState);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK to OrganStateManager.");
            }

            // 6. HologramController
            HologramController holoController = UnityEngine.Object.FindAnyObjectByType<HologramController>();
            if (holoController != null)
            {
                holoController.hologramRoot = heartRoot.transform;
                SerializedObject so = new SerializedObject(holoController);
                SerializedProperty prop = so.FindProperty("hologramRoot");
                if (prop != null)
                {
                    prop.objectReferenceValue = heartRoot.transform;
                    so.ApplyModifiedProperties();
                }
                EditorUtility.SetDirty(holoController);
                wiredCount++;
                Debug.Log($"[HoloMed] Assigned HEART_MOCK to HologramController.");
            }

            if (wiredCount == 0)
            {
                Debug.LogWarning("[HoloMed] HEART_MOCK generated, but no active gesture/manipulation controllers were found in the current scene to bind.");
            }
        }
    }
}
#endif
