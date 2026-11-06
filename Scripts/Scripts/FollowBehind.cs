using UnityEngine;

// Red rim from the furnace doorway: a point light kept 1.2 m behind the target's spine at head height, characters-only (layer 8).
public class FollowBehind : MonoBehaviour
{
    public Transform target; public float back = 1.2f, up = 2.2f;
    void LateUpdate() { if (target) transform.position = target.position - target.forward * back + Vector3.up * up; }
}
