using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HoloMed AI - Patient Vitals Simulation Manager
/// File: Assets/VitalsSimulationManager.cs
/// 
/// Phase 3: Clinical Realism
/// 
/// Simulates physiological patient vitals:
/// 1. Heart Rate (BPM): Baseline ~75 BPM, spikes to 130-160 BPM on surgical slicing/trauma, drops on ischemia/arrest.
/// 2. Blood Pressure (mmHg): Dynamic systolic/diastolic calculations (e.g., 120/80 -> 165/105 mmHg under stress).
/// 3. Oxygen Saturation (SpO2 %): ~98-99% baseline, drops to 86-90% during critical hypoxia/ischemia.
/// 4. Cardiac Rhythm Status: Normal Sinus Rhythm, Sinus Tachycardia, STEMI Ischemia, Asystole Flatline.
/// </summary>
public class VitalsSimulationManager : MonoBehaviour
{
    public enum CardiacRhythm
    {
        NormalSinus,
        SinusTachycardia,
        ST_Elevation_Ischemia,
        Bradycardia,
        Asystole_Flatline
    }

    [Header("Current Live Vitals Readout")]
    [Range(0, 220)] public float heartRateBPM = 75f;
    public int systolicBP = 120;
    public int diastolicBP = 80;
    [Range(0, 100)] public float oxygenSpO2 = 98.5f;
    public CardiacRhythm currentRhythm = CardiacRhythm.NormalSinus;

    [Header("Baseline Normal Values")]
    public float baselineBPM = 75f;
    public int baselineSystolic = 120;
    public int baselineDiastolic = 80;
    public float baselineSpO2 = 98.5f;

    [Header("Dynamic Trauma & Recovery Speeds")]
    [Tooltip("Speed at which vitals smoothly transition toward target values")]
    public float recoverySpeed = 0.8f;

    [Tooltip("Target BPM during surgical incision / acute trauma")]
    public float traumaSpikeBPM = 138f;

    [Header("Optional UI References")]
    public Text bpmText;
    public Text bpText;
    public Text spo2Text;
    public Text rhythmText;
    public Slider bpmGauge;

    [Header("On-Screen HUD Display")]
    public bool showVitalsHUD = true;

    // Events
    public static event Action<float, int, int, float, CardiacRhythm> OnVitalsUpdated;
    public static event Action<string> OnCriticalAlarmTriggered;

    // Internal Simulation State
    private float targetBPM = 75f;
    private int targetSystolic = 120;
    private int targetDiastolic = 80;
    private float targetSpO2 = 98.5f;
    private float pulsePhase = 0f;
    private bool isCriticalAlarmActive = false;
    private string lastTraumaEvent = "None (Stable Baseline)";
    private float traumaTimer = 0f;

    void Awake()
    {
        ResetVitals();
    }

    void Update()
    {
        // 1. Smoothly interpolate vitals toward target values
        float dt = Time.deltaTime;
        heartRateBPM = Mathf.Lerp(heartRateBPM, targetBPM, dt * recoverySpeed);
        systolicBP = (int)Mathf.Lerp(systolicBP, targetSystolic, dt * recoverySpeed);
        diastolicBP = (int)Mathf.Lerp(diastolicBP, targetDiastolic, dt * recoverySpeed);
        oxygenSpO2 = Mathf.Lerp(oxygenSpO2, targetSpO2, dt * recoverySpeed);

        // 2. Pulse phase oscillation for physical cardiac pulsation
        float frequencyHz = Mathf.Max(0.1f, heartRateBPM / 60.0f);
        pulsePhase += dt * frequencyHz * Mathf.PI * 2.0f;

        // 3. Trauma recovery timer: slowly stabilize back toward baseline
        if (traumaTimer > 0f)
        {
            traumaTimer -= dt;
            if (traumaTimer <= 0f && currentRhythm != CardiacRhythm.ST_Elevation_Ischemia && currentRhythm != CardiacRhythm.Asystole_Flatline)
            {
                SetNormalBaseline();
            }
        }

        // 4. Update UI Text/Sliders if connected
        UpdateConnectedUI();

        // 5. Fire global event
        OnVitalsUpdated?.Invoke(heartRateBPM, systolicBP, diastolicBP, oxygenSpO2, currentRhythm);
    }

    /// <summary>
    /// Triggers an immediate physiological trauma spike when a surgical slice/cut is made.
    /// </summary>
    public void TriggerTraumaSpike(string traumaSource = "Surgical Incision", float severity = 1.0f)
    {
        lastTraumaEvent = $"{traumaSource} (Severity: {severity:F1}x)";
        traumaTimer = 8.0f; // Stay elevated for 8 seconds before gradual recovery

        targetBPM = Mathf.Clamp(baselineBPM + (traumaSpikeBPM - baselineBPM) * severity, 40f, 185f);
        targetSystolic = Mathf.Clamp((int)(baselineSystolic + 35 * severity), 90, 210);
        targetDiastolic = Mathf.Clamp((int)(baselineDiastolic + 20 * severity), 60, 130);
        targetSpO2 = Mathf.Clamp(baselineSpO2 - (4.5f * severity), 84f, 100f);

        currentRhythm = CardiacRhythm.SinusTachycardia;
        isCriticalAlarmActive = targetBPM > 130f;

        if (isCriticalAlarmActive)
        {
            OnCriticalAlarmTriggered?.Invoke($"TACHYCARDIA ALERT: BPM {targetBPM:F0} | Acute Trauma: {traumaSource}");
        }

        Debug.Log($"<color=#FF3366><b>[Vitals Alert]</b></color> {traumaSource} -> BPM spiking to {targetBPM:F0}, BP: {targetSystolic}/{targetDiastolic} mmHg");
    }

