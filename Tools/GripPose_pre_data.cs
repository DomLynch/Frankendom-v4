using UnityEngine;

// Closes the weapon hand: curls the right-hand finger chain on top of the animator pose (LateUpdate, after animation) so the haft reads as gripped.
public class GripPose : MonoBehaviour
{
    public float fingerDeg = 62f, thumbDeg = 35f;
    Transform[][] chains; Quaternion[][] rest; bool init;
    void Init()
    {
        init = true;
        string[] fingers = { "Index", "Middle", "Ring", "Pinky" };
        var all = GetComponentsInChildren<Transform>(true);
        chains = new Transform[5][]; rest = new Quaternion[5][];
        for (int f = 0; f < 5; f++)
        {
            string nm = f < 4 ? fingers[f] : "Thumb"; var list = new System.Collections.Generic.List<Transform>();
            for (int j = 1; j <= 3; j++) foreach (var t in all) if (t.name == "mixamorig:RightHand" + nm + j) { list.Add(t); break; }
            chains[f] = list.ToArray();
        }
    }
    void LateUpdate()
    {
        if (!init) Init();
        for (int f = 0; f < 5; f++)
            foreach (var t in chains[f])
            {
                float a = f < 4 ? fingerDeg : thumbDeg;
                t.localRotation = t.localRotation * Quaternion.AngleAxis(a * (f < 4 ? 1f : 0.7f), f < 4 ? Vector3.forward : Vector3.up);
            }
    }
}
