using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class UnitHiveBinding : MonoBehaviour
{
    [HideInInspector] public HiveNode parent;
    [HideInInspector] public EnemyType type;

    private NavMeshAgent agent;
    private EnemyUnit unit;
    private bool handledEnd;
    public bool IsReturning { get; private set; }

    private float reabsorbRadius;
    private float timeout;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        unit = GetComponent<EnemyUnit>();
    }

    // Called by HiveNode right after spawn.
    public void Bind(HiveNode parentNode, EnemyType enemyType, float reabsorbRadius, float timeout)
    {
        parent = parentNode;
        type = enemyType;
        this.reabsorbRadius = reabsorbRadius;
        this.timeout = timeout;
    }

    // Called by the HiveNode during a global recall.
    public void OrderReturn(Vector3 hivePos)
    {
        if (IsReturning) return;
        IsReturning = true;

        unit?.SetExternalControl(true);
        StopAllCoroutines();
        StartCoroutine(ReturnRoutine(hivePos));
    }

    // Called by the EnemyUnit itself when idle too long.
    public void OrderReturnToParent()
    {
        if (IsReturning || parent == null) return;
        IsReturning = true;

        unit?.SetExternalControl(true);
        StopAllCoroutines();
        StartCoroutine(ReturnRoutine(parent.transform.position));
    }

    // Handles the actual movement and despawn/reabsorb.
    private IEnumerator ReturnRoutine(Vector3 hivePos)
    {
        if (!agent) agent = GetComponent<NavMeshAgent>();
        float t = 0f;
        const float repathTick = 0.5f;
        float nextRepath = 0f;

        // Find safe NavMesh position near the hive
        Vector3 targetPos = hivePos;
        if (NavMesh.SamplePosition(hivePos, out var hit, 2.0f, NavMesh.AllAreas))
            targetPos = hit.position;

        if (agent && agent.isOnNavMesh)
            agent.SetDestination(targetPos);

        var wait = new WaitForSeconds(0.2f);
        while (t < timeout)
        {
            t += 0.2f;

            if (Time.time >= nextRepath && agent && agent.isOnNavMesh)
            {
                nextRepath = Time.time + repathTick;
                agent.SetDestination(targetPos);
            }

            if ((transform.position - targetPos).sqrMagnitude <= reabsorbRadius * reabsorbRadius)
            {
                handledEnd = true;
                parent?.OnUnitReabsorbed(unit, type);
                Destroy(gameObject);
                yield break;
            }

            yield return wait;
        }

        // Timeout ? recycle or force reabsorb if close
        if ((transform.position - targetPos).sqrMagnitude <= reabsorbRadius * reabsorbRadius * 4f)
        {
            handledEnd = true;
            parent?.OnUnitReabsorbed(unit, type);
        }
        Destroy(gameObject);
    }
    public void CancelReturn()
    {
        if (!IsReturning) return;
        IsReturning = false;

        // stop the return coroutine without notifying the node
        StopAllCoroutines();

        // hand AI control back to the unit
        unit?.SetExternalControl(false);

        // clear/reset path
        if (agent && agent.isOnNavMesh)
        {
            agent.isStopped = false;
            agent.ResetPath();
        }
        // do not touch handledEnd here
    }

    private void OnDestroy()
    {
        if (handledEnd) return;
        if (parent != null && gameObject.scene.isLoaded)
            parent.OnUnitDied(unit, type);
    }
}
