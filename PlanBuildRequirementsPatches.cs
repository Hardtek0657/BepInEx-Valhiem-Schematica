using HarmonyLib;

namespace ValheimPlanBuild;

internal static class PlanBuildRequirementContext
{
    public static bool AllowRealRequirementsForMaterializer;
}

[HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
internal static class PlanBuildHaveRequirementsPatch
{
    private static bool Prefix(Player __instance, Player.RequirementMode mode, ref bool __result)
    {
        if (PlanBuildRequirementContext.AllowRealRequirementsForMaterializer ||
            !PlanBuildPlugin.ModEnabled.Value ||
            !PlanBuildPlugin.PlanningEnabled ||
            __instance != Player.m_localPlayer)
        {
            return true;
        }

        if (mode == Player.RequirementMode.IsKnown)
        {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources), typeof(Piece.Requirement[]), typeof(int), typeof(int), typeof(int))]
internal static class PlanBuildConsumeResourcesPatch
{
    private static bool Prefix(Player __instance)
    {
        return PlanBuildRequirementContext.AllowRealRequirementsForMaterializer ||
               !PlanBuildPlugin.ModEnabled.Value ||
               !PlanBuildPlugin.PlanningEnabled ||
               __instance != Player.m_localPlayer;
    }
}
