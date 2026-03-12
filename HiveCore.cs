using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class HiveCore : MonoBehaviour
{
    [Header("Bio Mass")]
    [Min(0)] public float baseBioMassPerSecond = 1f;
    public float biomass;

    [Header("Node Spawning")]
    public GameObject hiveNodePrefab;
    public HiveTierConfig tierConfig;
    public float nodeMinRadius = 8f;
    public float nodeMaxRadius = 16f;
    public int nodePlacementTries = 12;
    public LayerMask nodeObstacles;
    public float nodeClearanceRadius = 2f;

    [Header("Spread")]
    public float baseMinRadius = 8f;
    public float baseMaxRadius = 16f;
    public float growthPerNode = 1.5f;

    [Header("Evolution")]
    [SerializeField] private int unitEvolutionLevel = 0;

    [Header("Costs")]
    public int baseNodeCost = 20;
    public float nodeCostGrowth = 1.25f;

    public int baseEvolutionCost = 200;
    public float evolutionCostGrowth = 1.3f;

    [Header("Phase / Gating")]
    public int criticalMassNodeCount = 30;
    public float minNodeSpawnCooldown = 10f;
    public float minEvolutionCooldown = 15f;

    [Header("Hive Tyrant")]
    public GameObject hiveTyrantPrefab;
    public bool spawnTyrantOnDeath = true;

    [SerializeField] private bool debugLogs = true;

    private float nodeCooldown, evolutionCooldown;
    private int CurrentNodeCost() => Mathf.RoundToInt(baseNodeCost * Mathf.Pow(nodeCostGrowth, nodes.Count));
    private int CurrentEvolutionCost() => Mathf.RoundToInt(baseEvolutionCost * Mathf.Pow(evolutionCostGrowth, unitEvolutionLevel));
    private bool ReachedCriticalMass => nodes.Count >= criticalMassNodeCount;

    // Runtime
    private readonly List<HiveNode> nodes = new();

    // Public API for nodes
    public int GetEvolutionLevel() => unitEvolutionLevel;
    public event Action<int> OnEvolutionChanged;
    // In HiveCore.cs
    private void OnEnable() { if (HiveOvermind.Instance) HiveOvermind.Instance.RegisterCore(this); }
    private void OnDisable() { if (HiveOvermind.Instance) HiveOvermind.Instance.UnregisterCore(this); }

    void Start()
    {
        var d = SimulationDifficulty.Instance;
        if (d != null)
        {
            baseBioMassPerSecond *= d.hiveGrowthMult;
            minNodeSpawnCooldown /= d.hiveGrowthMult;

            baseNodeCost = Mathf.RoundToInt(baseNodeCost / d.hiveGrowthMult);
        }
    }
    void Update()
    {
        var om = HiveOvermind.Instance;

        // 1) Income ? add to global pool
        float income = baseBioMassPerSecond + nodes.Count;
        if (om) om.AddIncome(income * Time.deltaTime);

        // Mirror - Inspection Only
        if (om) biomass = om.GetGlobalBiomass();

        // Cooldowns
        if (nodeCooldown > 0f) nodeCooldown -= Time.deltaTime;
        if (evolutionCooldown > 0f) evolutionCooldown -= Time.deltaTime;

        // Spend decision
        bool purchased = false;

        if (ReachedCriticalMass)
        {
            if (om) om.NotifyCoreCriticalMass(this);

            const float postCriticalNodeChance = 0.50f;
            if (!purchased && nodeCooldown <= 0f && om && om.GetGlobalBiomass() >= CurrentNodeCost() && UnityEngine.Random.value < postCriticalNodeChance)
            {
                BuyNode();
                purchased = true;
            }
        }
        else
        {
            if (!purchased && nodeCooldown <= 0f && om && om.GetGlobalBiomass() >= CurrentNodeCost())
            {
                BuyNode();
                purchased = true;
            }
        }
    }
    private void BuyNode()
    {
        var om = HiveOvermind.Instance;
        if (!om) return;

        int cost = CurrentNodeCost();
        float before = om.GetGlobalBiomass();

        if (!om.TrySpendGlobal(cost))
        {
            if (debugLogs) Debug.LogWarning($"[Hive:{name}] Not enough GLOBAL biomass. Need {cost}, have {before:F1}");
            return;
        }

        if (!TrySpawnNode(out HiveNode node))
        {
            om.RefundGlobal(cost);
            if (debugLogs) Debug.LogWarning($"[Hive:{name}] Node placement failed (refunded {cost}). Global={om.GetGlobalBiomass():F1}");
            return;
        }

        RegisterNode(node);
        nodeCooldown = minNodeSpawnCooldown;

        if (debugLogs) Debug.Log($"[Hive:{name}] Bought NODE for {cost}. Global {before:F1} ? {om.GetGlobalBiomass():F1}. Nodes={nodes.Count}");
    }

    private void BuyEvolution()
    {
        int cost = CurrentEvolutionCost();
        biomass -= cost;
        unitEvolutionLevel++;
        evolutionCooldown = minEvolutionCooldown;

        OnEvolutionChanged?.Invoke(unitEvolutionLevel);

        if (debugLogs) Debug.Log($"[Hive] Bought EVOLUTION L{unitEvolutionLevel} for {cost}. Biomass={biomass:F1}");
    }

    private void RegisterNode(HiveNode node)
    {
        nodes.Add(node);

        int n = nodes.Count;
        if (n % 10 == 0) node.tier = HiveNode.Tier.T3;
        else if (n % 5 == 0) node.tier = HiveNode.Tier.T2;
        else node.tier = HiveNode.Tier.T1;

        // Initialize node with refs + subscrition to evolution
        node.Initialize(this, tierConfig);
        OnEvolutionChanged += node.OnEvolutionChanged;
    }

    private bool TrySpawnNode(out HiveNode node)
    {
        node = null;

        for (int i = 0; i < nodePlacementTries; i++)
        {
            float minR = baseMinRadius + nodes.Count * growthPerNode;
            float maxR = baseMaxRadius + nodes.Count * growthPerNode;

            float r = UnityEngine.Random.Range(minR, maxR);
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            Vector3 offset = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
            Vector3 candidate = transform.position + offset;

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, nodeClearanceRadius, NavMesh.AllAreas))
            {
                Vector3 pos = hit.position;

                if (Physics.CheckSphere(pos, nodeClearanceRadius, nodeObstacles))
                    continue;

                bool tooClose = false;
                foreach (var existing in nodes)
                {
                    if (Vector3.Distance(pos, existing.transform.position) < nodeClearanceRadius * 2f)
                    {
                        tooClose = true; break;
                    }
                }
                if (tooClose) continue;

                var go = Instantiate(hiveNodePrefab, pos, Quaternion.identity);
                node = go.GetComponent<HiveNode>();
                if (node == null) node = go.AddComponent<HiveNode>();
                return true;
            }
        }

        if (debugLogs) Debug.LogWarning("[Hive] Failed to place node (NavMesh blocked)");
        return false;
    }
    private void SpawnHiveTyrant()
    {
        if (!spawnTyrantOnDeath || !hiveTyrantPrefab)
            return;

        var om = HiveOvermind.Instance;
        if (!om || !om.playerBase)
            return;

        Vector3 spawnPos = transform.position;

        // Snap to NavMesh
        if (NavMesh.SamplePosition(spawnPos, out var hit, 3f, NavMesh.AllAreas))
            spawnPos = hit.position;

        var go = Instantiate(hiveTyrantPrefab, spawnPos, Quaternion.identity);

        // Apply GLOBAL HIVE BUFF
        float mult = om.GetGlobalBuffMult();

        // Health component owns health
        var health = go.GetComponent<Health>();
        if (health != null)
        {
            health.maxHealth = Mathf.RoundToInt(health.maxHealth * mult);
            health.currentHealth = health.maxHealth;
        }

        // EnemyUnit owns damage
        var unit = go.GetComponent<EnemyUnit>();
        if (unit != null)
        {
            unit.damage *= mult;

            // Tyrants should NEVER be recalled or pooled
            unit.SetExternalControl(false);
        }

        // Force target = player base (harasser behaviour)
        var agent = go.GetComponent<NavMeshAgent>();
        if (agent && agent.isOnNavMesh)
        {
            agent.isStopped = false;
            agent.SetDestination(om.playerBase.position);
        }

        if (debugLogs)
            Debug.Log("[Hive] Hive Tyrant spawned at destroyed core.");
    }

    private void OnDestroy()
    {
        // Real death vs Unload check
        if (Application.isPlaying)
            SpawnHiveTyrant();
    }
}