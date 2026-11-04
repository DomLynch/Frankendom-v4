using UnityEngine;

// Cool rim light that follows the camera side and only lights the Character layer (8).
public class RimFollow : MonoBehaviour
{
    public Transform cam;
    void LateUpdate()
    {
        if (!cam) return;
        Vector3 dir = -cam.forward + Vector3.up * 0.25f + cam.right * 0.6f;
        transform.rotation = Quaternion.LookRotation(dir.normalized);
    }
}
