using UnityEngine;

public class FollowLookAhead : MonoBehaviour
{
    [Header("Follow")]
    [SerializeField] private Transform target;
    [SerializeField] private Rigidbody targetRb;
    [SerializeField] private Vector3 offset = new Vector3(0, 2, -5);

    [Header("Look-Ahead Offset")]
    [Tooltip("Max distance the FX is pushed forward in velocity direction.")]
    [SerializeField] private float lookAheadMaxDistance = 4f;

    [Header("Rotation From Velocity")]
    [Tooltip("Ignore tiny speeds to prevent jitter.")]
    [SerializeField] private float minSpeedForRotation = 0.5f;

    [Tooltip("How quickly the rotation follows velocity. 0 = snap instantly.")]
    [SerializeField] private float rotationSharpness = 12f;

    private void Start()
    {
        if (!targetRb && target)
        {
            targetRb = target.GetComponentInChildren<Rigidbody>();
            if (!targetRb) targetRb = target.GetComponent<Rigidbody>();
        }
    }

    private void LateUpdate()
    {
        if (!target || !targetRb) return;

        Vector3 v = targetRb.linearVelocity;
        // if this errors, use: targetRb.velocity

        float speed = v.magnitude;

        // --- look-ahead offset (position only) ---
        Vector3 lookAhead = Vector3.zero;
        if (speed > minSpeedForRotation)
        {
            lookAhead = v.normalized * lookAheadMaxDistance;
        }

        // --- position follow ---
        transform.position = target.position + offset + lookAhead;

        // --- rotation from velocity ---
        if (speed < minSpeedForRotation) return;

        Quaternion desiredRot = Quaternion.LookRotation(v.normalized, transform.up);

        if (rotationSharpness <= 0f)
        {
            transform.rotation = desiredRot;
        }
        else
        {
            float t = 1f - Mathf.Exp(-rotationSharpness * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, t);
        }
    }
}
