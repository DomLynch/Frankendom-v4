using UnityEngine;

// Single tunables for the slice. PERF3 v100: the dead pitborn* members (pitbornHeight, PitbornNativeHeight, PitbornScale) are gone, nothing read them (character size is roster.json heightM / DuelFighter.scale).
public class GameConfig : ScriptableObject
{
    public int difficulty = 5;
}



