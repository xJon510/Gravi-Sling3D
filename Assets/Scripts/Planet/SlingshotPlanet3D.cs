using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class SlingshotPlanet3D : MonoBehaviour
{
    [Header("Capture / Collision")]
    [Tooltip("If the orbit radius shrinks to this, we consider it a crash (planet surface).")]
    public float planetRadius = 10f;

    [Tooltip("Radius at which the ship orbits when captured.")]
    public float orbitRadius = 18f;

    [Tooltip("How quickly the ship aligns during the smoothing window. Higher = faster.")]
    public float orientationAlignSharpness = 20f;

    [Tooltip("Seconds to ramp from smooth capture to fully locked alignment.")]
    public float alignRampDuration = 0.35f;

    [Tooltip("Curve mapping 0..1 time -> 0..1 lock amount. 0 = fully smooth, 1 = fully locked.")]
    public AnimationCurve alignRamp = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Tooltip("Optional: only capture objects with this tag (leave empty to ignore).")]
    public string requiredTag = "";

    [Header("Orbit / Launch")]
    public float baseOrbitSpeedDegPerSec = 180f;
    public float baseLaunchSpeed = 12f;

    [Tooltip("Extra degrees applied at launch (along the orbit direction).")]
    public float launchAngleOffsetDeg = 0f;

    [Header("Boost (Optional)")]
    public bool enableBoosting = true;

    [Tooltip("Hold Space/LMB to charge. Orbit speed & launch speed increase by this per second.")]
    public float chargeRate = 80f;

    [Tooltip("While charging, orbit radius shrinks toward planetRadius at this speed (units/sec).")]
    public float shrinkRate = 2f;

    [Tooltip("Safety timer for overcharge -> crash.")]
    public float maxChargeTime = 60f;

    [Header("Orbit Speed from Momentum")]
    [Tooltip("Minimum tangential orbit speed on capture (m/s). Helps slow entries feel snappy.")]
    public float minCaptureTangentialSpeed = 35f;

    [Tooltip("Maximum tangential orbit speed on capture (m/s). Prevents crazy fast orbit visuals.")]
    public float maxCaptureTangentialSpeed = 500f;

    [Tooltip("How much of the ship's incoming tangential speed is preserved on capture (0-1).")]
    [Range(0f, 1f)]
    public float captureMomentumPreserve = 0.85f;

    [Header("Ship Orientation")]
    [Tooltip("Which local axis is the ship's 'bottom'? For most models, bottom is -up (Vector3.down).")]
    public Vector3 localShipBottomAxis = new Vector3(0f, -1f, 0f);
    [Tooltip("Which local axis is the ship's 'forward'? For most models, forward is +Z (Vector3.forward).")]
    public Vector3 localShipForwardAxis = new Vector3(0f, 0f, 1f);

    [Tooltip("Extra rotation around the ship's forward axis after alignment (degrees).")]
    public float rollOffsetDeg = 0f;

    [Header("Input")]
    public KeyCode boostKey = KeyCode.Space;
    public int mouseButton = 0; // 0 = left click

    [Header("Orbit Plane Steering (Q/E)")]
    public bool enablePlaneSteering = true;

    [Tooltip("Hold Q/E to rotate the orbit plane (deg/sec). This rotates orbitAxis around radialDir.")]
    public float planeSteerSpeedDegPerSec = 90f;

    public KeyCode planeLeftKey = KeyCode.Q;
    public KeyCode planeRightKey = KeyCode.E;

    [Tooltip("Invert which key rotates the plane direction.")]
    public bool invertPlaneSteer = false;

    private bool orbitInverted; // true = ship TOP faces planet (ship stays upside down)

    private bool isOrbiting;
    public static SlingshotPlanet3D Active;

    private Rigidbody cachedRb;
    private MonoBehaviour cachedMoveScript; // e.g., SimpleMove
    private Vector3 orbitAxis;              // axis normal to the orbit plane
    private Vector3 radialDir;              // unit vector from planet -> ship
    private float currentRadius;
    private float orbitSpeed;
    private float launchSpeed;
    private float chargeTimer;
    private float orbitTangentialSpeed; // units/sec along the arc
    private float orbitStartTime;

    private void Reset()
    {
        // Make sure our own collider is a trigger (common setup for capture volumes)
        var c = GetComponent<Collider>();
        if (c) c.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isOrbiting) return;
        if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag)) return;

        var rb = other.attachedRigidbody;
        if (!rb) return;

        // Optional: only capture the player if it has SimpleMove (or some movement script).
        // If you want to capture anything with an RB, remove this block.
        var move = other.GetComponent<SimpleMove>();
        if (!move) return;

        StartCoroutine(OrbitAndCharge(rb, move));
    }

    private IEnumerator OrbitAndCharge(Rigidbody rb, MonoBehaviour moveScript)
    {
        isOrbiting = true;
        orbitStartTime = Time.time;
        Active = this;

        if (PlayerThrustManager.Instance)
            PlayerThrustManager.Instance.SetOrbiting(0.5f);

        cachedRb = rb;
        cachedMoveScript = moveScript;

        // Disable normal movement while captured.
        if (cachedMoveScript) cachedMoveScript.enabled = false;

        // Seed orbit state.
        Vector3 center = transform.position;

        Vector3 toShip = (rb.position - center);
        if (toShip.sqrMagnitude < 0.0001f)
            toShip = (rb.transform.position - center);

        radialDir = toShip.normalized;
        currentRadius = orbitRadius;

        // Decide whether to keep the ship inverted to minimize snap.
        // Compare which ship axis is already closer to pointing at the planet (i.e., along -radialDir).
        Vector3 desiredToPlanet = (-radialDir).normalized;

        Vector3 bottomWorld = rb.transform.TransformDirection(localShipBottomAxis).normalized;
        Vector3 topWorld = -bottomWorld;

        float bottomDot = Vector3.Dot(bottomWorld, desiredToPlanet);
        float topDot = Vector3.Dot(topWorld, desiredToPlanet);

        // If the ship's "top" is already closer to the planet, keep it inverted during this orbit.
        orbitInverted = topDot > bottomDot;

        orbitSpeed = baseOrbitSpeedDegPerSec;
        launchSpeed = baseLaunchSpeed;
        chargeTimer = 0f;

        // Convert initial angular speed -> tangential speed (units/sec)
        orbitTangentialSpeed = Mathf.Abs(orbitSpeed) * Mathf.Deg2Rad * currentRadius;

        // Define an orbit plane based on entry velocity.
        // orbitAxis = radial x velocity (normal to the plane of motion)
        Vector3 v = rb.linearVelocity;
        if (v.sqrMagnitude < 0.01f)
        {
            // Fallback if we entered nearly stopped.
            orbitAxis = Vector3.up;
        }
        else
        {
            orbitAxis = Vector3.Cross(radialDir, v.normalized);
            if (orbitAxis.sqrMagnitude < 0.0001f)
                orbitAxis = Vector3.up;
            else
                orbitAxis.Normalize();
        }

        // Incoming speed (full magnitude, not just tangential)
        float incomingSpeed = v.magnitude;

        // Optional preserve (set to 1 if you truly want "whatever it is")
        float preserved = incomingSpeed * captureMomentumPreserve;

        // Clamp (optional) — remove clamp if you really want raw
        orbitTangentialSpeed = Mathf.Clamp(
            Mathf.Max(preserved, minCaptureTangentialSpeed),
            minCaptureTangentialSpeed,
            maxCaptureTangentialSpeed
        );

        // Convert to orbitSpeed degrees/sec for your existing system.
        orbitSpeed = (orbitTangentialSpeed / Mathf.Max(currentRadius, 0.001f)) * Mathf.Rad2Deg;

        // Optional: immediately show current entry speed before we zero it
        if (SpeedHUD.Instance)
            SpeedHUD.Instance.SetSpeed(rb.linearVelocity.magnitude);

        // Stop physics drift while we manually drive.
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // “Swallow” any held button by requiring a fresh press to start charging (if boosting enabled)
        bool wasHeldOnCapture = IsBoostHeld();
        bool charging = false;

        while (true)
        {
            bool held = IsBoostHeld();

            if (enableBoosting)
            {
                // Require a fresh press after capture.
                if (!wasHeldOnCapture && held) charging = true;
                if (wasHeldOnCapture && !held) wasHeldOnCapture = false;

                if (charging && held)
                {
                    float dt = Time.fixedDeltaTime;

                    // Shrink radius
                    currentRadius = Mathf.MoveTowards(currentRadius, planetRadius, shrinkRate * dt);

                    orbitTangentialSpeed += chargeRate * dt;

                    launchSpeed = Mathf.Max(baseLaunchSpeed, orbitTangentialSpeed);

                    chargeTimer += dt;
                    if (chargeTimer >= maxChargeTime || currentRadius <= planetRadius)
                    {
                        rb.linearVelocity = Vector3.zero;
                        if (cachedMoveScript) cachedMoveScript.enabled = false;

                        isOrbiting = false;
                        if (Active == this) Active = null;

                        if (SpeedHUD.Instance)
                            SpeedHUD.Instance.SetSpeed(0f);

                        yield break;
                    }

                    if (PlayerThrustManager.Instance)
                    {
                        float charge01 = Mathf.Clamp01(chargeTimer / 2.0f);
                        PlayerThrustManager.Instance.SetCharging(charge01);
                    }
                }

                // Release to launch (only after we've started charging at least once)
                if (charging && !held)
                    break;
            }
            else
            {
                // No boosting: just wait for a click/press, then launch immediately.
                if (!wasHeldOnCapture && held) break;
                if (wasHeldOnCapture && !held) wasHeldOnCapture = false;
            }

            // If we're orbiting but not currently charging this tick, fade to idle-orbit visuals.
            if (PlayerThrustManager.Instance)
            {
                bool chargingThisTick = enableBoosting && charging && held;
                if (!chargingThisTick)
                    PlayerThrustManager.Instance.SetOrbiting(0.5f);
            }

            // --- Plane steering: rotate the orbit "slice" around the current radial direction ---
            if (enablePlaneSteering)
            {
                bool left = Input.GetKey(planeLeftKey);
                bool right = Input.GetKey(planeRightKey);

                if (left ^ right) // one or the other (not both)
                {
                    float dir = right ? 1f : -1f;
                    if (invertPlaneSteer) dir = -dir;

                    float planeDelta = dir * planeSteerSpeedDegPerSec * Time.fixedDeltaTime;

                    // Rotate the orbit plane normal around radialDir.
                    orbitAxis = Quaternion.AngleAxis(planeDelta, radialDir) * orbitAxis;
                    orbitAxis.Normalize();

                    // (Optional safety) keep it perfectly perpendicular to radialDir.
                    // This helps fight tiny drift over long sessions.
                    orbitAxis = Vector3.ProjectOnPlane(orbitAxis, radialDir).normalized;
                }
            }

            orbitSpeed = (orbitTangentialSpeed / Mathf.Max(currentRadius, 0.001f)) * Mathf.Rad2Deg;

            // Advance along orbit by rotating radialDir around orbitAxis.
            float deltaDeg = orbitSpeed * Time.fixedDeltaTime;
            radialDir = Quaternion.AngleAxis(deltaDeg, orbitAxis) * radialDir;

            Vector3 targetPos = center + (radialDir * currentRadius);
            rb.MovePosition(targetPos);

            // HUD speed during orbit: tangential speed along the arc (units/sec)
            if (SpeedHUD.Instance)
                SpeedHUD.Instance.SetSpeed(orbitTangentialSpeed);

            // Orientation: ship "bottom" points toward the planet (i.e., along -radialDir).
            Vector3 tangent2 = Vector3.Cross(orbitAxis, radialDir).normalized;
            Vector3 desiredBottomDir = orbitInverted ? radialDir : -radialDir; // inverted flips which side faces planet
            float u = Mathf.Clamp01((Time.time - orbitStartTime) / Mathf.Max(alignRampDuration, 0.001f));
            float lock01 = alignRamp.Evaluate(u); // 0..1
            AlignShipToPlanet(rb.transform, desiredBottomDir, tangent2, orbitAxis, lock01);

            yield return new WaitForFixedUpdate();
        }

        // Launch tangent to the orbit.
        // Tangent direction = orbitAxis x radialDir (right-hand rule)
        Vector3 tangent = Vector3.Cross(orbitAxis, radialDir).normalized;

        // Optional extra angle offset at launch (rotate tangent around orbitAxis)
        if (Mathf.Abs(launchAngleOffsetDeg) > 0.001f)
            tangent = Quaternion.AngleAxis(launchAngleOffsetDeg, orbitAxis) * tangent;

        if (PlayerThrustManager.Instance)
            PlayerThrustManager.Instance.OnLaunch();

        rb.linearVelocity = tangent * launchSpeed;

        if (SpeedHUD.Instance)
            SpeedHUD.Instance.SetSpeed(rb.linearVelocity.magnitude);

        // Re-enable movement.
        if (cachedMoveScript) cachedMoveScript.enabled = true;

        if (PlayerThrustManager.Instance)
            PlayerThrustManager.Instance.ResetToIdle();

        isOrbiting = false;
        if (Active == this) Active = null;
        cachedRb = null;
        cachedMoveScript = null;
    }

    private bool IsBoostHeld()
    {
        return Input.GetKey(boostKey) || Input.GetMouseButton(mouseButton);
    }

    private void AlignShipToPlanet(
        Transform ship,
        Vector3 desiredBottomWorldDir,
        Vector3 desiredForwardWorldDir,
        Vector3 orbitAxisWorld,
        float lock01
    )
    {
        desiredBottomWorldDir = desiredBottomWorldDir.normalized;
        desiredForwardWorldDir = desiredForwardWorldDir.normalized;

        if (desiredBottomWorldDir.sqrMagnitude < 0.0001f || desiredForwardWorldDir.sqrMagnitude < 0.0001f)
            return;

        // --- Step 1: align bottom axis ---
        Vector3 currentBottomWorld = ship.TransformDirection(localShipBottomAxis).normalized;
        if (currentBottomWorld.sqrMagnitude < 0.0001f) return;

        Quaternion toBottom = Quaternion.FromToRotation(currentBottomWorld, desiredBottomWorldDir);
        Quaternion rotAfterBottom = toBottom * ship.rotation;

        // --- Step 2: twist around bottom so forward faces orbit direction ---
        Vector3 forwardAfterBottom = (rotAfterBottom * localShipForwardAxis).normalized;

        // Project both forwards onto the plane perpendicular to bottom
        Vector3 fA = Vector3.ProjectOnPlane(forwardAfterBottom, desiredBottomWorldDir);
        Vector3 fD = Vector3.ProjectOnPlane(desiredForwardWorldDir, desiredBottomWorldDir);

        if (fA.sqrMagnitude > 0.0001f && fD.sqrMagnitude > 0.0001f)
        {
            fA.Normalize();
            fD.Normalize();

            // This rotation will effectively be around desiredBottomWorldDir
            Quaternion toForward = Quaternion.FromToRotation(fA, fD);
            rotAfterBottom = toForward * rotAfterBottom;
        }

        // Optional roll tweak (choose axis you like; orbitAxis feels stable)
        if (Mathf.Abs(rollOffsetDeg) > 0.001f)
            rotAfterBottom = Quaternion.AngleAxis(rollOffsetDeg, orbitAxisWorld) * rotAfterBottom;

        // Smooth step (slerp) amount this frame
        float smoothT = (orientationAlignSharpness <= 0f) ? 1f
            : (1f - Mathf.Exp(-orientationAlignSharpness * Time.fixedDeltaTime));

        Quaternion smoothRot = Quaternion.Slerp(ship.rotation, rotAfterBottom, smoothT);

        // lock01=0 => fully smoothRot
        // lock01=1 => fully rotAfterBottom (snapped/locked)
        ship.rotation = Quaternion.Slerp(smoothRot, rotAfterBottom, lock01);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, orbitRadius);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, planetRadius);
    }
}
