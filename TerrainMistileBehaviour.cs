using UnityEngine;

namespace TerrainMistile;

public class TerrainMistileBehaviour : MonoBehaviour
{
    internal const string SelfDestructZdoKey = "TerrainMistileSelfDestruct";
    internal const string TerrainMistileZdoKey = "TerrainMistile";
    internal const string TerrainSourceZdoKey = "TerrainMistileSource";
    internal const string TerrainSourceSetZdoKey = "TerrainMistileSourceSet";
    internal const string ResetRadiusZdoKey = "TerrainMistileResetRadius";
    internal const string HealthZdoKey = "TerrainMistileHealth";
    internal const string VisualColorZdoKey = "TerrainMistileVisualColor";
    internal const string VisualColorSetZdoKey = "TerrainMistileVisualColorSet";
    private const float TerrainImpactDistance = 1.25f;
    private const float TerrainImpactHeight = 0.85f;
    private const float TerrainTargetGroundOffset = 0.15f;
    private const float VisualSyncRetryInterval = 0.25f;

    private Character _character = null!;
    private MonsterAI _monsterAI = null!;
    private ZNetView _nview = null!;
    private Vector3 _terrainTarget;
    private float _resetRadius = 8f;
    private bool _hasTerrainTarget;
    private bool _selfDestructTriggered;
    private bool _terrainResetDone;
    private bool _visualColorApplied;
    private float _nextVisualSyncTime;

    private void Awake()
    {
        _character = GetComponent<Character>();
        _monsterAI = GetComponent<MonsterAI>();
        _nview = GetComponent<ZNetView>();

        TryApplySyncedVisualColor();
        ApplyIdentity();
        ConfigureTerrainSeeker();
        TerrainMistilePrefab.MakeCollidersNonBlocking(gameObject);
        TerrainMistileSystem.RegisterActiveTerrainMistile(this);

        if (_character)
        {
            _character.m_onDeath += OnDeath;
        }
    }

    private void OnDestroy()
    {
        if (_character)
        {
            _character.m_onDeath -= OnDeath;
        }

        if (_hasTerrainTarget)
        {
            TerrainMistileSystem.ReleaseTerrainTarget(_terrainTarget);
        }

        TerrainMistileSystem.UnregisterActiveTerrainMistile(this);
    }

    private void FixedUpdate()
    {
        TryApplySyncedVisualColor();

        if (!_nview || !_nview.IsValid() || !_nview.IsOwner() || !_character || _character.IsDead() || _terrainResetDone)
        {
            return;
        }

        ApplyIdentity();
        ConfigureTerrainSeeker();

        if (!TryLoadTerrainTarget())
        {
            return;
        }

        MoveTowardTerrainTarget();
    }

    internal void Initialize(Player target, Vector3 terrainOperationPoint, float resetRadius, float health)
    {
        ApplyIdentity();
        ConfigureTerrainSeeker();
        ApplyHealth(health);
        SetTerrainTarget(terrainOperationPoint, resetRadius);

        if (_nview && _nview.IsValid() && _nview.IsOwner())
        {
            _nview.GetZDO().Set(TerrainMistileZdoKey, true);
        }
    }

    internal void MarkSelfDestruct(string reason = "self-destruct")
    {
        _selfDestructTriggered = true;
        if (_nview && _nview.IsValid() && _nview.IsOwner())
        {
            _nview.GetZDO().Set(SelfDestructZdoKey, true);
        }
    }

    private void ApplyIdentity()
    {
        if (!_character)
        {
            return;
        }

        _character.m_name = TerrainMistilePlugin.DisplayName;
        _character.m_faction = Character.Faction.Dverger;
        _character.m_aiSkipTarget = true;
        _character.m_flying = true;
    }

    private void ApplyHealth(float health)
    {
        if (!_character || !_nview || !_nview.IsValid() || !_nview.IsOwner())
        {
            return;
        }

        health = Mathf.Max(1f, health);
        _character.SetMaxHealth(health);
        _character.SetHealth(health);

        ZDO zdo = _nview.GetZDO();
        if (zdo != null)
        {
            zdo.Set(HealthZdoKey, health);
        }
    }

    private void ApplyVisualColor(Color color, bool syncToZdo)
    {
        TerrainMistilePrefab.ApplyVisuals(gameObject, color);
        _visualColorApplied = true;

        if (!syncToZdo || !_nview || !_nview.IsValid() || !_nview.IsOwner())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        if (zdo == null)
        {
            return;
        }

        zdo.Set(VisualColorZdoKey, new Vector3(color.r, color.g, color.b));
        zdo.Set(VisualColorSetZdoKey, true);
    }

