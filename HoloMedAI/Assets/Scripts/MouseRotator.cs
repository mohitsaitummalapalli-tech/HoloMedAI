using UnityEngine;

/// <summary>
/// Rotates the attached GameObject via left-mouse-button drag using
/// legacy Input axes (Input.GetAxis).  Category 1: deterministic
/// mouse interaction only – no touch, no new Input System.
/// </summary>
public class MouseRotator : MonoBehaviour
{
    [Header("Rotation Settings")]
    [Tooltip("Degrees per pixel of mouse movement.")]
    [SerializeField] private float rotationSpeed = 0.4f;

    [Tooltip("Invert the vertical (up/down) rotation axis.")]
    [SerializeField] private bool invertY = false;

    // Cached transform for micro-optimisation.
    private Transform _t;

    private void Awake()
    {
        _t = transform;
    }

    private void Update()
    {
        // Only rotate while the left mouse button is held.
        if (!Input.GetMouseButton(0))
            return;

        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        float yRot = mouseX * rotationSpeed;
        float xRot = mouseY * rotationSpeed * (invertY ? 1f : -1f);

        // Rotate around world-space axes so the model orbits intuitively
        // regardless of its current orientation.
        _t.Rotate(Vector3.up, -yRot, Space.World);
        _t.Rotate(Vector3.right, xRot, Space.World);
    }
}
