using UnityEngine;

public class SimpleFollowCamera : MonoBehaviour
{
    [Header("Target")]
    public Transform target;

    [Header("Follow")]
    public bool keepInitialOffset = true;
    public Vector3 manualOffset = new Vector3(0f, 6f, -10f);
    public float positionSmooth = 12f;

    [Header("Look Around (Mouse)")]
    public float mouseSensitivity = 3.5f;

    [Header("Roll (Q/E)")]
    public float rollSpeed = 120f; // degrees per second

    [Tooltip("Axis used for yaw. Target up is nice for space/orbit games; world up is more stable if target rolls.")]
    public bool yawAroundTargetUp = true;

    [Header("Stabilization")]
    [Tooltip("0 = no auto-level. Higher = camera slowly untwists toward referenceUp.")]
    public float autoLevel = 6f;

    [Tooltip("Usually Vector3.up. If you want 'space gravity' around a planet, set this to (cameraPos - planetPos).normalized externally.")]
    public Vector3 referenceUp = Vector3.up;

    private Vector3 _offset;
    private Vector3 _smoothVel = Vector3.zero;
    private Vector3 _orbitDir = Vector3.forward;

    private void Start()
    {
        if (!target) return;

        _offset = keepInitialOffset ? (transform.position - target.position) : manualOffset;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void LateUpdate()
    {
        if (!target) return;

        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        // Yaw around the camera's current up (screen-relative), not world up.
        // This prevents "inverted yaw" when upside down.
        Vector3 yawAxis = transform.up;

        // Pitch around the camera's right axis (also screen-relative)
        Vector3 pitchAxis = transform.right;

        // Apply rotations incrementally (world space to avoid local gimbal weirdness)
        transform.Rotate(yawAxis, mx, Space.World);
        transform.Rotate(pitchAxis, -my, Space.World);

        // Follow position using rotated offset
        Vector3 desiredPos = target.position + transform.rotation * _offset;

        if (positionSmooth <= 0f)
            transform.position = desiredPos;
        else
            transform.position = Vector3.Lerp(transform.position, desiredPos, 1f - Mathf.Exp(-positionSmooth * Time.deltaTime)
            );

        float rollInput = 0f;
        if (Input.GetKey(KeyCode.Q)) rollInput += 1f;
        if (Input.GetKey(KeyCode.E)) rollInput -= 1f;

        if (rollInput != 0f)
        {
            // Roll around the camera's forward axis (space-feel roll)
            transform.Rotate(transform.forward, rollInput * rollSpeed * Time.deltaTime, Space.World);
        }
    }
}
