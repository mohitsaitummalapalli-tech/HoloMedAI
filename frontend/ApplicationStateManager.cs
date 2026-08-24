using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    public class ApplicationStateManager : MonoBehaviour, IApplicationStateManager
    {
        [Header("Active Interaction State")]
        [SerializeField] private InteractionState currentInteractionState = InteractionState.IDLE;
        [SerializeField] private string currentOrgan = "HEART";
        [SerializeField] private string currentStructure = "AORTA";
        [SerializeField] private string lastCommand = "NONE";
        [SerializeField] private long lastCommandTimestamp = 0;

        public InteractionState CurrentInteractionState => currentInteractionState;
        public string CurrentOrgan => currentOrgan;
        public string CurrentStructure => currentStructure;
        public long LastCommandTimestamp => lastCommandTimestamp;

        public event Action<string, string> OnSelectionChanged;
        public event Action<InteractionState> OnInteractionStateChanged;

        public void UpdateInteractionState(InteractionState state)
        {
            currentInteractionState = state;
            OnInteractionStateChanged?.Invoke(state);
        }

        public void UpdateSelection(string organ, string structure)
        {
            if (!string.IsNullOrEmpty(organ)) currentOrgan = organ;
            if (!string.IsNullOrEmpty(structure)) currentStructure = structure;
            OnSelectionChanged?.Invoke(currentOrgan, currentStructure);
        }

        public void UpdateLastCommand(string command, long timestamp)
        {
            lastCommand = command;
            lastCommandTimestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
