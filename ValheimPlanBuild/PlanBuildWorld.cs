using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPlanBuild;

internal sealed class PlanBuildWorld
{
    private readonly Dictionary<string, PlanPiece> _pieces = new();
    private readonly Dictionary<string, GameObject> _visuals = new();
    private bool _visible;
    private static int _ghostLayer = -1; // Cache layer mask

    public int Count => _pieces.Count;

    public bool AddOrUpdate(PlanPiece piece, bool localChange)
    {
        if (string.IsNullOrWhiteSpace(piece.Id) || string.IsNullOrWhiteSpace(piece.Prefab))
        {
            return false;
        }

        _pieces[piece.Id] = piece;
        if (_visible)
        {
            RefreshVisual(piece);
        }

        return true;
    }

    public bool Remove(string id, bool localChange)
    {
        if (!_pieces.Remove(id))
        {
            return false;
        }

        if (_visuals.TryGetValue(id, out GameObject visual) && visual)
        {
            UnityEngine.Object.Destroy(visual);
        }

        _visuals.Remove(id);

        return true;
    }

    public bool RemoveCompletedPiece(PlanPiece completedPiece, string fallbackId, out string removedId)
    {
        removedId = FindMatchingPiece(completedPiece);
        if (string.IsNullOrEmpty(removedId))
        {
            removedId = fallbackId;
        }

        return Remove(removedId, localChange: true);
    }

    public void Replace(IEnumerable<PlanPiece> pieces, bool localChange)
    {
        Clear(localChange: false);
        foreach (PlanPiece piece in pieces)
        {
            AddOrUpdate(piece, localChange);
        }
    }

    public void Clear(bool localChange)
    {
        foreach (GameObject visual in _visuals.Values)
        {
            if (visual)
            {
                UnityEngine.Object.Destroy(visual);
            }
        }

        _visuals.Clear();
        _pieces.Clear();
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        if (_visible)
        {
            RefreshAllVisuals();
        }
        else
        {
            DestroyAllVisuals();
        }
    }

    public List<PlanPiece> Snapshot()
    {
        // Pre-allocate list capacity for better performance
        var snapshot = new List<PlanPiece>(_pieces.Count);
        foreach (var piece in _pieces.Values)
        {
            snapshot.Add(Clone(piece));
        }
        return snapshot;
    }

    public string[] ExportRecords()
    {
        // Pre-allocate array and avoid LINQ for better performance
        var pieces = new PlanPiece[_pieces.Count];
        _pieces.Values.CopyTo(pieces, 0);
        Array.Sort(pieces, (a, b) => a.CreatedUnixMs.CompareTo(b.CreatedUnixMs));
        
        var records = new string[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            records[i] = pieces[i].ToRecord();
        }
        return records;
    }

    public bool TryGetAimedPiece(float maxDistanceFromHit, out string pieceId, out PlanPiece planPiece)
    {
        pieceId = "";
        planPiece = null!;
        
        if (_pieces.Count == 0 || GameCamera.instance == null)
        {
            return false;
        }

        Ray ray = new(GameCamera.instance.transform.position, GameCamera.instance.transform.forward);
        string bestId = FindClosestVisualToRay(ray, maxDistanceFromHit);
        
        if (string.IsNullOrEmpty(bestId) || !_pieces.TryGetValue(bestId, out planPiece!))
        {
            return false;
        }

        pieceId = bestId;
        return true;
    }

    public bool RemoveAimedPiece(float maxDistanceFromHit, out string removedId)
    {
        removedId = "";
        if (_pieces.Count == 0 || GameCamera.instance == null)
        {
            return false;
        }

        Ray ray = new(GameCamera.instance.transform.position, GameCamera.instance.transform.forward);
        string bestId = FindClosestVisualToRay(ray, maxDistanceFromHit);
        
        if (string.IsNullOrEmpty(bestId))
        {
            return false;
        }

        removedId = bestId;
        return Remove(bestId, localChange: true);
    }

