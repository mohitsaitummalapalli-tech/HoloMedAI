using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// HoloMed AI - Physics-Based Slicing & Dissection Controller
/// File: Assets/OrganSlicer.cs
/// 
/// Phase 3: Clinical Realism
/// 
/// Responsibilities:
/// 1. Provides dynamic cross-section slicing / dissection triggers when "SLICE" or "CUT" commands are received.
/// 2. Smoothly animates dissected organ halves or toggles internal cutaway chambers.
/// 3. Communicates surgical trauma to the VitalsSimulationManager (causing immediate heart rate and BP changes).
/// </summary>
public class OrganSlicer : MonoBehaviour
{
    [Header("Cross-Section Clipping Plane (Glass Sheet)")]
    [Tooltip("Visual and mathematical 'glass sheet' defining the cutting plane position and normal")]
    public Transform slicePlaneTransform;

    [Tooltip("Target heart Material using Custom/CrossSectionHeart (auto-detected if null)")]
    public Material targetMaterial;

    [Header("Slicing Visual Components")]
    [Tooltip("Internal chamber / cutaway mesh revealed upon slicing")]
    public GameObject crossSectionCutaway;

    [Tooltip("Visual surgical cutting plane / laser indicator")]
    public GameObject slicePlaneIndicator;

    [Tooltip("Left dissected anatomical hemisphere/half (optional)")]
    public Transform leftHalf;

    [Tooltip("Right dissected anatomical hemisphere/half (optional)")]
    public Transform rightHalf;

    [Header("Surgical Trauma & Vitals Reference")]
    [Tooltip("Vitals Manager to trigger physiological trauma events upon slicing")]
    public VitalsSimulationManager vitalsManager;

    [Header("Slice Animation Settings")]
    public float separationDistance = 0.6f;
    public float sliceAnimDuration = 0.5f;
    public bool isSliced = false;

    // Events
    public static event Action<bool> OnSliceStateChanged;

    private Vector3 leftInitialPos;
    private Vector3 rightInitialPos;
    private Vector3 initialPlanePos = new Vector3(0f, -0.5f, -1.5f);
    private Quaternion initialPlaneRot = Quaternion.identity;
    private Coroutine sliceAnimCoroutine;
    private Renderer heartRenderer;

    void Awake()
    {
        if (vitalsManager == null)
        {
            vitalsManager = FindAnyObjectByType<VitalsSimulationManager>();
        }

        if (leftHalf != null) leftInitialPos = leftHalf.localPosition;
        if (rightHalf != null) rightInitialPos = rightHalf.localPosition;

        if (slicePlaneTransform == null)
        {
            GameObject planeObj = GameObject.Find("SlicePlane");
            if (planeObj != null) slicePlaneTransform = planeObj.transform;
        }

        if (slicePlaneTransform != null)
        {
            initialPlanePos = slicePlaneTransform.position;
            initialPlaneRot = slicePlaneTransform.rotation;
        }

        FindTargetMaterial();
    }

    void Start()
    {
        FindTargetMaterial();
        UpdateSlicePlaneShader();
    }

    void Update()
    {
        UpdateSlicePlaneShader();
    }

    private void FindTargetMaterial()
    {
        if (targetMaterial == null)
        {
            if (heartRenderer == null) heartRenderer = GetComponentInChildren<Renderer>();
            if (heartRenderer != null)
            {
                targetMaterial = Application.isPlaying ? heartRenderer.material : heartRenderer.sharedMaterial;
            }
        }
    }

    /// <summary>
    /// Calculates the plane equation (Vector4) from slicePlaneTransform and updates the shader material.
    /// Plane equation: dot(worldPos, normal) - distance >= 0
    /// </summary>
    public void UpdateSlicePlaneShader()
    {
        if (slicePlaneTransform == null)
        {
            GameObject planeObj = GameObject.Find("SlicePlane");
            if (planeObj != null)
            {
                slicePlaneTransform = planeObj.transform;
                initialPlanePos = slicePlaneTransform.position;
                initialPlaneRot = slicePlaneTransform.rotation;
            }
        }

        if (slicePlaneTransform == null) return;

        if (targetMaterial == null)
        {
            FindTargetMaterial();
        }

        if (targetMaterial == null) return;

        // Plane normal: forward vector of slicePlaneTransform
        Vector3 normal = slicePlaneTransform.forward.normalized;
        // Plane distance: projection of plane position onto normal
        float distance = Vector3.Dot(normal, slicePlaneTransform.position);
        Vector4 planeVector = new Vector4(normal.x, normal.y, normal.z, distance);

        targetMaterial.SetVector("_SlicePlane", planeVector);
    }

