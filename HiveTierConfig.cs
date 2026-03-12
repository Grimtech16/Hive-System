using UnityEngine;

[CreateAssetMenu(fileName = "HiveTierConfig", menuName = "Hive/Tier Config")]
public class HiveTierConfig : ScriptableObject
{
    public EnemyType tier1Enemy; // default
    public EnemyType tier2Enemy; // 5th
    public EnemyType tier3Enemy; // 10th
}