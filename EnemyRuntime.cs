using UnityEngine;

public class EnemyRuntime : MonoBehaviour
{
    public int health;
    public float damage;

    public void InitFromType(EnemyType type, int evoLevel)
    {
        float hpMult = 1f + type.healthPerLevel * evoLevel;
        float dmgMult = 1f + type.damagePerLevel * evoLevel;

        health = Mathf.RoundToInt(type.baseHealth * hpMult);
        damage = type.baseDamage * dmgMult;

        // Debug for testing
        Debug.Log($"{gameObject.name} init ? HP {health}, Damage {damage}");
    }
}