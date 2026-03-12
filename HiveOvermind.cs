using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;

public class HiveOvermind : MonoBehaviour
{
    // -------- Singleton --------
    public static HiveOvermind Instance { get; private set; }

    private void Awake()
    {
        if (Instance && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // Global Buff
    [Header("Global Buff")]
    [Tooltip("Starts at 1.0; each branch multiplies by (1 + GlobalBuffStep).")]
    [Min(0.5f)] public float globalBuffMult = 1.0f;
    [Range(0f, 1f)] public float globalBuffStep = 0.10f; // Global Mult Increase Over Time
    public System.Action<float> OnGlobalBuffChanged; // Global Mult Action

    public float GetGlobalBuffMult() => globalBuffMult;

    [Header("Global Biomass")]
    public float globalBiomass = 0f;
    public System.Action<float> OnGlobalBiomassChanged;


    public void ApplyGlobalBuffStep(float stepPct = -1f)
    {
        float step = stepPct >= 0f ? stepPct : globalBuffStep;
        globalBuffMult *= (1f + Mathf.Max(0f, step));
        OnGlobalBuffChanged?.Invoke(globalBuffMult);
#if UNITY_EDITOR
        if (debugLogs) Debug.Log($"[Overmind] Global buff applied. ×{globalBuffMult:0.00}");
#endif
    }

    // Core Registry & Branching
    [Header("Cores & Branching")]
    [Tooltip("Max number of active HiveCores allowed.")]
    [Min(1)] public int maxCores = 4;
    [Tooltip("Minimum distance between cores when branching.")]
    public float minCoreSpacing = 55f;
    [Tooltip("Minimum distance from the player base when placing a new core.")]
    public float minDistanceFromBase = 60f;
    [Tooltip("Maximum distance from the player base when placing a new core.")]
    public float maxDistanceFromBase = 140f;
    [Tooltip("Placement tries when sampling the NavMesh for a branch core.")]
    public int branchPlacementTries = 24;
    [Tooltip("Clearance radius to avoid obstacles/overlaps for core placement.")]
    public float coreClearanceRadius = 2.0f;
    public LayerMask coreObstacleMask;

    [Header("Branch Rate Limiting")]
    [Tooltip("Minimum seconds between any two branch spawns globally.")]
    public float branchGlobalCooldown = 60f;
    [Tooltip("Minimum seconds before the same core can branch again.")]
    public float perCoreBranchCooldown = 90f;

    // runtime
    private float nextGlobalBranchTime = 0f;
    private readonly Dictionary<HiveCore, float> nextCoreBranchReady = new();

    [Tooltip("Optional: prefab to instantiate for a new HiveCore (must have HiveCore). If null, will Instantiate the same prefab as existing core if possible.")]
    public GameObject hiveCorePrefabOverride;

    [Tooltip("Reference to the player's base (used for branch & rupture placement).")]
    public Transform playerBase;
    private readonly List<HiveCore> cores = new();

    public void RegisterCore(HiveCore core)
    {
        if (!core || cores.Contains(core)) return;
        cores.Add(core);
        if (!nextCoreBranchReady.ContainsKey(core)) nextCoreBranchReady[core] = 0f;
#if UNITY_EDITOR
        if (debugLogs) Debug.Log($"[Overmind] Core registered. Total={cores.Count}");
#endif
    }

    public void UnregisterCore(HiveCore core)
    {
        if (!core) return;
        cores.Remove(core);
        nextCoreBranchReady.Remove(core);
#if UNITY_EDITOR
        if (debugLogs) Debug.Log($"[Overmind] Core unregistered. Total={cores.Count}");
        if (cores.Count == 0)
        {
            Log("[Overmind] All HiveCores destroyed. Hive defeated.");
            HandleHiveDefeated();
        }
#endif
    }

    // Called by a HiveCore that just reached critical mass.
    // Attempts to spawn a new core elsewhere and applies a global buff.

    public bool NotifyCoreCriticalMass(HiveCore parent)
    {
        if (!parent) return false;

        // global cooldown
        if (Time.time < nextGlobalBranchTime)
        {
            Log("[Overmind] Branch ignored: global cooldown.");
            return false;
        }

        // per-core cooldown
        if (nextCoreBranchReady.TryGetValue(parent, out float tReady) && Time.time < tReady)
        {
            Log("[Overmind] Branch ignored: core cooldown.");
            return false;
        }

        bool branched = TrySpawnBranch(parent);
        if (branched)
        {
            ApplyGlobalBuffStep();
            nextGlobalBranchTime = Time.time + branchGlobalCooldown;
            nextCoreBranchReady[parent] = Time.time + perCoreBranchCooldown;
        }
        return branched;
    }

    // Try to place a new HiveCore on the NavMesh, respecting spacing and base distance.
    public bool TrySpawnBranch(HiveCore parent)
    {
        if (cores.Count >= maxCores) { Log("[Overmind] Branch blocked: at max cores."); return false; }
        if (!playerBase) { LogWarning("[Overmind] No playerBase assigned; cannot branch safely."); return false; }

        // Sample around the player base within [min, max] ring but away from other cores
        for (int i = 0; i < branchPlacementTries; i++)
        {
            Vector3 pos = SampleAnnulus(playerBase.position, minDistanceFromBase, maxDistanceFromBase);
            if (!NavMesh.SamplePosition(pos, out var hit, coreClearanceRadius, NavMesh.AllAreas))
                continue;

            Vector3 candidate = hit.position;

            // Obstacle/clearance check
            if (Physics.CheckSphere(candidate, coreClearanceRadius, coreObstacleMask))
                continue;

            // Spacing vs existing core check
            bool tooClose = false;
            for (int c = 0; c < cores.Count; c++)
            {
                if ((cores[c].transform.position - candidate).sqrMagnitude < (minCoreSpacing * minCoreSpacing))
                {
                    tooClose = true; break;
                }
            }
            if (tooClose) continue;

            // Instantiate a new core
            GameObject prefab = hiveCorePrefabOverride;
            if (!prefab && cores.Count > 0)
            {
                // Try to use the same prefab as the first core if it was instantiated from one
                var first = cores[0].gameObject;
#if UNITY_EDITOR
                var src = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(first);
                if (src) prefab = src as GameObject;
#endif
            }

            HiveCore newCore = null;
            if (prefab)
            {
                var go = Instantiate(prefab, candidate, Quaternion.identity);
                newCore = go.GetComponent<HiveCore>() ?? go.AddComponent<HiveCore>();
            }
            else
            {
                // Fallback: blank GO + HiveCore
                var go = new GameObject("HiveCore_Branch");
                go.transform.position = candidate;
                newCore = go.AddComponent<HiveCore>();
            }

            // Register and give it a small starting biomass
            RegisterCore(newCore);
            newCore.enabled = true;
            newCore.SendMessage("OnValidate", SendMessageOptions.DontRequireReceiver);
            newCore.biomass = Mathf.Max(newCore.biomass, 50f);

            Log($"[Overmind] Branched new core at {candidate} (cores={cores.Count}).");
            return true;
        }

        LogWarning("[Overmind] Failed to place a branch core after tries.");
        return false;
    }

    // Ruptures
    [Header("Ruptures (Optional)")]
    public bool enableRuptures = false;
    public GameObject rupturePrefab;
    public int maxConcurrentRuptures = 2;
    public Vector2 ruptureIntervalRange = new Vector2(60f, 100f);
    public Vector2 ruptureAnnulusFromBase = new Vector2(25f, 45f);
    public int rupturePlacementTries = 16;

    // How many seconds of warning you want
    [Tooltip("How many seconds before a rupture wave spawns the warning is shown.")]
    public float ruptureWarningLeadTime = 30f;

    // Warning event: (plannedCount, etaSeconds)
    public System.Action<int, float> OnRuptureWaveScheduled;

    private readonly List<GameObject> liveRuptures = new();
    private Coroutine ruptureCo;

    void Start()
    {
        var d = SimulationDifficulty.Instance;
        if (d != null)
        {
            branchGlobalCooldown /= d.branchingMult;
            perCoreBranchCooldown /= d.branchingMult;
            globalBuffStep *= d.branchingMult;

            switch (d.ruptureMode)
            {
                case RuptureMode.Off:
                    enableRuptures = false;
                    break;

                case RuptureMode.Low:
                    enableRuptures = true;
                    ruptureIntervalRange = new Vector2(80f, 130f);
                    maxConcurrentRuptures = 1;
                    break;

                case RuptureMode.Normal:
                    enableRuptures = true;
                    break;

                case RuptureMode.High:
                    enableRuptures = true;
                    ruptureIntervalRange = new Vector2(40f, 70f);
                    maxConcurrentRuptures = 2;
                    break;
            }
        }
    }

    private void OnEnable()
    {
        if (enableRuptures && ruptureCo == null)
            ruptureCo = StartCoroutine(RuptureLoop());
    }

    private void OnDisable()
    {
        if (ruptureCo != null) { StopCoroutine(ruptureCo); ruptureCo = null; }
    }

    private IEnumerator RuptureLoop()
    {
        var waitSmall = new WaitForSeconds(0.5f);

        while (true)
        {
            // Clean dead entries
            for (int i = liveRuptures.Count - 1; i >= 0; i--)
            {
                if (!liveRuptures[i]) liveRuptures.RemoveAt(i);
            }

            if (!enableRuptures || !playerBase || !rupturePrefab)
            {
                yield return waitSmall;
                continue;
            }

            int availableSlots = maxConcurrentRuptures - liveRuptures.Count;
            if (availableSlots <= 0)
            {
                yield return waitSmall;
                continue;
            }

            // Full delay until the next wave actually spawns
            float fullDelay = Random.Range(ruptureIntervalRange.x, ruptureIntervalRange.y);
            int plannedCount = Random.Range(1, availableSlots + 1);

            float lead = Mathf.Max(0f, ruptureWarningLeadTime);
            bool warned = false;
            float timeLeft = fullDelay;

            // Count down to spawn
            while (timeLeft > 0f)
            {
                if (!enableRuptures)
                    break;

                if (!warned && timeLeft <= lead)
                {
                    float eta = timeLeft;
                    Log($"[Overmind] Scheduling rupture wave: {plannedCount} in ~{eta:F1}s");
                    OnRuptureWaveScheduled?.Invoke(plannedCount, eta);
                    warned = true;
                }

                timeLeft -= 0.5f;
                yield return waitSmall;
            }

            if (!enableRuptures)
                continue;

            // Spawn Ruptures
            for (int i = 0; i < plannedCount; i++)
            {
                int slotsLeft = maxConcurrentRuptures - liveRuptures.Count;
                if (slotsLeft <= 0) break;

                if (!TryFindRuptureSpot(out Vector3 pos))
                    break;

                var go = Instantiate(rupturePrefab, pos, Quaternion.identity);
                liveRuptures.Add(go);

                var rupture = go.GetComponent<Rupture>();
                if (rupture)
                    rupture.Begin(this);

                Log($"[Overmind] Rupture spawned @ {pos} (active={liveRuptures.Count})");
            }

            yield return waitSmall;
        }
    }


    private bool TryFindRuptureSpot(out Vector3 pos)
    {
        pos = default;
        if (!playerBase) return false;

        for (int i = 0; i < rupturePlacementTries; i++)
        {
            Vector3 candidate = SampleAnnulus(playerBase.position, ruptureAnnulusFromBase.x, ruptureAnnulusFromBase.y);
            if (!NavMesh.SamplePosition(candidate, out var hit, 2.0f, NavMesh.AllAreas)) continue;

            // Obstacle Detection
            if (coreObstacleMask.value != 0 && Physics.CheckSphere(hit.position, 1.0f, coreObstacleMask))
                continue;

            pos = hit.position;
            return true;
        }
        return false;
    }
    //Global Biomass
    public void AddIncome(float amount)
    {
        if (amount <= 0f) return;
        globalBiomass += amount;
        OnGlobalBiomassChanged?.Invoke(globalBiomass);
    }
    public bool TrySpendGlobal(float amount)
    {
        if (globalBiomass + 1e-4f < amount) return false;
        globalBiomass -= amount;
        OnGlobalBiomassChanged?.Invoke(globalBiomass);
        return true;
    }
    public void RefundGlobal(float amount)
    {
        if (amount <= 0f) return;
        globalBiomass += amount;
        OnGlobalBiomassChanged?.Invoke(globalBiomass);
    }
    public float GetGlobalBiomass() => globalBiomass;

    //WinCondition
    public System.Action OnHiveDefeated;

    private void HandleHiveDefeated()
    {
        // Stop ruptures
        enableRuptures = false;
        if (ruptureCo != null) { StopCoroutine(ruptureCo); ruptureCo = null; }
        for (int i = liveRuptures.Count - 1; i >= 0; i--)
        {
            if (liveRuptures[i]) Destroy(liveRuptures[i]);
        }
        liveRuptures.Clear();

        OnHiveDefeated?.Invoke();

        // Small delay to let any listeners react
        StartCoroutine(DestroyNextFrame());
    }
    private System.Collections.IEnumerator DestroyNextFrame()
    {
        yield return null;
        Destroy(gameObject);
        Debug.Log("All Hive Cores Destroyed... Victory");
    }

    //Helpers & Debug
    private Vector3 SampleAnnulus(Vector3 center, float rMin, float rMax)
    {
        float r = Random.Range(rMin, rMax);
        float ang = Random.Range(0f, Mathf.PI * 2f);
        return center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
    }

    [Header("Debugging")]
    public bool debugLogs = true;
    private void Log(string msg) { if (debugLogs) Debug.Log(msg); }
    private void LogWarning(string msg) { if (debugLogs) Debug.LogWarning(msg); }
}