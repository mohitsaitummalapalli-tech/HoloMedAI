using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Gesture State Machine
    /// File: GestureStateManager.cs
    /// 
    /// Manages interaction state lifecycle:
    /// IDLE -> CANDIDATE -> CONFIRMED -> ACTIVE -> COMPLETE -> IDLE
    /// Prevents repeated or contradictory state transitions.
    /// </summary>
    public class GestureStateManager : MonoBehaviour, IGestureStateManager
    {
        [Header("State Machine Monitor")]
        [SerializeField] private InteractionState currentState = InteractionState.IDLE;
        [SerializeField] private bool isPinchActive = false;

        public InteractionState CurrentState => currentState;
        public bool IsPinchActive => isPinchActive;

        public event Action<InteractionState, InteractionState> OnStateChanged;

        public bool TransitionTo(InteractionState targetState)
        {
            if (currentState == targetState) return true;

            // Enforce valid lifecycle transitions
            bool isValid = false;
            switch (currentState)
            {
                case InteractionState.IDLE:
                    isValid = (targetState == InteractionState.CANDIDATE || targetState == InteractionState.CONFIRMED || targetState == InteractionState.ACTIVE);
                    break;
                case InteractionState.CANDIDATE:
                    isValid = (targetState == InteractionState.CONFIRMED || targetState == InteractionState.IDLE);
                    break;
                case InteractionState.CONFIRMED:
                    isValid = (targetState == InteractionState.ACTIVE || targetState == InteractionState.IDLE);
                    break;
                case InteractionState.ACTIVE:
                    isValid = (targetState == InteractionState.COMPLETE || targetState == InteractionState.IDLE);
                    break;
                case InteractionState.COMPLETE:
                    isValid = (targetState == InteractionState.IDLE || targetState == InteractionState.CANDIDATE);
                    break;
            }

            if (isValid)
            {
                InteractionState previousState = currentState;
                currentState = targetState;
                isPinchActive = (currentState == InteractionState.ACTIVE);
                OnStateChanged?.Invoke(previousState, currentState);
                return true;
            }

            return false;
        }

        public void HandlePinchStart()
        {
            // Transition through CONFIRMED to ACTIVE
            TransitionTo(InteractionState.CONFIRMED);
            TransitionTo(InteractionState.ACTIVE);
            isPinchActive = true;
        }

        public void HandlePinchHold()
        {
            if (currentState != InteractionState.ACTIVE)
            {
                TransitionTo(InteractionState.ACTIVE);
            }
            isPinchActive = true;
        }

        public void HandlePinchRelease()
        {
            TransitionTo(InteractionState.COMPLETE);
            TransitionTo(InteractionState.IDLE);
            isPinchActive = false;
        }

        public void ResetState()
        {
            InteractionState prev = currentState;
            currentState = InteractionState.IDLE;
            isPinchActive = false;
            OnStateChanged?.Invoke(prev, currentState);
        }
    }
}
