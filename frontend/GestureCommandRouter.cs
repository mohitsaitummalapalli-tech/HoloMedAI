using System;
using UnityEngine;

namespace HoloMedAI.Gestures
{
    public class GestureCommandRouter
    {
        private readonly IAnatomySelectionController anatomyController;
        private readonly IModelManipulationController manipulationController;
        private readonly INavigationController navigationController;
        private readonly IGestureStateManager stateManager;
        private readonly IApplicationStateManager appStateManager;

        private float minimumConfidence = 0.75f;
        private GestureLogLevel logLevel = GestureLogLevel.INFO;

        public float MinimumConfidence
        {
            get => minimumConfidence;
            set => minimumConfidence = Mathf.Clamp01(value);
        }

        public GestureLogLevel LogLevel
        {
            get => logLevel;
            set => logLevel = value;
        }

        public GestureCommandRouter(
            IAnatomySelectionController anatomyCtrl,
            IModelManipulationController manipCtrl,
            INavigationController navCtrl,
            IGestureStateManager stateMgr,
            IApplicationStateManager appStateMgr,
            float minConfidence = 0.75f,
            GestureLogLevel logging = GestureLogLevel.INFO)
        {
            anatomyController = anatomyCtrl;
            manipulationController = manipCtrl;
            navigationController = navCtrl;
            stateManager = stateMgr;
            appStateManager = appStateMgr;
            minimumConfidence = minConfidence;
            logLevel = logging;
        }

        public void RouteCommand(CoreGestureCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.command))
            {
                Log(GestureLogLevel.WARNING, "[Router] Ignored null or empty command packet.");
                return;
            }

            if (command.confidence < minimumConfidence)
            {
                Log(GestureLogLevel.DEBUG, $"[Router] Rejected command '{command.command}' due to low confidence ({command.confidence:F2} < {minimumConfidence:F2}).");
                return;
            }

            string cmdType = command.command.Trim().ToUpperInvariant();
            appStateManager?.UpdateLastCommand(cmdType, command.timestamp);

