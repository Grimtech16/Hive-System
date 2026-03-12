using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class HiveNode : MonoBehaviour
{
    public enum Tier { T1, T2, T3 }
    [Header("Tier & Anchor")]
    public Tier tier;
    [Tooltip("Point used as a spawn origin for units.")]
    public Transform spawnAnchor;
    public Transform GetSpawnAnchor() => spawnAnchor != null ? spawnAnchor : transform;

    [Header("Pooling Caps")]
    [Min(1)] public int poolTotalCap = 40;
    [Min(0)] public int capT1 = 28;
    [Min(0)] public int capT2 = 9;
    [Min(0)] public int capT3 = 5;

    [Header("Accumulation")]
    public float accumulationTick = 2.0f;
    public Vector2Int accumulationBatchRange = new Vector2Int(1, 2); // add 1–2 units per tick
    [Range(0f, 0.5f)] public float nodeWeightJitter = 0.15f;

    [Header("LOS Sensing")]
    public float visionRadius = 35f;
    public LayerMask playerDetectMask;          // layers that contain "Player" objects
    public LayerMask obstructionLayers;         // terrain/buildings blocking LOS
    public string playerTag = "Player";
    public float losCheckInterval = 0.25f;
    public float enterLOSGrace = 0.5f;
    public float recallDelay = 10f;

    [Header("Deployment")]
    public int spawnBatchSize = 3;
    public float spawnInterval = 0.4f;
    public int maxSpawnsPerFrame = 5;
    public float spawnRadiusMin = 1.5f, spawnRadiusMax = 3.0f;

    [Header("Recall / Reabsorb")]
    public float reabsorbRadius = 2.0f;
    public float returnTimeout = 15f;

    [Header("Debug")]
    public bool debugLogs;

    // Runtime references
    private HiveCore core;
    private EnemyType t1, t2, t3;

    // Pool (int counters for perf)
    private int poolT1, poolT2, poolT3;

    // Weights per evolution stage (internally jittered per node)
    private Vector3 weightsL0 = new Vector3(1f, 0f, 0f);
    private Vector3 weightsL1 = new Vector3(0.70f, 0.25f, 0.05f);
    private Vector3 weightsL2 = new Vector3(0.55f, 0.30f, 0.15f);
    private Vector3 currentWeights;

    // State
    private bool hasLOS;
    private float losHeldTime;
    private float noLosHeldTime;
    private bool deploying;
    private Coroutine deployCo;
    private float phaseOffset;

    // Active units we spawned
    private readonly HashSet<EnemyUnit> activeUnits = new HashSet<EnemyUnit>();

    public void Initialize(HiveCore core, HiveTierConfig cfg)
    {
        this.core = core;
        t1 = cfg != null ? cfg.tier1Enemy : null;
        t2 = cfg != null ? cfg.tier2Enemy : null;
        t3 = cfg != null ? cfg.tier3Enemy : null;

        // Phase offset so nodes don't sync
        phaseOffset = Random.Range(0f, Mathf.Min(2f, accumulationTick));

        // Set weights based on current evolution
        OnEvolutionChanged(core != null ? core.GetEvolutionLevel() : 0);

        // Start tickers
        StartCoroutine(AccumulateLoop());
        StartCoroutine(LOSLoop());
    }

    public void OnEvolutionChanged(int level)
    {
        Vector3 baseW = level <= 0 ? weightsL0 : (level == 1 ? weightsL1 : weightsL2);

        // Node jitter
        float j = nodeWeightJitter;
        float j1 = 1f + Random.Range(-j, j);
        float j2 = 1f + Random.Range(-j, j);
        float j3 = 1f + Random.Range(-j, j);

        Vector3 w = new Vector3(baseW.x * j1, baseW.y * j2, baseW.z * j3);
        float sum = w.x + w.y + w.z;
        if (sum <= 0f) w = new Vector3(1f, 0f, 0f);
        else w /= sum;

        currentWeights = w;
        if (debugLogs) Debug.Log($"[HiveNode] Weights set L{level}: {currentWeights}");
    }

    // Accumulation
    private IEnumerator AccumulateLoop()
    {
        // Initial random delay (phase offset)
        if (phaseOffset > 0f) yield return new WaitForSeconds(phaseOffset);

        WaitForSeconds wait = new WaitForSeconds(accumulationTick);
        while (true)
        {
            TryAccumulateOnce();
            yield return wait;
        }
    }

    private void TryAccumulateOnce()
    {
        // Respect total cap
        int total = poolT1 + poolT2 + poolT3;
        if (total >= poolTotalCap) return;

        int toAdd = Random.Range(accumulationBatchRange.x, accumulationBatchRange.y + 1);
        for (int i = 0; i < toAdd; i++)
        {
            // Sample tier by weights, fall back if capped/unavailable
            int tierPick = SampleTierIndex();
            if (tierPick == 0 && t1 != null && poolT1 < capT1 && total < poolTotalCap) { poolT1++; total++; }
            else if (tierPick == 1 && t2 != null && poolT2 < capT2 && total < poolTotalCap) { poolT2++; total++; }
            else if (tierPick == 2 && t3 != null && poolT3 < capT3 && total < poolTotalCap) { poolT3++; total++; }
            else
            {
                // Retry within this tick to find a valid tier
                if (t1 != null && poolT1 < capT1 && total < poolTotalCap) { poolT1++; total++; }
                else if (t2 != null && poolT2 < capT2 && total < poolTotalCap) { poolT2++; total++; }
                else if (t3 != null && poolT3 < capT3 && total < poolTotalCap) { poolT3++; total++; }
                else break;
            }
        }

        if (debugLogs) Debug.Log($"[HiveNode] Accumulate -> T1:{poolT1} T2:{poolT2} T3:{poolT3} / {poolTotalCap}");
    }

    private int SampleTierIndex()
    {
        float r = Random.value;
        if (r < currentWeights.x) return 0;
        r -= currentWeights.x;
        if (r < currentWeights.y) return 1;
        return 2;
    }

    // LOS & Deploy/Recall control
    private IEnumerator LOSLoop()
    {
        // desync
        yield return new WaitForSeconds(Random.Range(0f, losCheckInterval));

        WaitForSeconds wait = new WaitForSeconds(losCheckInterval);
        while (true)
        {
            bool losNow = HasLOSToAnyPlayer(out Transform seenTarget);

            if (losNow)
            {
                noLosHeldTime = 0f;
                losHeldTime += losCheckInterval;

                if (!deploying && losHeldTime >= enterLOSGrace)
                {
                    // start deploy
                    deploying = true;
                    if (deployCo != null) StopCoroutine(deployCo);
                    deployCo = StartCoroutine(DeployLoop());
                }
            }
            else
            {
                losHeldTime = 0f;
                if (deploying)
                {
                    noLosHeldTime += losCheckInterval;
                    if (noLosHeldTime >= recallDelay)
                    {
                        // begin recall
                        BeginRecall();
                    }
                }
            }

            hasLOS = losNow;
            yield return wait;
        }
    }

    private bool HasLOSToAnyPlayer(out Transform target)
    {
        target = null;

        // Broad phase: overlap sphere
        Collider[] buffer = Physics.OverlapSphere(transform.position, visionRadius, playerDetectMask, QueryTriggerInteraction.Ignore);
        float bestSqr = float.PositiveInfinity;
        Transform best = null;

        for (int i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (!c) continue;
            if (!string.IsNullOrEmpty(playerTag) && !c.CompareTag(playerTag)) continue;

            float d2 = (c.transform.position - transform.position).sqrMagnitude;
            if (d2 < bestSqr) { bestSqr = d2; best = c.transform; }
        }

        if (best == null) return false;

        Vector3 origin = transform.position + Vector3.up * 0.8f;
        Vector3 dest = best.position + Vector3.up * 0.8f;
        Vector3 dir = dest - origin;
        float dist = dir.magnitude;
        dir /= Mathf.Max(dist, 0.0001f);

        // Narrow phase: raycast vs obstructions
        if (Physics.Raycast(origin, dir, out RaycastHit hit, dist, obstructionLayers, QueryTriggerInteraction.Ignore))
        {
            // blocked
            return false;
        }

        target = best;
        return true;
    }

    // Deployment
    private IEnumerator DeployLoop()
    {
        if (debugLogs) Debug.Log("[HiveNode] DeployLoop START");
        WaitForSeconds wait = new WaitForSeconds(spawnInterval);

        while (hasLOS)
        {
            int spawnedThisFrame = 0;
            int batch = spawnBatchSize;

            for (int i = 0; i < batch; i++)
            {
                if (spawnedThisFrame >= maxSpawnsPerFrame) break;

                EnemyType pick = PickTypeFromPool();
                if (pick == null) break;

                if (TrySpawnUnit(pick, out EnemyUnit u))
                {
                    activeUnits.Add(u);
                    spawnedThisFrame++;
                }
                else
                {
                    // failed to spawn refund the pool
                    RefundPoolForType(pick);
                    break;
                }
            }

            // Stop deploying if nothing left in pool
            if (poolT1 + poolT2 + poolT3 <= 0)
                break;

            yield return wait;
        }

        if (debugLogs) Debug.Log("[HiveNode] DeployLoop END");
        deploying = false;
    }

    private EnemyType PickTypeFromPool()
    {
        // Constrain by actual pool contents; sample like weights but clamp to available
        int available = poolT1 + poolT2 + poolT3;
        if (available <= 0) return null;

        // Simple proportional pick biased by currentWeights but clipped by zero counts
        float w1 = (poolT1 > 0 ? currentWeights.x : 0f);
        float w2 = (poolT2 > 0 ? currentWeights.y : 0f);
        float w3 = (poolT3 > 0 ? currentWeights.z : 0f);
        float sum = w1 + w2 + w3;
        if (sum <= 0f)
        {
            if (poolT1 > 0) { poolT1--; return t1; }
            if (poolT2 > 0) { poolT2--; return t2; }
            if (poolT3 > 0) { poolT3--; return t3; }
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
        Transform anchor = GetSpawnAnchor();
        Vector3 basePos = anchor.position;

        // pick a ring position
        Vector2 ring = Random.insideUnitCircle.normalized * Random.Range(spawnRadiusMin, spawnRadiusMax);
        Vector3 pos = basePos + new Vector3(ring.x, 0f, ring.y);

        if (NavMesh.SamplePosition(pos, out var hit, 1.5f, NavMesh.AllAreas))
            pos = hit.position;

        var go = Instantiate(type.prefab, pos, Quaternion.identity);
        unit = go.GetComponent<EnemyUnit>();
        if (!unit) unit = go.AddComponent<EnemyUnit>();

        // Init with evolution
        int evo = core != null ? core.GetEvolutionLevel() : 0;
        unit.InitFromType(type, evo);

        // Bind to hive (UnitHiveBinding handles return & callbacks)
        TryBindParent(unit, type);

        return true;
    }

    private void TryBindParent(EnemyUnit u, EnemyType type)
    {
        var hook = u.GetComponent<UnitHiveBinding>();
        if (!hook) hook = u.gameObject.AddComponent<UnitHiveBinding>();
        hook.Bind(this, type, reabsorbRadius, returnTimeout);
    }

    // Recall & Reabsorb
    private void BeginRecall()
    {
        if (debugLogs) Debug.Log("[HiveNode] RECALL start");
        // Order units back in small batches to avoid spikes
        StartCoroutine(RecallBatched());
        deploying = false;
        if (deployCo != null) { StopCoroutine(deployCo); deployCo = null; }
    }

    private IEnumerator RecallBatched()
    {
        var iter = activeUnits.GetEnumerator();
        int issued = 0;
        while (iter.MoveNext())
        {
            var u = iter.Current;
            if (u && u.gameObject.activeInHierarchy)
            {
                var binder = u.GetComponent<UnitHiveBinding>();
                if (binder != null)
                    binder.OrderReturn(transform.position);
            }

            issued++;
            if (issued >= 12) { issued = 0; yield return null; } // throttle
        }
    }

    // Called by binder when unit reaches node (or forced despawn)
    public void OnUnitReabsorbed(EnemyUnit u, EnemyType type)
    {
        activeUnits.Remove(u);

        // Try to add back, else recycle (delete)
        if (type == t1 && poolT1 < capT1 && TotalPool() < poolTotalCap) poolT1++;
        else if (type == t2 && poolT2 < capT2 && TotalPool() < poolTotalCap) poolT2++;
        else if (type == t3 && poolT3 < capT3 && TotalPool() < poolTotalCap) poolT3++;
        // else: recycled (do nothing)
    }

    public void OnUnitDied(EnemyUnit u, EnemyType type)
    {
        activeUnits.Remove(u); // no refund
    }

    private int TotalPool() => poolT1 + poolT2 + poolT3;
}