    /// <summary>
    /// Reacts to pathology state changes (e.g. "DISEASED", "INFARCTION", "ISCHEMIA", "HEALTHY").
    /// </summary>
    public void OnPathologyStateChanged(string pathologyState)
    {
        string p = pathologyState.Trim().ToUpperInvariant();

        if (p.Contains("INFARCT") || p.Contains("ISCHEMI") || p.Contains("DISEASE"))
        {
            lastTraumaEvent = "Acute Myocardial Infarction / STEMI";
            targetBPM = 126f;
            targetSystolic = 158;
            targetDiastolic = 102;
            targetSpO2 = 89.0f;
            currentRhythm = CardiacRhythm.ST_Elevation_Ischemia;
            isCriticalAlarmActive = true;
            OnCriticalAlarmTriggered?.Invoke("CRITICAL ALERT: STEMI Myocardial Infarction - SpO2 89% - ST Elevation");
        }
        else if (p.Contains("ARREST") || p.Contains("FLATLINE"))
        {
            lastTraumaEvent = "Cardiac Arrest / Asystole";
            targetBPM = 0f;
            targetSystolic = 40;
            targetDiastolic = 20;
            targetSpO2 = 65.0f;
            currentRhythm = CardiacRhythm.Asystole_Flatline;
            isCriticalAlarmActive = true;
            OnCriticalAlarmTriggered?.Invoke("CODE BLUE: Cardiac Arrest / Asystole Flatline");
        }
        else
        {
            SetNormalBaseline();
        }
    }

    public void SetNormalBaseline()
    {
        targetBPM = baselineBPM;
        targetSystolic = baselineSystolic;
        targetDiastolic = baselineDiastolic;
        targetSpO2 = baselineSpO2;
        currentRhythm = CardiacRhythm.NormalSinus;
        isCriticalAlarmActive = false;
        lastTraumaEvent = "Stable Baseline Restored";
        Debug.Log("<color=#00FFB4>[Vitals]</color> Normal patient vitals baseline restored.");
    }

    public void ResetVitals()
    {
        heartRateBPM = baselineBPM;
        systolicBP = baselineSystolic;
        diastolicBP = baselineDiastolic;
        oxygenSpO2 = baselineSpO2;
        SetNormalBaseline();
    }

    private void UpdateConnectedUI()
    {
        if (bpmText != null) bpmText.text = $"{heartRateBPM:F0} BPM";
        if (bpText != null) bpText.text = $"{systolicBP}/{diastolicBP} mmHg";
        if (spo2Text != null) spo2Text.text = $"{oxygenSpO2:F1}%";
        if (rhythmText != null) rhythmText.text = currentRhythm.ToString().Replace("_", " ");
        if (bpmGauge != null) bpmGauge.value = heartRateBPM;
    }

    // -------------------------------------------------------------
    // ON-SCREEN CLINICAL PATIENT VITALS HUD
    // -------------------------------------------------------------
    void OnGUI()
    {
        if (!showVitalsHUD) return;

        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.fontSize = 12;
        boxStyle.alignment = TextAnchor.UpperLeft;
        boxStyle.normal.textColor = Color.white;

        Color pulseColor = isCriticalAlarmActive ? (Mathf.Sin(Time.time * 8f) > 0 ? Color.red : Color.yellow) : new Color(0f, 1f, 0.7f, 1f);

        GUILayout.BeginArea(new Rect(20, Screen.height - 350, 360, 160), boxStyle);
        GUILayout.Label("<size=13><b>PATIENT VITALS SIMULATOR (ICU / OR)</b></size>");

        GUI.color = pulseColor;
        GUILayout.Label($"<b>HEART RATE:</b> {heartRateBPM:F0} BPM  [{currentRhythm.ToString().Replace("_", " ")}]");
        GUI.color = Color.white;

        GUILayout.Label($"<b>BLOOD PRESSURE:</b> {systolicBP}/{diastolicBP} mmHg (MAP: {(diastolicBP + (systolicBP - diastolicBP) / 3)} mmHg)");
        
        Color spo2Color = oxygenSpO2 < 90f ? Color.red : Color.white;
        GUI.color = spo2Color;
        GUILayout.Label($"<b>OXYGEN SAT (SpO2):</b> {oxygenSpO2:F1}%");
        GUI.color = Color.white;

        GUILayout.Label($"<size=11>Status: {lastTraumaEvent}</size>");

        if (GUILayout.Button("Normalize Vitals (Defibrillate / Restore)"))
        {
            SetNormalBaseline();
        }

        GUILayout.EndArea();
    }
}
