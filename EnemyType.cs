using UnityEngine;

[CreateAssetMenu(fileName = "EnemyType", menuName = "Hive/Enemy Type")]
public class EnemyType : ScriptableObject
{
    public string id;
    public GameObject prefab;

    [Header("Spawn Settings")]
    [Min(0)] public int perPoolUnit = 1;

    [Header("Evolution Scaling")] // Base stats + mult per evolution level
    public int baseHealth = 100;
    public float baseDamage = 10f;
    public float healthPerLevel = 0.2f;
    public float damagePerLevel = 0.2f;
}