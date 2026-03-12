using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class Rupture : MonoBehaviour
{
    [Header("Lifecycle")]
    [Tooltip("Seconds to telegraph before spawning begins.")]
    public float telegraphTime = 2.0f;
    [Tooltip("Seconds the rupture will actively spawn enemies.")]
    public float activeLifetime = 30.0f;

    [Header("Spawn Cadence")]
    [Tooltip("How many enemies per burst.")]
    public int spawnBatchSize = 4;
    [Tooltip("Time between bursts (seconds).")]
    public float spawnInterval = 0.35f;
    [Tooltip("Hard limit per frame to avoid spikes.")]
    public int maxSpawnsPerFrame = 8;

    [Header("Spawn Area (around this object)")]
    public float spawnRadiusMin = 1.5f;
    public float spawnRadiusMax = 3.0f;

    [Header("Composition Caps (per rupture)")]
    [Min(0)] public int capT1 = 24;
    [Min(0)] public int capT2 = 8;
    [Min(0)] public int capT3 = 3;

    [Header("Tier Bias (availability-based)")]
    [Range(0f, 1f)] public float jitter = 0.15f; // per-instance weight jitter
    // L0 / L1 / L2 default weights (T1,T2,T3)
    public Vector3 weightsL0 = new Vector3(1.00f, 0.00f, 0.00f);
    public Vector3 weightsL1 = new Vector3(0.70f, 0.25f, 0.05f);
    public Vector3 weightsL2 = new Vector3(0.55f, 0.30f, 0.15f);

    [Header("Handoff to Hive on Expiry")]
    [Tooltip("Max distance to look for a HiveNode when the rupture ends.")]
    public float nodeHandoffSearchRadius = 60f;
    [Tooltip("Reabsorb distance used once units reach the node.")]
    public float reabsorbRadius = 2.0f;
    [Tooltip("How long a returning unit will try to get back before auto-despawn.")]
    public float returnTimeout = 10.0f;

    [Header("References (optional)")]
    [Tooltip("If not set, we will find any HiveCore in scene and read its tierConfig.")]
    public HiveTierConfig tierConfigOverride;

    [Header("Debug")]
    public bool debugLogs = false;

    // runtime
    private HiveOvermind overmind;
    private EnemyType t1, t2, t3;
    private Vector3 weights;              // final (jittered) weights in use
    private int poolT1, poolT2, poolT3;   // remaining counts to spawn
    private readonly List<EnemyUnit> spawnedUnits = new List<EnemyUnit>();
    private readonly List<EnemyType> spawnedTypes = new List<EnemyType>();
    private bool running;

    // Entry point called by Overmind
    public void Begin(HiveOvermind om)
    {
        overmind = om;
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        running = true;

        // Resolve EnemyTypes (T1/T2/T3)
        ResolveTierConfig();

        // Fill local pool to caps
        poolT1 = capT1;
        poolT2 = t2 ? capT2 : 0;
        poolT3 = t3 ? capT3 : 0;

        // Choose weights from availability
        Vector3 baseW = (t3 ? weightsL2 : (t2 ? weightsL1 : weightsL0));
        weights = ApplyJitterAndNormalize(baseW, jitter);

        // Telegraph phase
        if (telegraphTime > 0f)
            yield return new WaitForSeconds(telegraphTime);

        // Active spawn phase
        float endTime = Time.time + activeLifetime;
        var wait = new WaitForSeconds(spawnInterval);

        while (Time.time < endTime && (poolT1 + poolT2 + poolT3) > 0)
        {
            int spawnedThisFrame = 0;
            for (int i = 0; i < spawnBatchSize; i++)
            {
                if (spawnedThisFrame >= maxSpawnsPerFrame) break;

                var pick = PickTypeFromPool();
                if (pick == null) break;

                if (TrySpawnUnit(pick, out var unit))
                {
                    spawnedUnits.Add(unit);
                    spawnedTypes.Add(pick);
                    spawnedThisFrame++;
                }
                else
                {
                    // refund if spawn failed
                    RefundPoolForType(pick);
                    break;
                }
            }
            yield return wait;
        }

        // Expiry: hand off survivors to nearest node if available else recycle
        HandoffOrRecycleSurvivors();

        running = false;
        Destroy(gameObject); // rupture collapse
    }

    // Setup & helpers

    private void ResolveTierConfig()
    {
        var cfg = tierConfigOverride;
        if (!cfg)
        {
            var core = FindObjectOfType<HiveCore>();
            if (core) cfg = core.tierConfig;
        }

        if (cfg)
        {
            t1 = cfg.tier1Enemy;
            t2 = cfg.tier2Enemy;
            t3 = cfg.tier3Enemy;
        }
        else
        {
            LogWarn("[Rupture] No HiveTierConfig available. Only T1 spawns if set manually.");
        }
    }

    private Vector3 ApplyJitterAndNormalize(Vector3 w, float j)
    {
        float j1 = 1f + Random.Range(-j, j);
        float j2 = 1f + Random.Range(-j, j);
        float j3 = 1f + Random.Range(-j, j);
        var v = new Vector3(w.x * j1, w.y * j2, w.z * j3);
        float s = v.x + v.y + v.z;
        if (s <= 0f) return new Vector3(1f, 0f, 0f);
        return v / s;
    }

    private EnemyType PickTypeFromPool()
    {
        int total = poolT1 + poolT2 + poolT3;
        if (total <= 0) return null;

        float w1 = (poolT1 > 0 && t1) ? weights.x : 0f;
        float w2 = (poolT2 > 0 && t2) ? weights.y : 0f;
        float w3 = (poolT3 > 0 && t3) ? weights.z : 0f;
        float sum = w1 + w2 + w3;
        if (sum <= 0f)
        {
            if (poolT1 > 0 && t1) { poolT1--; return t1; }
            if (poolT2 > 0 && t2) { poolT2--; return t2; }
            if (poolT3 > 0 && t3) { poolT3--; return t3; }
            return null;
        }

        float r = Random.value * sum;
        if (r < w1) { poolT1--; return t1; }
        r -= w1;
        if (r < w2) { poolT2--; return t2; }
        poolT3--; return t3;
    }

    private void RefundPoolForType(EnemyType type)
    {
        if (type == t1) poolT1++;
        else if (type == t2) poolT2++;
        else if (type == t3) poolT3++;
    }

    private bool TrySpawnUnit(EnemyType type, out EnemyUnit unit)
    {
        unit = null;

        // ring spawn around rupture
        Vector2 ring = Random.insideUnitCircle.normalized * Random.Range(spawnRadiusMin, spawnRadiusMax);
        Vector3 pos = transform.position + new Vector3(ring.x, 0f, ring.y);

        if (NavMesh.SamplePosition(pos, out var hit, 1.5f, NavMesh.AllAreas))
            pos = hit.position;

        var go = Instantiate(type.prefab, pos, Quaternion.identity);
        unit = go.GetComponent<EnemyUnit>();
        if (!unit) unit = go.AddComponent<EnemyUnit>();

        // Init stats (use availability-derived stage)
        unit.InitFromType(type, GetCurrentEvoLevel());

        // Apply Overmind global buff to Health + damage
        var om = HiveOvermind.Instance;
        if (om != null)
        {
            float mult = om.GetGlobalBuffMult();

            // Buff health via Health component
            var health = unit.GetComponent<Health>();
            if (health != null)
            {
                health.maxHealth = Mathf.RoundToInt(health.maxHealth * mult);
                health.currentHealth = health.maxHealth;
            }

            // Buff damage on EnemyUnit
            unit.damage *= mult;
        }

        // Push initial destination toward the player base (harass)
        if (overmind && overmind.playerBase)
        {
            Vector3 basePos = overmind.playerBase.position;
            if (NavMesh.SamplePosition(basePos, out var baseHit, 2.0f, NavMesh.AllAreas))
                basePos = baseHit.position;

            var nav = unit.GetComponent<NavMeshAgent>();
            if (nav && nav.isOnNavMesh)
            {
                nav.isStopped = false;
                nav.SetDestination(basePos);
            }
        }

        return true;
    }

    private int GetCurrentEvoLevel()
    {
        // Mapping by availability
        return t3 ? 2 : (t2 ? 1 : 0);
    }

    private void HandoffOrRecycleSurvivors()
    {
        // Clean nulls and keep types aligned
        for (int i = spawnedUnits.Count - 1; i >= 0; i--)
        {
            if (!spawnedUnits[i])
            {
                spawnedUnits.RemoveAt(i);
                spawnedTypes.RemoveAt(i);
            }
        }
        if (spawnedUnits.Count == 0) return;

        // Find nearest node once
        HiveNode nearest = FindNearestNode(transform.position, nodeHandoffSearchRadius);

        for (int i = 0; i < spawnedUnits.Count; i++)
        {
            var u = spawnedUnits[i];
            var type = spawnedTypes[i];
            if (!u) continue;

            var hook = u.GetComponent<UnitHiveBinding>();
            if (!hook) hook = u.gameObject.AddComponent<UnitHiveBinding>();

            if (nearest)
            {
                // Bind to a node so later recalls / reabsorbs work,
                hook.Bind(nearest, type, reabsorbRadius, returnTimeout);
            }
            else
            {
                // Default AI if no node nearby, no auto clear
            }
        }
    }

    private HiveNode FindNearestNode(Vector3 pos, float maxDist)
    {
        HiveNode best = null;
        float bestSqr = maxDist * maxDist;

        var nodes = FindObjectsOfType<HiveNode>();
        for (int i = 0; i < nodes.Length; i++)
        {
            float d2 = (nodes[i].transform.position - pos).sqrMagnitude;
            if (d2 < bestSqr) { bestSqr = d2; best = nodes[i]; }
        }
        return best;
    }

    private void OnDestroy()
    {
        running = false;
    }

    private void Log(string msg) { if (debugLogs) Debug.Log(msg); }
    private void LogWarn(string msg) { if (debugLogs) Debug.LogWarning(msg); }
}