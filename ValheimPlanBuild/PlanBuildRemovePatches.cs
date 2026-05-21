using System.Reflection;
using HarmonyLib;

namespace ValheimPlanBuild;

[HarmonyPatch]
internal static class PlanBuildRemovePiecePatch
{
    private static MethodBase? TargetMethod()
    {
        return typeof(Player).GetMethod("RemovePiece", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static bool Prefix(Player __instance, ref bool __result)
    {
        if (!PlanBuildPlugin.ModEnabled.Value || !PlanBuildPlugin.PlanningEnabled || __instance != Player.m_localPlayer)
        {
            return true;
        }

        // Remove the ghost piece but always return false so the caller's
        // UseStamina(GetBuildStamina()) block never executes.
        PlanBuildPlugin.TryRemoveAimedPlannedPiece();
        __result = false;
        return false;
    }
}