    /// <summary>
    /// Moves the slicePlaneTransform forward and backward along the Z-axis based on gesture input.
    /// Clamps position between -1.5f (in front of heart) and 1.5f (behind heart).
    /// </summary>
    /// <param name="depthDelta">Depth delta movement along the slicing axis</param>
    public void UpdateSliceDepth(float depthDelta)
    {
        if (slicePlaneTransform == null) return;

        float newZ = Mathf.Clamp(slicePlaneTransform.position.z + (depthDelta * 15f), -1.5f, 1.5f);
        slicePlaneTransform.position = new Vector3(0f, -0.5f, newZ);
        UpdateSlicePlaneShader();
    }

    /// <summary>
    /// Executes surgical dissection, animates halves, and triggers patient vitals trauma.
    /// </summary>
    public void ExecuteSlice()
    {
        isSliced = true;

        // 1. Reveal internal cross-section cutaway
        if (crossSectionCutaway != null)
        {
            crossSectionCutaway.SetActive(true);
        }

        // 2. Show cutting plane laser momentarily
        if (slicePlaneIndicator != null)
        {
            slicePlaneIndicator.SetActive(true);
            StartCoroutine(HidePlaneAfterDelay(1.2f));
        }

        // 3. Animate dissected halves parting
        if (leftHalf != null && rightHalf != null)
        {
            if (sliceAnimCoroutine != null) StopCoroutine(sliceAnimCoroutine);
            sliceAnimCoroutine = StartCoroutine(AnimateHalvesSeparation(true));
        }

        // 4. Trigger Patient Vitals Trauma Spike
        if (vitalsManager != null)
        {
            vitalsManager.TriggerTraumaSpike("Surgical Cardiac Incision / Dissection", 1.25f);
        }

        OnSliceStateChanged?.Invoke(true);
        Debug.Log("<color=#FF3366><b>[OrganSlicer]</b></color> Surgical Slicing Plane Executed -> Patient Vitals Trauma Triggered!");
    }

    /// <summary>
    /// Restores organ back into unified whole.
    /// </summary>
    public void ResetSlice()
    {
        isSliced = false;

        if (crossSectionCutaway != null)
        {
            crossSectionCutaway.SetActive(false);
        }

        if (slicePlaneIndicator != null)
        {
            slicePlaneIndicator.SetActive(false);
        }

        if (leftHalf != null && rightHalf != null)
        {
            if (sliceAnimCoroutine != null) StopCoroutine(sliceAnimCoroutine);
            sliceAnimCoroutine = StartCoroutine(AnimateHalvesSeparation(false));
        }

        if (slicePlaneTransform != null && (initialPlanePos != Vector3.zero || initialPlaneRot != Quaternion.identity))
        {
            slicePlaneTransform.position = initialPlanePos;
            slicePlaneTransform.rotation = initialPlaneRot;
            UpdateSlicePlaneShader();
        }

        OnSliceStateChanged?.Invoke(false);
        Debug.Log("<color=#00FFB4>[OrganSlicer]</color> Dissected organ halves reunited.");
    }

    /// <summary>
    /// Toggles between sliced dissection and unified organ.
    /// </summary>
    public void ToggleSlice()
    {
        if (isSliced)
        {
            ResetSlice();
        }
        else
        {
            ExecuteSlice();
        }
    }

    private IEnumerator AnimateHalvesSeparation(bool separate)
    {
        float elapsed = 0f;
        Vector3 leftStart = leftHalf.localPosition;
        Vector3 rightStart = rightHalf.localPosition;

        Vector3 leftTarget = separate ? (leftInitialPos + Vector3.left * separationDistance) : leftInitialPos;
        Vector3 rightTarget = separate ? (rightInitialPos + Vector3.right * separationDistance) : rightInitialPos;

        while (elapsed < sliceAnimDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / sliceAnimDuration);
            leftHalf.localPosition = Vector3.Lerp(leftStart, leftTarget, t);
            rightHalf.localPosition = Vector3.Lerp(rightStart, rightTarget, t);
            yield return null;
        }

        leftHalf.localPosition = leftTarget;
        rightHalf.localPosition = rightTarget;
        sliceAnimCoroutine = null;
    }

    private IEnumerator HidePlaneAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (slicePlaneIndicator != null) slicePlaneIndicator.SetActive(false);
    }
}
