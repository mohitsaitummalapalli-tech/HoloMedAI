using System;
using UnityEngine;

/// <summary>
/// HoloMed AI - Gesture Subsystem Interfaces
/// File: IGestureInterfaces.cs
/// 
/// Decouples networking & command routing from application-specific implementations.
/// </summary>
namespace HoloMedAI.Gestures
{
    public interface IAnatomySelectionController
    {
        bool SelectStructure(string organ, string structure);
        void ClearSelection();
    }

    public interface IModelManipulationController
    {
        void RotateModel(float dx, float dy);
        void ZoomModel(float distanceDelta);
        void MoveModel(float dx, float dy);
        void StopRotation();
        void ResetTransform();
    }

    public interface INavigationController
    {
        void Navigate(NavigationDirection direction, float magnitude);
    }

    public interface IApplicationStateManager
    {
        InteractionState CurrentInteractionState { get; }
        string CurrentOrgan { get; }
        string CurrentStructure { get; }
        long LastCommandTimestamp { get; }

        void UpdateInteractionState(InteractionState state);
        void UpdateSelection(string organ, string structure);
        void UpdateLastCommand(string command, long timestamp);
    }

    public interface IGestureStateManager
    {
        InteractionState CurrentState { get; }
        bool IsPinchActive { get; }

        bool TransitionTo(InteractionState targetState);
        void HandlePinchStart();
        void HandlePinchHold();
        void HandlePinchRelease();
        void ResetState();
    }
}
