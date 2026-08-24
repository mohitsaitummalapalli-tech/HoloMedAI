using System;
using UnityEngine;
using UnityEngine.Events;

namespace HoloMedAI.Gestures
{
    /// <summary>
    /// HoloMed AI - Category 1: View & Structure Navigation Controller
    /// File: NavigationController.cs
    /// 
    /// Translates deliberate SWIPE gestures into high-level navigation events:
    /// - Minimum displacement / velocity thresholding
    /// - Cooldown / debounce timer to prevent repeated triggers from a single swipe
    /// - Slow ordinary hand movements are safely ignored
    /// </summary>
    public class NavigationController : MonoBehaviour, INavigationController
    {
        [Header("Navigation Thresholds")]
        [Tooltip("Minimum swipe magnitude required to trigger a navigation event")]
        [SerializeField] private float minimumMagnitude = 0.35f;

        [Tooltip("Cooldown period in seconds to prevent multiple rapid triggers from one swipe motion")]
        [SerializeField] private float swipeCooldown = 0.5f;

        [Header("Unity Events (Inspector Hookups)")]
        public UnityEvent OnNavigateLeftEvent;
        public UnityEvent OnNavigateRightEvent;
        public UnityEvent OnNavigateUpEvent;
        public UnityEvent OnNavigateDownEvent;

        // C# Events for decoupled script subscriptions
        public event Action<NavigationDirection, float> OnNavigate;
        public event Action<float> OnNavigateLeft;
        public event Action<float> OnNavigateRight;

        private float lastSwipeTime = -10f;

        public void Navigate(NavigationDirection direction, float magnitude)
        {
            if (direction == NavigationDirection.UNKNOWN) return;

            // Enforce minimum magnitude
            if (magnitude < minimumMagnitude) return;

            // Enforce cooldown debounce
            if (Time.time - lastSwipeTime < swipeCooldown) return;

            lastSwipeTime = Time.time;
            OnNavigate?.Invoke(direction, magnitude);

            switch (direction)
            {
                case NavigationDirection.LEFT:
                    OnNavigateLeft?.Invoke(magnitude);
                    OnNavigateLeftEvent?.Invoke();
                    break;
                case NavigationDirection.RIGHT:
                    OnNavigateRight?.Invoke(magnitude);
                    OnNavigateRightEvent?.Invoke();
                    break;
                case NavigationDirection.UP:
                    OnNavigateUpEvent?.Invoke();
                    break;
                case NavigationDirection.DOWN:
                    OnNavigateDownEvent?.Invoke();
                    break;
                default:
                    break;
            }
        }
    }
}
