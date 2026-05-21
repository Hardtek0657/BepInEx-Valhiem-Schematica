using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildMaterializer
{
    public static void TryMaterializePiece(Player player, bool ignoreCollision = false)
    {
        if (!PlanBuildPlugin.PlanningEnabled)
        {
            return;
        }

        if (!IsHammerEquipped(player))
        {
            player.Message(MessageHud.MessageType.Center, "Equip hammer to materialize pieces");
            return;
        }

        if (!PlanBuildPlugin.World.TryGetAimedPiece(PlanBuildPlugin.RemoveAimDistance.Value, out string pieceId, out PlanPiece planPiece))
        {
            player.Message(MessageHud.MessageType.Center, "No ghost piece in crosshair");
            return;
        }

        if (ZNetScene.instance == null)
        {
            return;
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(planPiece.Prefab);
        if (prefab == null)
        {
            player.Message(MessageHud.MessageType.Center, $"Unknown piece: {planPiece.Prefab}");
            return;
        }

        Piece pieceComponent = prefab.GetComponent<Piece>();
        if (pieceComponent == null)
        {
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return;
        }

        // --- Validate all placement restrictions at the stored position ---
        if (!ValidatePlacement(player, pieceComponent, planPiece.Position, ignoreCollision))
        {
            return;
        }

        // --- Check stamina BEFORE consuming resources ---
        float buildStamina = GetBuildStamina(player);
        if (buildStamina > 0f && player.GetStamina() < buildStamina)
        {
            LogMaterializerFailure(planPiece.Prefab, planPiece.Position, ignoreCollision,
                $"Not enough stamina. Required={buildStamina:0.##}, Current={player.GetStamina():0.##}");
            player.Message(MessageHud.MessageType.Center, "$msg_notenoughstamina");
            return;
        }

        // --- Check and consume resources using real Valheim requirements.
        // Planning mode normally bypasses requirements for ghost placement, so temporarily
        // let the materializer pass through to the game's original checks/consumption.
        PlanBuildRequirementContext.AllowRealRequirementsForMaterializer = true;
        try
        {
            if (!player.HaveRequirements(pieceComponent, Player.RequirementMode.CanBuild))
            {
                LogMaterializerFailure(planPiece.Prefab, planPiece.Position, ignoreCollision,
                    "Missing material or crafting requirements from Player.HaveRequirements.");
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                return;
            }

            player.ConsumeResources(pieceComponent.m_resources, 0, -1);
        }
        finally
        {
            PlanBuildRequirementContext.AllowRealRequirementsForMaterializer = false;
        }

        // --- Consume stamina ---
        if (buildStamina > 0f)
        {
            player.UseStamina(buildStamina);
        }

        // --- Instantiate the real piece ---
        GameObject realPiece = Object.Instantiate(prefab, planPiece.Position, planPiece.Rotation);

        Piece realPieceComponent = realPiece.GetComponent<Piece>();
        if (realPieceComponent != null)
        {
            realPieceComponent.SetCreator(player.GetPlayerID());
        }

        PrivateArea privateArea = realPiece.GetComponent<PrivateArea>();
        if (privateArea != null)
        {
            privateArea.Setup(Game.instance.GetPlayerProfile().GetName());
        }

        WearNTear wearNTear = realPiece.GetComponent<WearNTear>();
        if (wearNTear != null)
        {
            wearNTear.OnPlaced();
        }

        // Remove the ghost from the plan and relay
        if (PlanBuildPlugin.World.RemoveCompletedPiece(planPiece, pieceId, out string removedId))
        {
            PlanBuildRelayClient.SendRemove(removedId);
        }

        pieceComponent.m_placeEffect.Create(planPiece.Position, planPiece.Rotation, realPiece.transform);
        player.Message(MessageHud.MessageType.TopLeft, $"$msg_placed {pieceComponent.m_name}");
    }

    // Checks all placement restrictions that Valheim enforces in UpdatePlacementGhost.
    private static bool ValidatePlacement(Player player, Piece piece, Vector3 pos, bool ignoreCollision)
    {
        // No-build zones (dungeons, locations, etc.)
        if (Location.IsInsideNoBuildLocation(pos))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Inside no-build location.");
            player.Message(MessageHud.MessageType.Center, "$msg_nobuildzone");
            return false;
        }

        // Ward / private area
        PrivateArea piecePrivateArea = piece.GetComponent<PrivateArea>();
        float wardRadius = piecePrivateArea != null ? piecePrivateArea.m_radius : 0f;
        bool isWardPiece = piecePrivateArea != null;
        if (!PrivateArea.CheckAccess(pos, wardRadius, flash: false, wardCheck: isWardPiece))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Blocked by private area. WardRadius={wardRadius:0.##}, IsWardPiece={isWardPiece}");
            player.Message(MessageHud.MessageType.Center, "$msg_privatezone");
            return false;
        }

        // Biome restriction
        if (piece.m_onlyInBiome != Heightmap.Biome.None)
        {
            Heightmap.Biome currentBiome = Heightmap.FindBiome(pos);
            if ((currentBiome & piece.m_onlyInBiome) == 0)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    $"Wrong biome. Current={currentBiome}, Required={piece.m_onlyInBiome}");
                player.Message(MessageHud.MessageType.Center, "$msg_wrongbiome");
                return false;
            }
        }

        // Crafting station requirement (workbench range, forge range, etc.)
        // HaveRequirements already checks this, but we want the correct message here.
        if (piece.m_craftingStation != null &&
            !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, player.transform.position) &&
            !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Missing build station. Required={piece.m_craftingStation.m_name}, PlayerPos={FormatVector(player.transform.position)}");
            player.Message(MessageHud.MessageType.Center, "$msg_missingstation");
            return false;
        }

        // Station extension: needs the parent station nearby, and no other extension already occupying the space
        StationExtension stationExt = piece.GetComponent<StationExtension>();
        if (stationExt != null)
        {
            // FindClosestStationInRange takes an explicit Vector3 center - safe to call on prefab
            if (stationExt.FindClosestStationInRange(pos) == null)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision, "Station extension missing parent station in range.");
                player.Message(MessageHud.MessageType.Center, "$msg_extensionmissingstation");
                return false;
            }
            // OtherExtensionInRange uses transform.position internally, which is wrong on a prefab.
            // Replicate it manually using the planned position via the static m_allExtensions list.
            if (!ignoreCollision && AnyExtensionInRangeAt(pos, piece.m_spaceRequirement))
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    $"Station extension space blocked. SpaceRequirement={piece.m_spaceRequirement:0.##}");
                player.Message(MessageHud.MessageType.Center, "$msg_needspace");
                return false;
            }
            if (ignoreCollision)
            {
                PlanBuildPlugin.Log.LogInfo(
                    $"PlanBuild materializer collision override skipped station extension spacing for {piece.name} at {FormatVector(pos)}.");
            }
        }

        // Blocking pieces (e.g., only one portal within X radius)
        if (!ignoreCollision && piece.m_blockRadius > 0f && piece.m_blockingPieces.Count > 0)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, piece.m_blockRadius, LayerMask.GetMask("piece"));
            foreach (Collider col in nearby)
            {
                Piece found = col.GetComponentInParent<Piece>();
                if (found == null) continue;
                foreach (Piece blocking in piece.m_blockingPieces)
                {
                    if (blocking.m_name == found.m_name)
                    {
                        LogMaterializerFailure(piece.name, pos, ignoreCollision,
                            $"Blocked by nearby piece. BlockingPiece={blocking.m_name}, Found={found.m_name}, Radius={piece.m_blockRadius:0.##}");
                        player.Message(MessageHud.MessageType.Center, "$msg_needspace");
                        return false;
                    }
                }
            }
        }
        else if (ignoreCollision && piece.m_blockRadius > 0f && piece.m_blockingPieces.Count > 0)
        {
            PlanBuildPlugin.Log.LogInfo(
                $"PlanBuild materializer collision override skipped blocking-piece check for {piece.name} at {FormatVector(pos)}. Radius={piece.m_blockRadius:0.##}");
        }

        // mustConnectTo: piece must be within m_connectRadius of a specific prefab (e.g. vines on walls)
        // Game uses ghost transform.position; we use the planned pos directly.
        if (piece.m_mustConnectTo != null)
        {
            bool connected = false;
            Collider[] connectNearby = Physics.OverlapSphere(pos, piece.m_connectRadius);
            foreach (Collider col in connectNearby)
            {
                ZNetView znv = col.GetComponentInParent<ZNetView>();
                if (znv == null || !znv.name.Contains(piece.m_mustConnectTo.name)) continue;
                if (piece.m_mustBeAboveConnected)
                {
                    if (Physics.Raycast(pos, Vector3.down, out RaycastHit downHit) &&
                        downHit.transform.GetComponentInParent<ZNetView>() == znv)
                    {
                        connected = true;
                        break;
                    }
                }
                else
                {
                    connected = true;
                    break;
                }
            }
            if (!connected)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    $"Missing required connection. MustConnectTo={piece.m_mustConnectTo.name}, Radius={piece.m_connectRadius:0.##}, MustBeAbove={piece.m_mustBeAboveConnected}");
                player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
                return false;
            }
        }

        // Water surface checks
        if (piece.m_waterPiece || piece.m_noInWater)
        {
            bool hasWaterSurface = TryGetWaterSurface(pos, out float waterSurface);
            bool inWater = hasWaterSurface && pos.y <= waterSurface + 0.25f;

            if (piece.m_waterPiece && !inWater)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    hasWaterSurface
                        ? $"Water piece is above water surface. PieceY={pos.y:0.###}, WaterY={waterSurface:0.###}"
                        : "Water piece has no water surface at position.");
                player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
                return false;
            }
            if (piece.m_noInWater && inWater)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    $"Piece cannot be placed in water. PieceY={pos.y:0.###}, WaterY={waterSurface:0.###}");
                player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
                return false;
            }
        }

        // Heightmap-dependent checks
        Heightmap heightmap = Heightmap.FindHeightmap(pos);

        if (piece.m_groundPiece && heightmap == null)
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Ground piece has no heightmap at position.");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        if (piece.m_groundOnly && heightmap == null)
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Ground-only piece has no heightmap at position.");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        if (piece.m_cultivatedGroundOnly && (heightmap == null || !heightmap.IsCultivated(pos)))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Piece requires cultivated ground.");
            player.Message(MessageHud.MessageType.Center, "$msg_needcultivated");
            return false;
        }

        if (piece.m_vegetationGroundOnly && heightmap != null)
        {
            Heightmap.Biome biome = heightmap.GetBiome(pos);
            float vegMask = heightmap.GetVegetationMask(pos);
            bool notVeg = (biome == Heightmap.Biome.AshLands) ? (vegMask > 0.1f) : (vegMask < 0.25f);
            if (notVeg)
            {
                LogMaterializerFailure(piece.name, pos, ignoreCollision,
                    $"Piece requires vegetation/dirt mask. Biome={biome}, VegetationMask={vegMask:0.###}");
                player.Message(MessageHud.MessageType.Center, "$msg_needdirt");
                return false;
            }
        }

        // Surface normal checks: single downward raycast to get the surface normal and material.
        // Uses same layers as the game's m_placeRayMask equivalent.
        Vector3 surfaceNormal = Vector3.up;
        WearNTear? surfaceWear = null;
        if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit surfaceHit, 10f,
            LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain")))
        {
            surfaceNormal = surfaceHit.normal;
            surfaceWear = surfaceHit.collider.GetComponentInParent<WearNTear>();
        }

        if (piece.m_notOnWood && surfaceWear != null &&
            (surfaceWear.m_materialType == WearNTear.MaterialType.Wood ||
             surfaceWear.m_materialType == WearNTear.MaterialType.HardWood))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Piece cannot be placed on wood. SurfaceMaterial={surfaceWear.m_materialType}");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        if (piece.m_notOnTiltingSurface && surfaceNormal.y < 0.8f)
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Surface is too tilted. Normal={FormatVector(surfaceNormal)}");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        if (piece.m_inCeilingOnly && surfaceNormal.y > -0.5f)
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Piece requires ceiling. SurfaceNormal={FormatVector(surfaceNormal)}");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        if (piece.m_notOnFloor && surfaceNormal.y > 0.1f)
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision,
                $"Piece cannot be placed on floor. SurfaceNormal={FormatVector(surfaceNormal)}");
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return false;
        }

        // Teleport area restriction
        if (piece.m_onlyInTeleportArea && !EffectArea.IsPointInsideArea(pos, EffectArea.Type.Teleport))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Piece requires teleport area.");
            player.Message(MessageHud.MessageType.Center, "$msg_noteleportarea");
            return false;
        }

        // Dungeon / interior restriction
        if (!piece.m_allowedInDungeons && player.InInterior() &&
            !EnvMan.instance.CheckInteriorBuildingOverride() &&
            !ZoneSystem.instance.GetGlobalKey(GlobalKeys.DungeonBuild))
        {
            LogMaterializerFailure(piece.name, pos, ignoreCollision, "Piece is not allowed in dungeon/interior.");
            player.Message(MessageHud.MessageType.Center, "$msg_notindungeon");
            return false;
        }

        return true;
    }

    // Cache the reflection field for StationExtension.m_allExtensions (private static)
    private static FieldInfo? _allExtensionsField;

    private static bool AnyExtensionInRangeAt(Vector3 pos, float radius)
    {
        if (radius <= 0f) return false;

        _allExtensionsField ??= typeof(StationExtension).GetField("m_allExtensions",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (_allExtensionsField?.GetValue(null) is not System.Collections.Generic.List<StationExtension> allExtensions)
            return false;

        foreach (StationExtension ext in allExtensions)
        {
            if (ext != null && Vector3.Distance(ext.transform.position, pos) < radius)
                return true;
        }
        return false;
    }

    private static bool IsHammerEquipped(Player player)
    {
        ItemDrop.ItemData? rightItem = GetRightItem(player);
        if (rightItem == null) return false;
        return rightItem.m_shared.m_name.Contains("$item_hammer");
    }

    private static float GetBuildStamina(Player player)
    {
        ItemDrop.ItemData? rightItem = GetRightItem(player);
        if (rightItem == null) return 0f;
        return rightItem.m_shared.m_attack.m_attackStamina;
    }

    private static ItemDrop.ItemData? GetRightItem(Player player)
    {
        MethodInfo? method = typeof(Humanoid).GetMethod("GetRightItem",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return method?.Invoke(player, null) as ItemDrop.ItemData;
    }

    private static bool TryGetWaterSurface(Vector3 pos, out float surface)
    {
        surface = 0f;

        List<WaterVolume> volumes = WaterVolume.Instances;
        if (volumes == null || volumes.Count == 0)
        {
            return false;
        }

        bool found = false;
        float bestSurface = 0f;
        float bestDistance = float.MaxValue;
        Vector3 boundsPoint = pos;

        foreach (WaterVolume volume in volumes)
        {
            if (volume == null)
            {
                continue;
            }

            Collider volumeCollider = volume.GetComponent<Collider>();
            if (volumeCollider == null)
            {
                continue;
            }

            Bounds bounds = volumeCollider.bounds;
            boundsPoint.y = bounds.center.y;
            if (!bounds.Contains(boundsPoint))
            {
                continue;
            }

            float candidateSurface = volume.GetWaterSurface(pos, Time.time);
            float distance = Mathf.Abs(pos.y - candidateSurface);
            if (!found || distance < bestDistance)
            {
                found = true;
                bestDistance = distance;
                bestSurface = candidateSurface;
            }
        }

        surface = bestSurface;
        return found;
    }

    private static void LogMaterializerFailure(string prefab, Vector3 pos, bool ignoreCollision, string reason)
    {
        PlanBuildPlugin.Log.LogWarning(
            $"PlanBuild materializer rejected {prefab} at {FormatVector(pos)}. ShiftCollisionOverride={ignoreCollision}. Reason={reason}");
    }

    private static string FormatVector(Vector3 value)
    {
        return $"{value.x:0.###},{value.y:0.###},{value.z:0.###}";
    }
}
