using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class EnemyUnit : MonoBehaviour
{
    [Header("Targeting")]
    [SerializeField] private LayerMask targetLayerMask;   // Target Layer - Player and Building
    [SerializeField] private string[] targetTags = new[] { "Player", "Structure", "Building" };
    [SerializeField] private float targetSearchInterval = 0.5f;
    [SerializeField] private float targetSearchRadius = 40f;

    [Header("Chase")]
    [SerializeField] private float pathRefreshInterval = 0.4f;
    [SerializeField] private float repathMoveThreshold = 0.75f;

    [Header("Combat")]
    [SerializeField] private float attackRange = 1.6f;
    [SerializeField] private float attackCooldown = 1.0f;
    [SerializeField] private LayerMask meleeTargetMask;   // Attack Layer Mask - Building and Player
    [SerializeField] private string meleeTargetTag = "Player";
    [SerializeField] private bool meleeAOE = false;

    [Header("NavMesh")]
    [SerializeField] private float navSampleRadius = 6.0f;
    [SerializeField] private int agentTypeId = -1;

    [Header("Return")]
    [SerializeField] private float noTargetReturnDelay = 8f; // Time delay before Return

    [Header("Debug")]
    [SerializeField] private bool debugLogs = false;

    // Runtime stats
    public float damage;
    private Health health;

    private EnemyType enemyType;
    private int evoLevel;

    // Runtime
    private NavMeshAgent agent;
    private Transform target;
    private float nextTargetScan;
    private float nextPathRefresh;
    private Vector3 lastSetDestination;

    // External control - recall/return
    public bool ExternalControl { get; private set; }

    // Self-return helpers
    private float noTargetTimer;
    private bool selfReturnTriggered;
    private UnitHiveBinding hiveBinding;

    // Trigger-based melee runtime
    private SphereCollider meleeTrigger;
    private readonly HashSet<Collider> contacts = new HashSet<Collider>();
    private readonly Dictionary<int, float> nextHitTime = new Dictionary<int, float>();

    // Init from EnemyType
    public void InitFromType(EnemyType type, int evolutionLevel)
    {
        enemyType = type;

        float hpMult = 1f + type.healthPerLevel * evolutionLevel;
        float dmgMult = 1f + type.damagePerLevel * evolutionLevel;

        int newHP = Mathf.RoundToInt(type.baseHealth * hpMult);
        damage = type.baseDamage * dmgMult;

        if (!health) health = GetComponent<Health>();
        if (health != null)
        {
            health.maxHealth = newHP;
            health.currentHealth = newHP;
        }
    }

    private void Awake()
    {
        health = GetComponent<Health>();
        agent = GetComponent<NavMeshAgent>() ?? gameObject.AddComponent<NavMeshAgent>();
        if (agentTypeId >= 0) agent.agentTypeID = agentTypeId;
        agent.autoRepath = true;
        agent.autoTraverseOffMeshLink = true;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.autoBraking = false;
        agent.stoppingDistance = Mathf.Max(agent.stoppingDistance, attackRange * 0.8f);

        meleeTrigger = GetComponent<SphereCollider>();
        if (!meleeTrigger) meleeTrigger = gameObject.AddComponent<SphereCollider>();
        meleeTrigger.isTrigger = true;
        meleeTrigger.radius = attackRange;

        var rb = GetComponent<Rigidbody>();
        if (!rb) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true; rb.useGravity = false;
        rb.constraints |= RigidbodyConstraints.FreezeRotation;

        TrySnapToNavMesh();
        nextTargetScan = 0f;
        nextPathRefresh = 0f;

        // Unit to HiveNode binding - For recall/return
        hiveBinding = GetComponent<UnitHiveBinding>();
    }

    private void OnEnable()
    {
        contacts.Clear();
        nextHitTime.Clear();
        noTargetTimer = 0f;
        selfReturnTriggered = false;
    }

    private void Update()
    {
        // If recalling, still scan for targets and cancel if a target returns
        if (ExternalControl)
        {
            if (!agent || !agent.isOnNavMesh) TrySnapToNavMesh();

            if (hiveBinding != null && hiveBinding.IsReturning && Time.time >= nextTargetScan)
            {
                nextTargetScan = Time.time + targetSearchInterval;

                var reacquire = FindNearestTarget();
                if (reacquire != null)
                {
                    // Cancel recall and resume combat AI
                    hiveBinding.CancelReturn(); // must set ExternalControl(false)
                    target = reacquire;
                    noTargetTimer = 0f;
                    selfReturnTriggered = false;

                    SetDestinationTo(target.position);
                    nextPathRefresh = Time.time + pathRefreshInterval;
                }
            }
            return; // still in external control unless we canceled above
        }

        if (!agent || !agent.isOnNavMesh) { TrySnapToNavMesh(); return; }

        // Keep trigger radius in sync
        if (meleeTrigger && Mathf.Abs(meleeTrigger.radius - attackRange) > 0.001f)
            meleeTrigger.radius = attackRange;

        // Ensure binding if it was added after spawn
        if (hiveBinding == null) hiveBinding = GetComponent<UnitHiveBinding>();

        // Acquire/Re-acquire target
        if (Time.time >= nextTargetScan || target == null || !target.gameObject.activeInHierarchy)
        {
            nextTargetScan = Time.time + targetSearchInterval;

            var newTarget = FindNearestTarget();
            if (newTarget != null)
            {
                target = newTarget;
                noTargetTimer = 0f;
                selfReturnTriggered = false;

                SetDestinationTo(target.position);
                nextPathRefresh = Time.time + pathRefreshInterval;
            }
            else
            {
                // No targets: idle countdown ? self-return
                noTargetTimer += targetSearchInterval;
                if (!selfReturnTriggered && noTargetTimer >= noTargetReturnDelay && hiveBinding != null)
                {
                    selfReturnTriggered = true;
                    hiveBinding.OrderReturnToParent();
                }
                return;
            }
        }

        // Chase and Repath
        if (target)
        {
            if (Time.time >= nextPathRefresh ||
                (target.position - lastSetDestination).sqrMagnitude >= repathMoveThreshold * repathMoveThreshold)
            {
                SetDestinationTo(target.position);
                nextPathRefresh = Time.time + pathRefreshInterval;
            }

            if (!agent.pathPending && !agent.hasPath)
                SetDestinationTo(target.position);
        }
    }

    public void SetExternalControl(bool enabled)
    {
        ExternalControl = enabled;
        if (enabled)
        {
            target = null;
            contacts.Clear();
            nextHitTime.Clear();
            selfReturnTriggered = true; // Double trigger prevention
            if (agent && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.ResetPath();
            }
        }
    }

    private void FixedUpdate()
    {
        if (ExternalControl) return;
        if (contacts.Count == 0) return;

        if (meleeAOE)
        {
            foreach (var col in contacts)
                TryHit(col);
        }
        else
        {
            Collider best = null; float bestD2 = float.PositiveInfinity; Vector3 bestPoint = default;
            foreach (var col in contacts)
            {
                if (!IsValidMeleeTarget(col)) continue;
                Vector3 p = col.ClosestPoint(transform.position);
                float d2 = (p - transform.position).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = col; bestPoint = p; }
            }
            if (best) TryHit(best, bestPoint);
        }
    }

    private void TryHit(Collider col) => TryHit(col, col.ClosestPoint(transform.position));
    private void TryHit(Collider col, Vector3 hitPoint)
    {
        if (!IsValidMeleeTarget(col)) return;
        var dmg = col.GetComponentInParent<IDamageable>();
        if (dmg == null) return;

        int id = dmg.GetHashCode();
        if (!nextHitTime.TryGetValue(id, out float next)) next = 0f;
        if (Time.time < next) return;

        Vector3 dir = (hitPoint - transform.position).normalized;
        dmg.TakeDamage(damage, hitPoint, dir);
        nextHitTime[id] = Time.time + attackCooldown;

        if (debugLogs) Debug.Log($"[{name}] Hit {col.name} for {damage:F1}");
    }

    private bool IsValidMeleeTarget(Collider col)
    {
        if (!col || !col.enabled) return false;
        if (((1 << col.gameObject.layer) & meleeTargetMask) == 0) return false;
        if (!string.IsNullOrEmpty(meleeTargetTag) && !col.CompareTag(meleeTargetTag)) return false;
        return true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (((1 << other.gameObject.layer) & meleeTargetMask) == 0) return;
        if (!string.IsNullOrEmpty(meleeTargetTag) && !other.CompareTag(meleeTargetTag)) return;
        contacts.Add(other);
    }

    private void OnTriggerExit(Collider other)
    {
        contacts.Remove(other);
        var dmg = other.GetComponentInParent<IDamageable>();
        if (dmg != null) { /* keep cooldown to prevent stutter abuse */ }
    }

    private Transform FindNearestTarget()
    {
        // Broad phase: OverlapSphereNonAlloc
        const int MAX = 128;
        Collider[] buf = new Collider[MAX];
        int n = Physics.OverlapSphereNonAlloc(transform.position, targetSearchRadius, buf, targetLayerMask, QueryTriggerInteraction.Ignore);

        Transform best = null; float bestSqr = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            var c = buf[i]; if (!c) continue;
            // Tag filter
            bool tagOK = false;
            for (int t = 0; t < targetTags.Length; t++)
                if (c.CompareTag(targetTags[t])) { tagOK = true; break; }
            if (!tagOK) continue;

            float d2 = (c.transform.position - transform.position).sqrMagnitude;
            if (d2 < bestSqr) { bestSqr = d2; best = c.transform; }
        }
        return best;
    }

    private void SetDestinationTo(Vector3 worldPos)
    {
        if (!agent) return;
        if (NavMesh.SamplePosition(worldPos, out var hit, navSampleRadius, NavMesh.AllAreas))
        {
            agent.isStopped = false;
            agent.SetDestination(hit.position);
            lastSetDestination = hit.position;
        }
    }

    private void TrySnapToNavMesh()
    {
        if (!agent) return;
        if (NavMesh.SamplePosition(transform.position, out var hit, navSampleRadius, NavMesh.AllAreas))
            agent.Warp(hit.position);
    }

}