    private void ApplyVisualColorForTarget(Vector3 target)
    {
        int biome = TerrainMistileSpawnRules.GetBiomeKey(target);
        TerrainMistileBiomeSpawnRule rule = TerrainMistileSpawnRules.GetRule(biome);
        ApplyVisualColor(rule.VisualColor, syncToZdo: true);
    }

    private void TryApplySyncedVisualColor()
    {
        if (_visualColorApplied || Time.time < _nextVisualSyncTime)
        {
            return;
        }

        _nextVisualSyncTime = Time.time + VisualSyncRetryInterval;
        if (!_nview || !_nview.IsValid())
        {
            return;
        }

        ZDO zdo = _nview.GetZDO();
        if (zdo == null || !zdo.GetBool(VisualColorSetZdoKey))
        {
            return;
        }

        Vector3 color = zdo.GetVec3(VisualColorZdoKey, Vector3.zero);
        ApplyVisualColor(new Color(color.x, color.y, color.z, 1f), syncToZdo: false);
    }

    private void ConfigureTerrainSeeker()
    {
        if (_monsterAI)
        {
            _monsterAI.m_enableHuntPlayer = false;
            _monsterAI.m_attackPlayerObjects = false;
            _monsterAI.m_aggravatable = false;
            _monsterAI.m_targetCreature = null;
            _monsterAI.m_targetStatic = null;
            if (_monsterAI.m_nview && _monsterAI.m_nview.IsValid())
            {
                _monsterAI.SetHuntPlayer(false);
                _monsterAI.SetAlerted(false);
            }
            else
            {
                _monsterAI.m_huntPlayer = false;
                _monsterAI.m_alerted = false;
            }
            _monsterAI.enabled = false;
        }
    }

    private void SetTerrainTarget(Vector3 terrainOperationPoint, float resetRadius)
    {
        if (_hasTerrainTarget)
        {
            TerrainMistileSystem.ReleaseTerrainTarget(_terrainTarget);
        }

        _terrainTarget = terrainOperationPoint;
        _resetRadius = Mathf.Max(0.1f, resetRadius);
        _hasTerrainTarget = true;
        TerrainMistileSystem.ReserveTerrainTarget(_terrainTarget, _resetRadius);
        ApplyVisualColorForTarget(_terrainTarget);

        if (_nview && _nview.IsValid() && _nview.IsOwner())
        {
            _nview.GetZDO().Set(TerrainSourceZdoKey, terrainOperationPoint);
            _nview.GetZDO().Set(TerrainSourceSetZdoKey, true);
            _nview.GetZDO().Set(ResetRadiusZdoKey, _resetRadius);
        }
    }

    private bool TryLoadTerrainTarget()
    {
        if (!TryGetTerrainTarget(out Vector3 terrainTarget, out float resetRadius))
        {
            return false;
        }

        if (!_hasTerrainTarget)
        {
            _terrainTarget = terrainTarget;
            _resetRadius = resetRadius;
            _hasTerrainTarget = true;
            TerrainMistileSystem.ReserveTerrainTarget(_terrainTarget, _resetRadius);
        }

        return true;
    }

    internal bool TryGetTerrainTarget(out Vector3 terrainTarget)
    {
        return TryGetTerrainTarget(out terrainTarget, out _);
    }

    internal bool TryGetTerrainTarget(out Vector3 terrainTarget, out float resetRadius)
    {
        if (_hasTerrainTarget)
        {
            terrainTarget = _terrainTarget;
            resetRadius = _resetRadius;
            return true;
        }

        if (!_nview || !_nview.IsValid())
        {
            terrainTarget = default;
            resetRadius = _resetRadius;
            return false;
        }

        ZDO zdo = _nview.GetZDO();
        if (zdo == null || !zdo.GetBool(TerrainSourceSetZdoKey))
        {
            terrainTarget = default;
            resetRadius = _resetRadius;
            return false;
        }

        terrainTarget = zdo.GetVec3(TerrainSourceZdoKey, transform.position);
        resetRadius = zdo.GetFloat(ResetRadiusZdoKey);
        return true;
    }

