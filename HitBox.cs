using UnityEngine;

public class Hitbox : MonoBehaviour, IDamageable
{
    [SerializeField] private Health health;

    void Awake()
    {
        // Auto-grab Health component on parent if not manually assigned
        if (!health) health = GetComponentInParent<Health>();

        if (!health)
            Debug.LogWarning($"[Hitbox] No Health component found in parent of {name}");
    }

    public void TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection)
    {
        if (health)
        {
            health.TakeDamage(amount, hitPoint, hitDirection);
        }
    }
}