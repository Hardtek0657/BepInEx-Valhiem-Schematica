using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildMaterializerInput
{
    public static void CheckMaterializeInput()
    {
        if (!PlanBuildPlugin.ModEnabled.Value || !PlanBuildPlugin.PlanningEnabled)
        {
            return;
        }

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.G) &&
            !Chat.instance.HasFocus() &&
            !Console.IsVisible() &&
            !InventoryGui.IsVisible() &&
            !Minimap.IsOpen())
        {
            bool ignoreCollision = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            PlanBuildMaterializer.TryMaterializePiece(localPlayer, ignoreCollision);
        }
    }
}
