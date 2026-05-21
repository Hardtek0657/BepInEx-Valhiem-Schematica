using System.Reflection;
using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildReflection
{
    private static readonly FieldInfo? PlacementGhostField = typeof(Player).GetField("m_placementGhost", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? UpdatePlacementGhostMethod = typeof(Player).GetMethod("UpdatePlacementGhost", BindingFlags.Instance | BindingFlags.NonPublic);

    public static GameObject? GetPlacementGhost(Player player)
    {
        return PlacementGhostField?.GetValue(player) as GameObject;
    }

    public static bool TryRefreshPlacement(Player player, bool flashGuardStone)
    {
        if (UpdatePlacementGhostMethod == null)
        {
            PlanBuildPlugin.Log.LogWarning("Unable to find Player.UpdatePlacementGhost.");
            return false;
        }

        UpdatePlacementGhostMethod.Invoke(player, new object[] { flashGuardStone });
        return true;
    }
}
