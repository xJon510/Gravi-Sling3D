using UnityEngine;

public class SimpleMove : MonoBehaviour
{
    [Header("References")]
    public Rigidbody rb; // Assign your CHILD rigidbody here in the inspector.

    [Tooltip("Optional. If null, uses Camera.main.")]
    public Transform cameraTransform;

    [Header("Rigidbody Movement (Inertia)")]
    public float maxSpeed = 8f;
    public float acceleration = 25f;
    public float damping = 2f;                 // lower = more drift
    public bool normalizeInput = true;

    [Header("Rotation")]
    public bool enableRotation = true;
    public float rotationSpeed = 12f;          // higher = snappier
    public float minSpeedToRotate = 0.15f;     // ignore tiny velocity jitters

    [Tooltip("Base orientation to make your mesh face the 'right' way before steering is applied.")]
    public Vector3 baseEulerOffset = Vector3.zero;

    [Header("Manual Roll (Q/E)")]
    public float manualRollSpeed = 120f; // degrees per second
    public float _manualRoll;           // accumulated roll angle

    [Header("Style / Flair")]
    [Tooltip("Roll/bank amount while steering (degrees). This does NOT change direction; it's just style.")]
    public float bankDegrees = 20f;

    [Tooltip("How strongly bank responds to turning/side input.")]
    public float bankResponse = 10f;

    [Tooltip("Optional little wobble roll while moving (degrees). Set to 0 to disable.")]
    public float thrustWobbleDegrees = 5f;

    [Tooltip("Wobble speed (Hz-ish).")]
    public float thrustWobbleSpeed = 6f;

    private float _bank;
    private float _wobbleT;

    private void Awake()
    {
        if (!rb)
        {
            Debug.LogError("SimpleMove: Assign the child Rigidbody in the inspector.");
            enabled = false;
            return;
        }

        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (!cameraTransform && Camera.main)
            cameraTransform = Camera.main.transform;
    }

