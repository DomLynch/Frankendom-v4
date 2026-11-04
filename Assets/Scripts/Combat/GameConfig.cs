using UnityEngine;

// Single tunables for the slice. pitbornHeight drives the Pitborn root scale (native 2.1 m); everything that depends on his size reads DuelFighter.scale.
public class GameConfig : ScriptableObject
{
    public float pitbornHeight = 2.5f;
    public int difficulty = 5;
    public const float PitbornNativeHeight = 2.1f;
    public float PitbornScale => pitbornHeight / PitbornNativeHeight;
}



