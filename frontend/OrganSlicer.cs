using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// HoloMed AI - Physics-Based Slicing & Dissection Controller
/// File: frontend/OrganSlicer.cs
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
    private Coroutine sliceAnimCoroutine;

    void Awake()
    {
        if (vitalsManager == null)
        {
            vitalsManager = FindFirstObjectByType<VitalsSimulationManager>();
        }

        if (leftHalf != null) leftInitialPos = leftHalf.localPosition;
        if (rightHalf != null) rightInitialPos = rightHalf.localPosition;
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
