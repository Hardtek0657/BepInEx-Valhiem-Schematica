using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimPlanBuild;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class PlanBuildPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "local.valheim.planbuild";
    public const string PluginName = "Valheim Plan Build";
    public const string PluginVersion = "0.1.0";

    internal static ConfigEntry<bool> ModEnabled = null!;
    internal static ConfigEntry<bool> RelayEnabled = null!;
    internal static ConfigEntry<string> RelayServerUrl = null!;
    internal static ConfigEntry<float> RelayReconnectSeconds = null!;
    internal static ConfigEntry<float> RemoveAimDistance = null!;
    internal static ManualLogSource Log = null!;

    private static PlanBuildPlugin? _instance;
    private readonly PlanBuildWorld _world = new();
    private Harmony? _harmony;

    internal static bool PlanningEnabled { get; private set; }
    internal static string CurrentPlanName { get; private set; } = "";
    internal static PlanBuildWorld World => _instance!._world;

    private void Awake()
    {
        _instance = this;
        Log = Logger;
        ModEnabled = Config.Bind("General", "Enabled", true, "Enable this mod.");
        RelayEnabled = Config.Bind("Relay", "Enabled", false, "Connect to the WebSocket plan-build relay.");
        RelayServerUrl = Config.Bind("Relay", "ServerUrl", "wss://127.0.0.1:5001/planbuild", "WebSocket relay endpoint.");
        RelayReconnectSeconds = Config.Bind("Relay", "ReconnectSeconds", 5f, "Seconds between relay reconnect attempts.");
        RemoveAimDistance = Config.Bind("Planning", "RemoveAimDistance", 4f, "Maximum distance from your crosshair hit point for /planbuild remove.");
        MigrateRelayUrl();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(PlanBuildPlugin).Assembly);
        PlanBuildRelayClient.Start();
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Use /planbuild to toggle planning mode.");
    }

    internal static bool IsRelayEnabled()
    {
        return true;
    }

    private void MigrateRelayUrl()
    {
        if (!RelayServerUrl.Value.StartsWith("ws://127.0.0.1:5001", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RelayServerUrl.Value = "wss://" + RelayServerUrl.Value.Substring("ws://".Length);
        Config.Save();
        Logger.LogInfo("Updated plan build relay URL to use wss.");
    }

    private void Update()
    {
        PlanBuildRelayClient.Update(Time.deltaTime);
        PlanBuildFogController.Update();
        PlanBuildFullbright.Update(); // Continuously enforce fullbright when active
        PlanBuildMaterializerInput.CheckMaterializeInput(); // Check for E key to materialize ghost pieces
    }

    internal static bool TryHandleChatCommand(Chat chat, string text)
    {
        if (_instance == null)
        {
            return false;
        }

        string trimmed = text.Trim();
        if (!trimmed.StartsWith("/planbuild", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            SetPlanningEnabled(!PlanningEnabled);
            chat.AddString($"Plan build mode {(PlanningEnabled ? "enabled" : "disabled")}. Planned pieces: {World.Count}");
            return true;
        }

        string command = parts[1].ToLowerInvariant();
        switch (command)
        {
            case "on":
            case "enable":
                SetPlanningEnabled(true);
                chat.AddString("Plan build mode enabled.");
                return true;
            case "off":
            case "disable":
                SetPlanningEnabled(false);
                chat.AddString("Plan build mode disabled.");
                return true;
            case "status":
                chat.AddString($"Plan build mode {(PlanningEnabled ? "enabled" : "disabled")}. Plan: {FormatPlanName()}. Planned pieces: {World.Count}. Relay: {PlanBuildRelayClient.StatusText}");
                return true;
            case "reconnect":
                PlanBuildRelayClient.ForceReconnect();
                chat.AddString("Plan build relay reconnect requested.");
                return true;
            case "clear":
                World.Clear(localChange: true);
                CurrentPlanName = "";
                chat.AddString("Cleared planned pieces.");
                return true;
            case "create":
                CreateCommand(chat, parts);
                return true;
            case "remove":
                if (TryRemoveAimedPlannedPiece())
                {
                    chat.AddString("Removed planned piece.");
                }
                else
                {
                    chat.AddString("No planned piece found near the crosshair.");
                }
                return true;
            case "save":
                SaveCommand(chat, parts);
                return true;
            case "load":
                LoadCommand(chat, parts);
                return true;
            case "help":
                ShowHelp(chat);
                return true;
            default:
                ShowHelp(chat);
                return true;
        }
    }

    private static void SetPlanningEnabled(bool enabled)
    {
        PlanningEnabled = enabled;
        World.SetVisible(enabled);
        
        // Enable/disable fullbright for better visibility
        if (enabled)
        {
            PlanBuildFogController.Enable();
            PlanBuildFullbright.Enable();
        }
        else
        {
            PlanBuildFogController.Disable();
            PlanBuildFullbright.Disable();
        }
    }

    internal static bool TryPlanPlacement(Player player, Piece selectedPiece)
    {
        if (!ModEnabled.Value || !PlanningEnabled || selectedPiece == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(CurrentPlanName))
        {
            player.Message(MessageHud.MessageType.Center, "Load or create a plan before placing ghost pieces.");
            return true;
        }

        if (!PlanBuildRelayClient.CanSendRealtimeChanges)
        {
            player.Message(MessageHud.MessageType.Center, "Plan build relay is not connected.");
            return true;
        }

        if (!PlanBuildReflection.TryRefreshPlacement(player, flashGuardStone: true))
        {
            return true;
        }

        Player.PlacementStatus status = player.GetPlacementStatus();
        if (status != Player.PlacementStatus.Valid)
        {
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return true;
        }

        GameObject? ghost = PlanBuildReflection.GetPlacementGhost(player);
        if (ghost == null)
        {
            player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
            return true;
        }

        PlanPiece plan = new()
        {
            Id = NewPieceId(),
            Prefab = selectedPiece.gameObject.name,
            Position = ghost.transform.position,
            Rotation = ghost.transform.rotation,
            Owner = GetPlayerName(),
            CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (World.AddOrUpdate(plan, localChange: true))
        {
            PlanBuildRelayClient.SendPlace(plan);
            player.Message(MessageHud.MessageType.TopLeft, $"Planned {plan.Prefab}");
        }

        return true;
    }

    internal static bool TryRemoveAimedPlannedPiece()
    {
        if (!ModEnabled.Value || !PlanningEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(CurrentPlanName))
        {
            return false;
        }

        if (!PlanBuildRelayClient.CanSendRealtimeChanges)
        {
            return false;
        }

        if (!World.RemoveAimedPiece(RemoveAimDistance.Value, out string removedId))
        {
            return false;
        }

        PlanBuildRelayClient.SendRemove(removedId);
        return true;
    }

    private static void SaveCommand(Chat chat, IReadOnlyList<string> parts)
    {
        if (parts.Count < 3)
        {
            chat.AddString("Usage: /planbuild save <name>");
            return;
        }

        string name = SanitizeName(parts[2]);
        if (name.Length == 0)
        {
            chat.AddString("Save name must contain letters, numbers, dash, or underscore.");
            return;
        }

        CurrentPlanName = name;
        PlanBuildRelayClient.SendSave(name, World.Snapshot());
        chat.AddString($"Saved {World.Count} planned pieces to the relay as {name}.");
    }

    private static void CreateCommand(Chat chat, IReadOnlyList<string> parts)
    {
        if (parts.Count < 3)
        {
            chat.AddString("Usage: /planbuild create <name>");
            return;
        }

        if (!string.IsNullOrWhiteSpace(CurrentPlanName) || World.Count > 0)
        {
            chat.AddString($"A plan is already loaded ({FormatPlanName()}). Use /planbuild clear before creating a new one.");
            return;
        }

        string name = SanitizeName(parts[2]);
        if (name.Length == 0)
        {
            chat.AddString("Plan name must contain letters, numbers, dash, or underscore.");
            return;
        }

        CurrentPlanName = name;
        SetPlanningEnabled(true);
        chat.AddString($"Created plan {name}. Plan build mode enabled. Use /planbuild save {name} to save it to the relay.");
    }

    private static void LoadCommand(Chat chat, IReadOnlyList<string> parts)
    {
        if (parts.Count < 3)
        {
            chat.AddString("Usage: /planbuild load <name>");
            return;
        }

        string name = SanitizeName(parts[2]);
        PlanBuildRelayClient.SendLoadRequest(name);
        chat.AddString($"Requested plan build save {name} from the relay.");
    }

    internal static void LoadRemoteSave(string name, IReadOnlyList<PlanPiece> pieces)
    {
        World.Replace(pieces, localChange: false);
        CurrentPlanName = SanitizeName(name);
        if (Chat.instance)
        {
            Chat.instance.AddString($"Loaded relay plan {name}: {pieces.Count} pieces.");
        }
    }

    private static void ShowHelp(Chat chat)
    {
        chat.AddString("Usage: /planbuild, /planbuild create <name>, /planbuild save <name>, /planbuild load <name>, /planbuild remove, /planbuild clear, /planbuild status, /planbuild reconnect");
    }

    private static string FormatPlanName()
    {
        return string.IsNullOrWhiteSpace(CurrentPlanName) ? "none" : CurrentPlanName;
    }

    private static string SanitizeName(string value)
    {
        char[] chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars).Trim('_');
    }

    private static string NewPieceId()
    {
        return ZNet.GetUID().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
    }

    private static string GetPlayerName()
    {
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer)
        {
            string name = localPlayer.GetPlayerName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return "Unknown";
    }

    private void OnDestroy()
    {
        PlanBuildRelayClient.Stop();
        PlanBuildFogController.Disable();
        _world.Clear(localChange: false);
        _harmony?.UnpatchSelf();
        _harmony = null;
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}