    private string FindClosestVisualToRay(Ray ray, float maxDistanceFromHit)
    {
        string bestId = "";
        float bestDistance = float.MaxValue;
        const float maxTargetDistance = 50f;
        float aimTolerance = Mathf.Max(1f, maxDistanceFromHit * 0.5f);
        
        // First pass: Try to hit actual colliders with raycast (most accurate)
        // Use NonAlloc to avoid allocations
        RaycastHit[] hitBuffer = new RaycastHit[32]; // Reusable buffer
        int hitCount = Physics.RaycastNonAlloc(ray, hitBuffer, maxTargetDistance, ~0, QueryTriggerInteraction.Collide);
        
        // Build a lookup for faster matching
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = hitBuffer[i];
            if (hit.distance >= bestDistance)
            {
                continue;
            }
            
            // Check if this collider belongs to one of our ghost pieces
            foreach (KeyValuePair<string, GameObject> entry in _visuals)
            {
                if (entry.Value && hit.collider.transform.IsChildOf(entry.Value.transform))
                {
                    bestDistance = hit.distance;
                    bestId = entry.Key;
                    break;
                }
            }
        }

        // Second pass: If no direct hit, use bounds intersection (less accurate fallback)
        if (bestId.Length == 0)
        {
            foreach (KeyValuePair<string, GameObject> entry in _visuals)
            {
                GameObject visual = entry.Value;
                if (!visual)
                {
                    continue;
                }

                if (TryGetVisualBounds(visual, out Bounds bounds))
                {
                    bounds.Expand(0.35f);
                    if (bounds.IntersectRay(ray, out float hitDistance) && hitDistance < bestDistance && hitDistance <= maxTargetDistance)
                    {
                        bestDistance = hitDistance;
                        bestId = entry.Key;
                    }
                }
            }
        }

        // Third pass: floor pieces are thin, so near misses are common.
        // Pick the closest visual bounds to the crosshair ray within a tolerance.
        if (bestId.Length == 0)
        {
            bestId = FindClosestBoundsNearRay(ray, aimTolerance, maxTargetDistance);
        }

        if (bestId.Length == 0)
        {
            bestId = FindClosestPieceToRay(ray, aimTolerance);
        }

