using System.Reflection;

namespace ValheimPlanBuild;

internal static class PlanBuildItemUtils
{
    private static MethodInfo? _getRightItemMethod;

    public static bool IsHammerEquipped(Player? player)
    {
        ItemDrop.ItemData? rightItem = GetRightItem(player);
        return IsHammerItem(rightItem);
    }

    public static ItemDrop.ItemData? GetRightItem(Player? player)
    {
        if (player == null)
        {
            return null;
        }

        _getRightItemMethod ??= typeof(Humanoid).GetMethod(
            "GetRightItem",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        return _getRightItemMethod?.Invoke(player, null) as ItemDrop.ItemData;
    }

    private static bool IsHammerItem(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return false;
        }

        if (IsHammerName(item.m_shared?.m_name))
        {
            return true;
        }

        return IsHammerName(item.m_dropPrefab ? item.m_dropPrefab.name : null);
    }

    private static bool IsHammerName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && name.IndexOf("hammer", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
