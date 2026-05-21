using System.Reflection;
using HarmonyLib;

namespace ValheimPlanBuild;

[HarmonyPatch(typeof(Chat), "InputText")]
internal static class PlanBuildChatCommandPatch
{
    private static readonly FieldInfo? InputField = typeof(Chat).GetField("m_input", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static bool Prefix(Chat __instance)
    {
        string? text = GetInputText(__instance);
        if (text == null)
        {
            return true;
        }

        return !PlanBuildPlugin.TryHandleChatCommand(__instance, text);
    }

    private static string? GetInputText(Chat chat)
    {
        object? input = InputField?.GetValue(chat);
        if (input == null)
        {
            return null;
        }

        PropertyInfo? textProperty = input.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
        return textProperty?.GetValue(input) as string;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
internal static class PlanBuildTryPlacePiecePatch
{
    private static bool Prefix(Player __instance, Piece piece, ref bool __result)
    {
        if (!PlanBuildPlugin.TryPlanPlacement(__instance, piece))
        {
            return true;
        }

        __result = false;
        return false;
    }
}
