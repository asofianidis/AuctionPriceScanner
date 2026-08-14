using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using HarmonyLib;
using SpiritVale.ModConfig;
using UnityEngine;

namespace NevarisAuctionPriceScanner;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency(ModConfigPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "alex.nevaris.auctionpricescanner";
    public const string Name = "Auction Price Scanner";
    public const string Version = "1.1.2";

    internal static ManualLogSource Logger = null!;
    internal static ScannerSettings Settings = null!;
    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Settings = new ScannerSettings(Config);
        McmIntegration.Register();

        _harmony = new Harmony(Guid);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        PriceScannerController.Initialize();
        Info($"{Name} {Version} loaded. Hover an item and press {Settings.ScanHotkey.Value}.");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        ModConfigMenu.Unregister(Guid);
        PlayerWorldGate.Shutdown();
        PriceScannerController.Shutdown();
        return base.Unload();
    }

    internal static void Info(string message) => Logger.LogInfo($"[AuctionPriceScanner] {message}");
    internal static void Warning(string message) => Logger.LogWarning($"[AuctionPriceScanner] {message}");
    internal static void Error(string message) => Logger.LogError($"[AuctionPriceScanner] {message}");
}

internal sealed class ScannerSettings
{
    internal readonly ConfigEntry<KeyboardShortcut> ScanHotkey;
    internal readonly ConfigEntry<float> ResultDuration;
    internal readonly ConfigEntry<float> UiScale;
    internal readonly ConfigEntry<bool> ShowCacheAge;
    internal readonly ConfigEntry<float> RequestTimeout;
    internal readonly ConfigEntry<bool> TraceRequests;

    internal ScannerSettings(ConfigFile config)
    {
        ScanHotkey = config.Bind("Hotkeys", "Scan", new KeyboardShortcut(KeyCode.A, KeyCode.LeftControl),
            "Scan the auction house for the item under the mouse cursor.");
        ResultDuration = config.Bind("Display", "ResultDurationSeconds", 8f,
            "How long the price result remains visible.");
        UiScale = config.Bind("Display", "Scale", 1f,
            "Scale the auction price result panel.");
        ShowCacheAge = config.Bind("Display", "ShowCacheAge", true,
            "Show whether a result came from the one-minute cache and how old it is.");
        RequestTimeout = config.Bind("Network", "RequestTimeoutSeconds", 45f,
            "Seconds to wait for each auction result page before reporting a timeout.");
        TraceRequests = config.Bind("Network", "TraceRequests", true,
            "Write auction RPC request and response details to the BepInEx log.");
    }
}