    private void FixedUpdate()
    {
        if (!cameraTransform && Camera.main)
            cameraTransform = Camera.main.transform;

        // --- INPUT (WASD + Space/Ctrl) ---
        float h = Input.GetAxisRaw("Horizontal"); // A/D
        float v = Input.GetAxisRaw("Vertical");   // W/S

        float ud = 0f;
        if (Input.GetKey(KeyCode.Space)) ud += 1f;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ud -= 1f;

        Vector3 input = new Vector3(h, ud, v);

        if (normalizeInput && input.sqrMagnitude > 1f)
            input.Normalize();

        float roll = 0f;
        if (Input.GetKey(KeyCode.Q)) roll += 1f;
        if (Input.GetKey(KeyCode.E)) roll -= 1f;

        // accumulate roll over time (so it "sticks" like space roll)
        _manualRoll += roll * manualRollSpeed * Time.fixedDeltaTime;

        // --- BUILD CAMERA-RELATIVE MOVE DIR ---
        Vector3 desiredVel = Vector3.zero;

        if (cameraTransform)
        {
            // Use camera axes; keep them orthonormal and stable
            Vector3 camF = cameraTransform.forward;
            Vector3 camR = cameraTransform.right;
            Vector3 camU = cameraTransform.up;

            // Camera-relative intent:
            // x = strafe, y = up/down, z = forward/back
            Vector3 dir =
                camR * input.x +
                camU * input.y +
                camF * input.z;

            if (dir.sqrMagnitude > 0.0001f)
                dir.Normalize();

            desiredVel = dir * maxSpeed;
        }
        else
        {
            // Fallback: world-relative
            Vector3 dir = new Vector3(input.x, input.y, input.z);
            if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
            desiredVel = dir * maxSpeed;
        }

        // --- MOVE (INERTIA) ---
        Vector3 currentVel = rb.linearVelocity;
        Vector3 newVel = Vector3.MoveTowards(currentVel, desiredVel, acceleration * Time.fixedDeltaTime);

        // Drift damping when no input
        if (input.sqrMagnitude < 0.0001f)
        {
            float dampFactor = Mathf.Exp(-damping * Time.fixedDeltaTime);
            newVel *= dampFactor;
        }

        rb.linearVelocity = newVel;

        // --- ROTATION (keep existing flow, but make facing 3D + camera-relative) ---
        if (enableRotation)
        {
            // Consider us "wanting rotation" if we are moving OR rolling OR have some velocity
            bool hasMoveInput = input.sqrMagnitude > 0.0001f;
            bool hasVel = rb.linearVelocity.sqrMagnitude > (minSpeedToRotate * minSpeedToRotate);
            bool hasManualRollIntent = Mathf.Abs(_manualRoll) > 0.001f; // stored roll (or you can use roll key input)

            if (hasMoveInput || hasVel || hasManualRollIntent)
            {
                // Face direction preference:
                // 1) If player is inputting movement, face desiredVel (intent)
                // 2) else keep current forward (so roll still works while stationary)
                Vector3 faceDir;
                if (hasMoveInput && desiredVel.sqrMagnitude > 0.0001f)
                    faceDir = desiredVel.normalized;
                else
                    faceDir = rb.rotation * Vector3.forward;

                Quaternion target = ComputeFacing3D(faceDir);

                // If no movement input, don't inject bank (otherwise you’ll bank back to 0 weirdly)
                Vector3 bankInput = hasMoveInput ? input : Vector3.zero;

                Quaternion styled = ApplyBankAndWobble(target, bankInput, rb.linearVelocity);

                // Apply manual roll around the ship's forward axis AFTER styling (always)
                Vector3 shipForward = styled * Vector3.forward;
                Quaternion manualRollRot = Quaternion.AngleAxis(_manualRoll, shipForward);
                styled = manualRollRot * styled;

                float t = 1f - Mathf.Exp(-rotationSpeed * Time.fixedDeltaTime);
                rb.MoveRotation(Quaternion.Slerp(rb.rotation, styled, t));
            }
        }
    }

    private Quaternion ComputeFacing3D(Vector3 dir)
    {
        // Base orientation to correct mesh forward axis
        Quaternion baseRot = Quaternion.Euler(baseEulerOffset);

        // Use camera up if available (helps keep roll stable with a chase cam),
        // otherwise fall back to world up.
        Vector3 up = cameraTransform ? cameraTransform.up : Vector3.up;

        // Guard against degenerate look rotations
        if (dir.sqrMagnitude < 0.000001f)
            return rb.rotation;

        Quaternion steer = Quaternion.LookRotation(dir, up);
        return steer * baseRot;
    }

    private Quaternion ApplyBankAndWobble(Quaternion facing, Vector3 input, Vector3 vel)
    {
        // Bank based on strafe + forward (and a *little* on vertical)
        float targetBank = 0f;

        // Right/left = roll main driver
        targetBank += -input.x * bankDegrees;

        // Forward/back adds a little flair
        targetBank += -input.z * (bankDegrees * 0.25f);

        // Vertical adds subtle flair (optional but usually feels nice)
        targetBank += -input.y * (bankDegrees * 0.15f);

        float bankT = 1f - Mathf.Exp(-bankResponse * Time.fixedDeltaTime);
        _bank = Mathf.Lerp(_bank, targetBank, bankT);

        // Thrust wobble only while moving
        float wobble = 0f;
        if (thrustWobbleDegrees > 0f && vel.magnitude > minSpeedToRotate)
        {
            _wobbleT += Time.fixedDeltaTime * thrustWobbleSpeed;
            wobble = Mathf.Sin(_wobbleT) * thrustWobbleDegrees;
        }

        // Apply roll around the ship's forward axis in its faced orientation
        Vector3 forwardAxis = facing * Vector3.forward;
        Quaternion roll = Quaternion.AngleAxis(_bank + wobble, forwardAxis);

        return roll * facing;
    }
}
