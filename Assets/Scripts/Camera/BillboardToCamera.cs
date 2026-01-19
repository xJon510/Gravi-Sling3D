using UnityEngine;

public class BillboardToCamera : MonoBehaviour
{
    private Camera cam;

    void Awake()
    {
        cam = Camera.main;
    }

    void LateUpdate()
    {
        if (!cam) return;

        // Make the quad face the camera
        transform.forward = cam.transform.forward;
    }
}
