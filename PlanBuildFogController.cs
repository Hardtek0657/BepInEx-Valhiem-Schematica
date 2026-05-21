using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildFogController
{
    private static bool _active;
    private static bool _previousFog;
    private static float _previousFogDensity;
    private static Color _previousFogColor;

    public static void Enable()
    {
        if (_active)
        {
            return;
        }

        _previousFog = RenderSettings.fog;
        _previousFogDensity = RenderSettings.fogDensity;
        _previousFogColor = RenderSettings.fogColor;
        _active = true;
    }

    public static void Update()
    {
        if (!_active)
        {
            return;
        }

        if (!IsHammerEquipped())
        {
            return;
        }

        ApplyNoFog();
    }

    private static bool IsHammerEquipped()
    {
        return PlanBuildItemUtils.IsHammerEquipped(Player.m_localPlayer);
    }

    public static void Disable()
    {
        if (!_active)
        {
            return;
        }

        RenderSettings.fog = _previousFog;
        RenderSettings.fogDensity = _previousFogDensity;
        RenderSettings.fogColor = _previousFogColor;
        _active = false;
    }

    private static void ApplyNoFog()
    {
        RenderSettings.fog = false;
        RenderSettings.fogDensity = 0f;
    }
}
