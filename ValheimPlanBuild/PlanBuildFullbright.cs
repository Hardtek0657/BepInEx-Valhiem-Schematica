using UnityEngine;

namespace ValheimPlanBuild;

internal static class PlanBuildFullbright
{
    private static bool _fullbrightActive;
    private static Color _originalAmbientLight;
    private static float _originalAmbientIntensity;
    private static Color _lastAmbientLight;
    private static float _lastAmbientIntensity;

    public static void Enable()
    {
        if (_fullbrightActive)
        {
            return;
        }

        // Store original lighting settings
        _originalAmbientLight = RenderSettings.ambientLight;
        _originalAmbientIntensity = RenderSettings.ambientIntensity;

        // Set fullbright ambient lighting
        ApplyFullbright();

        _fullbrightActive = true;
        PlanBuildPlugin.Log.LogInfo("Fullbright enabled for plan build mode.");
    }

    public static void Disable()
    {
        if (!_fullbrightActive)
        {
            return;
        }

        // Restore original lighting settings
        RenderSettings.ambientLight = _originalAmbientLight;
        RenderSettings.ambientIntensity = _originalAmbientIntensity;

        _fullbrightActive = false;
        PlanBuildPlugin.Log.LogInfo("Fullbright disabled.");
    }

    public static void Update()
    {
        if (!_fullbrightActive)
        {
            return;
        }

        // Only apply fullbright if player has hammer equipped (prevent cheating)
        if (!IsHammerEquipped())
        {
            return;
        }

        // Only update if EnvMan has changed the lighting (performance optimization)
        Color currentAmbient = RenderSettings.ambientLight;
        float currentIntensity = RenderSettings.ambientIntensity;

        if (currentAmbient != _lastAmbientLight || !Mathf.Approximately(currentIntensity, _lastAmbientIntensity))
        {
            ApplyFullbright();
        }
    }

    private static bool IsHammerEquipped()
    {
        return PlanBuildItemUtils.IsHammerEquipped(Player.m_localPlayer);
    }

    private static void ApplyFullbright()
    {
        RenderSettings.ambientLight = Color.white;
        RenderSettings.ambientIntensity = 1.0f;
        
        _lastAmbientLight = Color.white;
        _lastAmbientIntensity = 1.0f;
    }
}
