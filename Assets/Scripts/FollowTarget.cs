using UnityEngine;

public class FollowTarget : MonoBehaviour
{
    [SerializeField] private GameObject target;
    [SerializeField] private Vector3 offset = new Vector3(0, 2, -5);

    void LateUpdate()
    {
        if (!target) return;
        transform.position = target.transform.position + offset;
    }
}