        return bestId;
    }

    private string FindClosestBoundsNearRay(Ray ray, float maxDistanceFromRay, float maxTargetDistance)
    {
        string bestId = "";
        float bestRayDistanceSqr = float.MaxValue;
        float bestForwardDistance = float.MaxValue;
        float maxDistanceSqr = maxDistanceFromRay * maxDistanceFromRay;

        foreach (KeyValuePair<string, GameObject> entry in _visuals)
        {
            GameObject visual = entry.Value;
            if (!visual || !TryGetVisualBounds(visual, out Bounds bounds))
            {
                continue;
            }

            bounds.Expand(0.35f);
            Vector3 originToBounds = bounds.center - ray.origin;
            float forwardDistance = Vector3.Dot(originToBounds, ray.direction);
            if (forwardDistance < 0f || forwardDistance > maxTargetDistance)
            {
                continue;
            }

            Vector3 pointOnRay = ray.origin + ray.direction * forwardDistance;
            Vector3 closestBoundsPoint = bounds.ClosestPoint(pointOnRay);
            float rayDistanceSqr = (closestBoundsPoint - pointOnRay).sqrMagnitude;
            if (rayDistanceSqr <= maxDistanceSqr && (rayDistanceSqr < bestRayDistanceSqr || Mathf.Approximately(rayDistanceSqr, bestRayDistanceSqr) && forwardDistance < bestForwardDistance))
            {
                bestId = entry.Key;
                bestRayDistanceSqr = rayDistanceSqr;
                bestForwardDistance = forwardDistance;
            }
        }

        return bestId;
    }

    private string FindMatchingPiece(PlanPiece completedPiece)
    {
        const float positionToleranceSqr = 0.05f * 0.05f;
        const float rotationDotTolerance = 0.9999f;

        foreach (KeyValuePair<string, PlanPiece> entry in _pieces)
        {
            PlanPiece piece = entry.Value;
            if (!string.Equals(piece.Prefab, completedPiece.Prefab, StringComparison.Ordinal))
            {
                continue;
            }

            if ((piece.Position - completedPiece.Position).sqrMagnitude > positionToleranceSqr)
            {
                continue;
            }

            if (Mathf.Abs(Quaternion.Dot(piece.Rotation, completedPiece.Rotation)) < rotationDotTolerance)
            {
                continue;
            }

            return entry.Key;
        }

        return "";
    }

    private void RefreshAllVisuals()
    {
        DestroyAllVisuals();
        foreach (PlanPiece piece in _pieces.Values)
        {
            RefreshVisual(piece);
        }
    }

    private void DestroyAllVisuals()
    {
        foreach (GameObject visual in _visuals.Values)
        {
            if (visual)
            {
                UnityEngine.Object.Destroy(visual);
            }
        }

        _visuals.Clear();
    }

    private void RefreshVisual(PlanPiece piece)
    {
        if (_visuals.TryGetValue(piece.Id, out GameObject oldVisual) && oldVisual)
        {
            UnityEngine.Object.Destroy(oldVisual);
        }

        GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(piece.Prefab) : null;
        if (prefab == null)
        {
            PlanBuildPlugin.Log.LogWarning("Missing prefab for planned piece: " + piece.Prefab);
            return;
        }

        GameObject visual = CreateVisual(prefab, piece.Position, piece.Rotation);
        visual.name = "PlanBuild_" + piece.Prefab + "_" + piece.Id;
        _visuals[piece.Id] = visual;
    }

    private static GameObject CreateVisual(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        bool previousTerrainOps = TerrainOp.m_forceDisableTerrainOps;
        bool previousZNetInit = ZNetView.m_forceDisableInit;
        TerrainOp.m_forceDisableTerrainOps = true;
        ZNetView.m_forceDisableInit = true;
        GameObject visual = UnityEngine.Object.Instantiate(prefab, position, rotation);
        ZNetView.m_forceDisableInit = previousZNetInit;
        TerrainOp.m_forceDisableTerrainOps = previousTerrainOps;

        foreach (Rigidbody body in visual.GetComponentsInChildren<Rigidbody>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(body);
        }

        foreach (AudioSource audioSource in visual.GetComponentsInChildren<AudioSource>(includeInactive: true))
        {
            audioSource.enabled = false;
        }

        // EffectArea.Awake() has no ZDO guard - it unconditionally registers in s_allAreas,
        // s_noMonsterAreas, and s_BurningAreas. Destroy them so OnDestroy() cleans the lists.
        foreach (EffectArea ea in visual.GetComponentsInChildren<EffectArea>(includeInactive: true))
        {
            UnityEngine.Object.Destroy(ea);
        }

        // Cache ghost layer lookup
        if (_ghostLayer == -1)
        {
            _ghostLayer = LayerMask.NameToLayer("ghost");
        }
        
        // Set all objects to ghost layer first (for rendering)
        Transform[] transforms = visual.GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i].gameObject.layer = _ghostLayer;
        }

        // Then enable colliders as triggers on piece layer for snap point detection
        // This overrides the ghost layer for collider objects, allowing snapping to work
        int pieceLayer = LayerMask.NameToLayer("piece");
        foreach (Collider collider in visual.GetComponentsInChildren<Collider>(includeInactive: true))
        {
            if (IsNonBuildAreaCollider(collider))
            {
                collider.enabled = false;
                continue;
            }

            collider.enabled = true;
            collider.isTrigger = true; // Make it a trigger so it doesn't physically collide
            collider.gameObject.layer = pieceLayer; // Use piece layer for snap point detection
        }

        // Ensure WearNTear pieces show the "new" visual state
        var wearNTear = visual.GetComponent<WearNTear>();
        if (wearNTear != null)
        {
            // Activate m_new visual (full health state)
            if (wearNTear.m_new != null)
            {
                wearNTear.m_new.SetActive(true);
            }
            // Deactivate worn and broken visuals
            if (wearNTear.m_worn != null && wearNTear.m_worn != wearNTear.m_new)
            {
                wearNTear.m_worn.SetActive(false);
            }
            if (wearNTear.m_broken != null && wearNTear.m_broken != wearNTear.m_new)
            {
                wearNTear.m_broken.SetActive(false);
            }
        }

        // Ensure Piece component is enabled for snap point detection
        var piece = visual.GetComponent<Piece>();
        if (piece != null)
        {
            piece.enabled = true;
            // Snap points are child transforms with "snappoint" tag - they're preserved automatically
        }

        TintRenderers(visual);
        return visual;
    }

    private static bool IsNonBuildAreaCollider(Collider collider)
    {
        if (collider.CompareTag("StationUseArea"))
        {
            return true;
        }

        // Any collider owned by an EffectArea should be disabled -
        // these are comfort/heat/noBuild zones that must not affect placement.
        if (collider.GetComponentInParent<EffectArea>() != null)
        {
            return true;
        }

        CraftingStation craftingStation = collider.GetComponentInParent<CraftingStation>();
        if (craftingStation != null)
        {
            if (craftingStation.m_effectAreaCollider == collider)
            {
                return true;
            }

            if (craftingStation.m_areaMarker != null && collider.transform.IsChildOf(craftingStation.m_areaMarker.transform))
            {
                return true;
            }
        }

        PrivateArea privateArea = collider.GetComponentInParent<PrivateArea>();
        if (privateArea != null && privateArea.m_areaMarker != null && collider.transform.IsChildOf(privateArea.m_areaMarker.transform))
        {
            return true;
        }

        return HasAreaMarkerName(collider.transform);
    }

    private static bool HasAreaMarkerName(Transform transform)
    {
        for (Transform? current = transform; current != null; current = current.parent)
        {
            string name = current.name;
            if (name.IndexOf("AreaMarker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("StationUseArea", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("EffectArea", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private string FindClosestPieceToRay(Ray ray, float maxDistanceFromRay)
    {
        string bestId = "";
        float maxDistanceSqr = Mathf.Max(0.25f, maxDistanceFromRay * maxDistanceFromRay);
        float bestRayDistanceSqr = float.MaxValue;
        float bestForwardDistance = float.MaxValue;

        foreach (PlanPiece piece in _pieces.Values)
        {
            Vector3 originToPiece = piece.Position - ray.origin;
            float forwardDistance = Vector3.Dot(originToPiece, ray.direction);
            if (forwardDistance < 0f)
            {
                continue;
            }

            Vector3 closestPointOnRay = ray.origin + ray.direction * forwardDistance;
            float rayDistanceSqr = (piece.Position - closestPointOnRay).sqrMagnitude;
            if (rayDistanceSqr <= maxDistanceSqr && (rayDistanceSqr < bestRayDistanceSqr || Mathf.Approximately(rayDistanceSqr, bestRayDistanceSqr) && forwardDistance < bestForwardDistance))
            {
                bestId = piece.Id;
                bestRayDistanceSqr = rayDistanceSqr;
                bestForwardDistance = forwardDistance;
            }
        }

        return bestId;
    }

    private static bool TryGetVisualBounds(GameObject visual, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(includeInactive: false))
        {
            if (!renderer.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private static void TintRenderers(GameObject visual)
    {
        // Cache renderer array to avoid repeated GetComponentsInChildren calls
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            
            // Ensure renderer is enabled
            renderer.enabled = true;
            
            Material[] materials = renderer.sharedMaterials;
            
            // Skip if no materials
            if (materials == null || materials.Length == 0)
            {
                continue;
            }
            
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null)
                {
                    continue;
                }

                Material material = new(materials[i]);
                ApplyPlanGhostMaterial(material);

                if (material.HasProperty("_ValueNoise"))
                {
                    material.SetFloat("_ValueNoise", 0f);
                }

                materials[i] = material;
            }

            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private static void ApplyPlanGhostMaterial(Material material)
    {
        const float alpha = 0.6f; // Increased alpha for better visibility

        // Set color with alpha
        if (material.HasProperty("_Color"))
        {
            Color color = material.color;
            material.color = new Color(
                Mathf.Lerp(color.r, 0.2f, 0.55f),
                Mathf.Lerp(color.g, 0.85f, 0.55f),
                Mathf.Lerp(color.b, 1f, 0.55f),
                alpha);
        }

        // Ensure main texture is preserved
        if (material.HasProperty("_MainTex") && material.mainTexture != null)
        {
            material.SetTexture("_MainTex", material.mainTexture);
        }

        // Handle bump map (normal map) if present
        if (material.HasProperty("_BumpMap") && material.GetTexture("_BumpMap") != null)
        {
            material.SetTexture("_BumpMap", material.GetTexture("_BumpMap"));
        }

        // Preserve emission if present
        if (material.HasProperty("_EmissionColor"))
        {
            Color emissionColor = material.GetColor("_EmissionColor");
            material.SetColor("_EmissionColor", emissionColor);
        }

        // Set rendering mode to transparent
        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f); // Transparent mode
        }

        // Set blend mode for transparency
        if (material.HasProperty("_SrcBlend"))
        {
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        // Ensure shader keywords are properly set for transparency
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        
        // Set render queue to transparent
        material.renderQueue = (int)RenderQueue.Transparent;
        
        // Enable GPU instancing if supported
        material.enableInstancing = true;
    }

    private static PlanPiece Clone(PlanPiece piece)
    {
        return new PlanPiece
        {
            Id = piece.Id,
            Prefab = piece.Prefab,
            Position = piece.Position,
            Rotation = piece.Rotation,
            Owner = piece.Owner,
            CreatedUnixMs = piece.CreatedUnixMs
        };
    }
}
