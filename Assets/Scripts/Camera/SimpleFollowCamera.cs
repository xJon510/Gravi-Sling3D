using UnityEngine;

public class SimpleFollowCamera : MonoBehaviour
{
    public static SimpleFollowCamera Instance { get; private set; }

    [Header("Target")]
    public Transform target;

    [Tooltip("Optional. If set, we use this RB velocity for look-ahead.")]
    public Rigidbody targetRb;

    [Header("Follow")]
    public bool keepInitialOffset = true;
    public Vector3 manualOffset = new Vector3(0f, 6f, -10f);
    public float positionSmooth = 12f;

    [Header("Look Around (Mouse)")]
    public float mouseSensitivity = 3.5f;

    [Header("Roll (Q/E)")]
    public float rollSpeed = 120f; // degrees per second

    [Header("Velocity Look-Ahead")]
    public bool enableLookAhead = true;

    [Tooltip("Max distance (meters) the camera can shift in the velocity direction.")]
    public float lookAheadMaxDistance = 4.0f;

    [Tooltip("How quickly the look-ahead vector reacts (higher = snappier).")]
    public float lookAheadSharpness = 10f;

    [Tooltip("Ignore tiny velocity jitters under this speed.")]
    public float lookAheadMinSpeed = 0.5f;

    [Tooltip("If > 0, look-ahead scales by speed / this value (clamped 0..1).")]
    public float lookAheadFullAtSpeed = 40f;

    [Header("Impulse Shake")]
    public bool enableShake = true;

    [Tooltip("How quickly shake fades out. Higher = shorter shake.")]
    public float shakeDamping = 18f;

    [Tooltip("Max shake offset (meters).")]
    public float shakeMaxOffset = 0.6f;

    [Header("Stabilization (Optional)")]
    [Tooltip("0 = no auto-level. Higher = camera slowly untwists toward referenceUp.")]
    public float autoLevel = 0f;

    public Vector3 referenceUp = Vector3.up;

    private Vector3 _offset;
    private Vector3 _lookAheadCurrent; // smoothed world-space look-ahead
    private Vector3 _shakeOffset; // world-space or camera-space; we'll use camera-space

    private void Awake()
    {
        if (Instance && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        if (!target) return;

        _offset = keepInitialOffset ? (transform.position - target.position) : manualOffset;

        // If user didn't assign RB, try find one on target or its children.
        if (!targetRb)
        {
            targetRb = target.GetComponentInChildren<Rigidbody>();
            if (!targetRb) targetRb = target.GetComponent<Rigidbody>();
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void LateUpdate()
    {
        if (!target) return;

        float dt = Time.deltaTime;

        // --- Mouse look (keep your current behavior) ---
        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        Vector3 yawAxis = transform.up;
        Vector3 pitchAxis = transform.right;

        transform.Rotate(yawAxis, mx, Space.World);
        transform.Rotate(pitchAxis, -my, Space.World);

        // Manual roll
        float rollInput = 0f;
        if (Input.GetKey(KeyCode.Q)) rollInput += 1f;
        if (Input.GetKey(KeyCode.E)) rollInput -= 1f;

        if (rollInput != 0f)
            transform.Rotate(transform.forward, rollInput * rollSpeed * dt, Space.World);

        // Optional auto-level (very gentle; keep off unless you want it)
        if (autoLevel > 0f)
        {
            // Nudge camera "up" toward referenceUp by removing twist slowly.
            // (simple approach: slerp rotation toward a version that uses referenceUp)
            Vector3 f = transform.forward;
            if (f.sqrMagnitude > 1e-6f)
            {
                Quaternion leveled = Quaternion.LookRotation(f, referenceUp);
                float t = 1f - Mathf.Exp(-autoLevel * dt);
                transform.rotation = Quaternion.Slerp(transform.rotation, leveled, t);
            }
        }

        // --- Velocity look-ahead (world-space) ---
        Vector3 desiredLookAhead = Vector3.zero;

        if (enableLookAhead && targetRb)
        {
            Vector3 v = targetRb.linearVelocity; // matches your usage elsewhere
            float speed = v.magnitude;

            if (speed > lookAheadMinSpeed)
            {
                float speed01 = 1f;
                if (lookAheadFullAtSpeed > 0.0001f)
                    speed01 = Mathf.Clamp01(speed / lookAheadFullAtSpeed);

                desiredLookAhead = v.normalized * (lookAheadMaxDistance * speed01);
            }
        }

        // Smooth the look-ahead so it doesn't jitter
        {
            float t = (lookAheadSharpness <= 0f) ? 1f : (1f - Mathf.Exp(-lookAheadSharpness * dt));
            _lookAheadCurrent = Vector3.Lerp(_lookAheadCurrent, desiredLookAhead, t);
        }

        // --- Shake decay ---
        if (enableShake)
        {
            float k = 1f - Mathf.Exp(-shakeDamping * dt);
            _shakeOffset = Vector3.Lerp(_shakeOffset, Vector3.zero, k);
        }

        // --- Follow position using rotated offset + look-ahead + shakeOffset ---
        Vector3 desiredPos = target.position + (transform.rotation * _offset) + _lookAheadCurrent + _shakeOffset;

        if (positionSmooth <= 0f)
        {
            transform.position = desiredPos;
        }
        else
        {
            float t = 1f - Mathf.Exp(-positionSmooth * dt);
            transform.position = Vector3.Lerp(transform.position, desiredPos, t);
        }
    }

    // ---------------------------
    // Optional “API” for other scripts (nice if you go state-based later)
    // ---------------------------
    public void SetTarget(Transform newTarget, Rigidbody newRb = null)
    {
        target = newTarget;
        targetRb = newRb;

        if (target && keepInitialOffset)
            _offset = (transform.position - target.position);
    }

    public void SetLookAheadEnabled(bool enabled) => enableLookAhead = enabled;

    public void SetLookAheadTuning(float maxDistance, float fullAtSpeed)
    {
        lookAheadMaxDistance = maxDistance;
        lookAheadFullAtSpeed = fullAtSpeed;
    }

    public void ClearLookAhead()
    {
        _lookAheadCurrent = Vector3.zero;
    }

    public void AddShakeImpulse(Vector3 worldDir, float strength)
    {
        if (!enableShake) return;

        // strength expected ~0..1+, clamp it
        float s = Mathf.Clamp01(strength);

        // Directional kick: convert world dir into camera space so it shakes relative to view
        Vector3 dir = (worldDir.sqrMagnitude > 1e-6f) ? worldDir.normalized : Random.onUnitSphere;

        // Slight randomness so it doesn't feel robotic
        Vector3 rand = Random.insideUnitSphere * 0.35f;

        // Shake in camera space (mostly opposite impact direction)
        Vector3 impulseWorld = (dir + rand).normalized * (s * shakeMaxOffset);

        _shakeOffset += impulseWorld;
    }
}
