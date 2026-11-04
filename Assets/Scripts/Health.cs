using UnityEngine;
using UnityEngine.Events;

public class Health : MonoBehaviour
{
    public float max = 100f;
    public float current;
    public bool Dead => current <= 0f;
    public UnityEvent<float> onDamaged = new UnityEvent<float>();
    public UnityEvent onDied = new UnityEvent();
    void Awake() { current = max; }
    public void Damage(float amount)
    {
        if (Dead) return;
        current = Mathf.Max(0f, current - amount);
        onDamaged.Invoke(amount);
        if (Dead) onDied.Invoke();
    }
}