    private void MoveTowardTerrainTarget()
    {
        Vector3 currentPosition = transform.position;
        Vector3 targetPosition = _terrainTarget;

        if (TerrainMistileSystem.TryGetGroundHeight(targetPosition, out float targetGroundHeight))
        {
            targetPosition.y = targetGroundHeight + TerrainTargetGroundOffset;
        }

        Vector3 toTarget = targetPosition - currentPosition;
        float horizontalDistance = new Vector2(toTarget.x, toTarget.z).magnitude;
        bool closeToTarget = horizontalDistance <= TerrainImpactDistance || toTarget.sqrMagnitude <= TerrainImpactDistance * TerrainImpactDistance;
        bool nearGround = TerrainMistileSystem.TryGetGroundHeight(currentPosition, out float currentGroundHeight) && currentPosition.y <= currentGroundHeight + TerrainImpactHeight;

        if (closeToTarget && nearGround)
        {
            DetonateOnTerrain();
            return;
        }

        if (toTarget.sqrMagnitude <= 0.01f)
        {
            DetonateOnTerrain();
            return;
        }

        Vector3 moveDirection = toTarget.normalized;
        _character.SetRun(true);
        _character.SetMoveDir(moveDirection);

        Vector3 lookDirection = new(moveDirection.x, 0f, moveDirection.z);
        if (lookDirection.sqrMagnitude > 0.001f)
        {
            _character.SetLookDir(lookDirection.normalized);
        }
    }

    private void DetonateOnTerrain()
    {
        if (_terrainResetDone || !_character || _character.IsDead())
        {
            return;
        }

        if (TryRetargetOrDespawnFromEmptyImpact())
        {
            return;
        }

        MarkSelfDestruct("terrain impact");
        TryResetTerrain("terrain impact");

        HitData hitData = new();
        hitData.m_point = transform.position;
        hitData.m_damage.m_damage = 9999999f;
        hitData.m_hitType = HitData.HitType.Self;
        _character.ApplyDamage(hitData, showDamageText: false, triggerEffects: true);

        if (!_character.IsDead() && _nview && _nview.IsValid() && _nview.IsOwner() && ZNetScene.instance)
        {
            ZNetScene.instance.Destroy(gameObject);
        }
    }

    private bool TryRetargetOrDespawnFromEmptyImpact()
    {
        TryLoadTerrainTarget();
        if (!_hasTerrainTarget)
        {
            return false;
        }

        if (TerrainMistileSystem.HasModifiedTerrainAround(_terrainTarget, _resetRadius))
        {
            return false;
        }

        TerrainMistileSystem.ReleaseTerrainTarget(_terrainTarget);
        if (TerrainMistileSystem.TryFindReplacementTerrainTarget(transform.position, out Vector3 replacementTarget))
        {
            SetTerrainTarget(replacementTarget, TerrainMistileSystem.GetResetRadiusForPoint(replacementTarget));
            return true;
        }

        DestroyWithoutTerrainReset();
        return true;
    }

    private void DestroyWithoutTerrainReset()
    {
        _terrainResetDone = true;
        if (_hasTerrainTarget)
        {
            TerrainMistileSystem.ReleaseTerrainTarget(_terrainTarget);
        }

        if (_nview && _nview.IsValid() && _nview.IsOwner() && ZNetScene.instance)
        {
            ZNetScene.instance.Destroy(gameObject);
            return;
        }

        Destroy(gameObject);
    }

    private void OnDeath()
    {
        TryResetTerrain("death");
    }

    internal void TryResetTerrain(string reason)
    {
        if (_nview && _nview.IsValid())
        {
            if (!_nview.IsOwner())
            {
                return;
            }

            _selfDestructTriggered |= _nview.GetZDO().GetBool(SelfDestructZdoKey);
        }

        if (_terrainResetDone)
        {
            return;
        }

        if (!_selfDestructTriggered)
        {
            return;
        }

        _terrainResetDone = true;
        TryLoadTerrainTarget();
        Vector3 resetCenter = _hasTerrainTarget ? _terrainTarget : transform.position;
        if (_hasTerrainTarget)
        {
            TerrainMistileSystem.ReleaseTerrainTarget(_terrainTarget);
        }

        TerrainMistilePlugin.TerrainMistileLogger.LogInfo($"TerrainMistile terrain reset triggered by {reason} at {resetCenter}.");
        TerrainMistileSystem.ResetTerrainAround(resetCenter, _resetRadius, resetPaint: true);
    }
}