            switch (cmdType)
            {
                case "SELECT_STRUCTURE":
                case "SELECT":
                case "POINT":
                case "POINT_SELECT":
                case "HIGHLIGHT":
                {
                    string organ = command.payload != null && !string.IsNullOrEmpty(command.payload.organ) ? command.payload.organ : command.organ;
                    string structure = command.payload != null && !string.IsNullOrEmpty(command.payload.structure) ? command.payload.structure : command.structure;

                    if (string.IsNullOrEmpty(organ)) organ = appStateManager?.CurrentOrgan ?? "HEART";

                    Log(GestureLogLevel.INFO, $"[Router] SELECT_STRUCTURE -> Organ: '{organ}', Structure: '{structure}' (Conf: {command.confidence:F2})");
                    anatomyController?.SelectStructure(organ, structure);
                    appStateManager?.UpdateSelection(organ, structure);
                    break;
                }

                case "PINCH_START":
                {
                    Log(GestureLogLevel.DEBUG, "[Router] PINCH_START -> Active manipulation initiated.");
                    stateManager?.HandlePinchStart();
                    appStateManager?.UpdateInteractionState(InteractionState.ACTIVE);
                    break;
                }

                case "PINCH_HOLD":
                {
                    stateManager?.HandlePinchHold();
                    break;
                }

                case "PINCH_RELEASE":
                {
                    Log(GestureLogLevel.DEBUG, "[Router] PINCH_RELEASE -> Manipulation terminated. Rotation halted immediately.");
                    stateManager?.HandlePinchRelease();
                    manipulationController?.StopRotation();
                    appStateManager?.UpdateInteractionState(InteractionState.IDLE);
                    break;
                }

                case "SYS.INPUT.INTERACT":
                case "SYS.INPUT.ROTATE":
                case "INTERACT":
                case "DRAG":
                case "PINCH_DRAG":
                case "ROTATE_MODEL":
                case "ROTATE":
                case "SPIN":
                case "ROTATE_WRIST_CW":
                case "ROTATE_WRIST_CCW":
                case "TWO_HAND_ROTATE_CW":
                case "TWO_HAND_ROTATE_CCW":
                {
                    float dx = command.payload != null && command.payload.dx != 0f ? command.payload.dx : command.dx;
                    float dy = command.payload != null && command.payload.dy != 0f ? command.payload.dy : command.dy;

                    if (cmdType == "ROTATE_WRIST_CW" || cmdType == "TWO_HAND_ROTATE_CW") dx = -0.05f;
                    else if (cmdType == "ROTATE_WRIST_CCW" || cmdType == "TWO_HAND_ROTATE_CCW") dx = 0.05f;

                    GameObject heart = GameObject.Find("Heart");
                    if (heart != null)
                    {
                        float speed = 1.0f;
                        heart.transform.Rotate(Vector3.up, -dx * speed, Space.World);
                        heart.transform.Rotate(Vector3.right, dy * speed, Space.World);
                    }
                    else
                    {
                        manipulationController?.RotateModel(dx, dy);
                    }
                    break;
                }

                case "ZOOM_MODEL":
                case "ZOOM":
                case "TWO-HAND ZOOM":
                case "TWO_HAND_ZOOM":
                case "SCALE":
                case "ZOOM_SPREAD":
                case "ZOOM_CLOSE":
                case "TWO_HAND_SCALE_UP":
                case "TWO_HAND_SCALE_DOWN":
                {
                    float distanceDelta = 0f;
                    if (command.payload != null)
                    {
                        if (command.payload.dz != 0f) distanceDelta = command.payload.dz;
                        else if (command.payload.scale != 0f) distanceDelta = command.payload.scale;
                        else if (command.payload.distanceDelta != 0f) distanceDelta = command.payload.distanceDelta;
                        else if (command.payload.delta != 0f) distanceDelta = command.payload.delta;
                        else if (command.payload.value != 0f) distanceDelta = command.payload.value;
                        else if (command.payload.zoomFactor > 0f) distanceDelta = (command.payload.zoomFactor - 1.0f) * 0.1f;
                    }
                    if (Mathf.Approximately(distanceDelta, 0f))
                    {
                        if (command.dz != 0f) distanceDelta = command.dz;
                        else if (command.scale != 0f) distanceDelta = command.scale;
                        else if (command.delta != 0f) distanceDelta = command.delta;
                        else if (command.distanceDelta != 0f) distanceDelta = command.distanceDelta;
                        else if (command.value != 0f) distanceDelta = command.value;
                    }

                    if (cmdType == "ZOOM_SPREAD" || cmdType == "TWO_HAND_SCALE_UP") distanceDelta = 0.04f;
                    else if (cmdType == "ZOOM_CLOSE" || cmdType == "TWO_HAND_SCALE_DOWN") distanceDelta = -0.04f;

                    manipulationController?.ZoomModel(distanceDelta);
                    break;
                }

                case "MOVE":
                case "PAN":
                case "TRANSLATE":
                case "MOVE_MODEL":
                case "SYS.INPUT.MOVE":
                {
                    float dx = command.payload != null && command.payload.dx != 0f ? command.payload.dx : command.dx;
                    float dy = command.payload != null && command.payload.dy != 0f ? command.payload.dy : command.dy;
                    manipulationController?.MoveModel(dx, dy);
                    break;
                }

                case "NAVIGATE":
                case "SWIPE":
                case "SWIPE_LEFT":
                case "SWIPE_RIGHT":
                case "SWIPE_UP":
                case "SWIPE_DOWN":
                {
                    string dirStr = command.payload != null && !string.IsNullOrEmpty(command.payload.direction) ? command.payload.direction : command.direction;
                    float magnitude = command.payload != null && command.payload.magnitude != 0f ? command.payload.magnitude : 1.0f;

                    if (string.IsNullOrEmpty(dirStr))
                    {
                        if (cmdType.Contains("LEFT")) dirStr = "LEFT";
                        else if (cmdType.Contains("RIGHT")) dirStr = "RIGHT";
                        else if (cmdType.Contains("UP")) dirStr = "UP";
                        else if (cmdType.Contains("DOWN")) dirStr = "DOWN";
                    }

                    NavigationDirection dir = NavigationDirection.UNKNOWN;
                    if (Enum.TryParse<NavigationDirection>(dirStr, true, out NavigationDirection parsedDir))
                    {
                        dir = parsedDir;
                    }

                    Log(GestureLogLevel.INFO, $"[Router] NAVIGATE -> Direction: {dir}, Mag: {magnitude:F2}");
                    navigationController?.Navigate(dir, magnitude);
                    break;
                }

                case "RESET":
                case "EMERGENCY_RESET":
                {
                    Log(GestureLogLevel.INFO, "[Router] RESET command received -> Resetting transform & state.");
                    manipulationController?.ResetTransform();
                    anatomyController?.ClearSelection();
                    stateManager?.ResetState();
                    appStateManager?.UpdateInteractionState(InteractionState.IDLE);
                    break;
                }

                case "PALM_STOP":
                case "FIST_LOCK":
                {
                    Log(GestureLogLevel.DEBUG, "[Router] STOP/LOCK gesture -> Freezing state.");
                    stateManager?.ResetState();
                    manipulationController?.StopRotation();
                    appStateManager?.UpdateInteractionState(InteractionState.IDLE);
                    break;
                }

                default:
                    Log(GestureLogLevel.WARNING, $"[Router] Unknown gesture command: '{cmdType}'. Safely ignored.");
                    break;
            }
        }

        private void Log(GestureLogLevel level, string message)
        {
            if (logLevel == GestureLogLevel.NONE || level > logLevel) return;

            switch (level)
            {
                case GestureLogLevel.ERROR:
                    Debug.LogError($"<color=#FF3366>[HoloMed AI Router]</color> {message}");
                    break;
                case GestureLogLevel.WARNING:
                    Debug.LogWarning($"<color=#FFAA00>[HoloMed AI Router]</color> {message}");
                    break;
                case GestureLogLevel.INFO:
                case GestureLogLevel.DEBUG:
                    Debug.Log($"<color=#00E5FF>[HoloMed AI Router]</color> {message}");
                    break;
            }
        }
    }
}
