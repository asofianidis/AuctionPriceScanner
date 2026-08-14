using SpiritVale.ModConfig;

namespace NevarisAuctionPriceScanner;

internal static class McmIntegration
{
    internal static void Register()
    {
        if (ModConfigMenu.Version < 2)
            throw new InvalidOperationException("Auction Price Scanner requires Mod Configuration Menu API 2 or newer.");

        ModPage page = ModConfigMenu.Register(
            Plugin.Guid,
            Plugin.Name,
            "Check current auction prices for the item under your cursor.",
            Plugin.Version);

        page.AddHeader("HOTKEY", "Hold any modifiers, press the main key last, then release.");
        page.BindHotkey(Plugin.Settings.ScanHotkey, "Scan hovered item",
            "Search the auction house, or reuse that item's result if it is less than one minute old.");

        page.AddHeader("DISPLAY", "Changes apply immediately.");
        page.BindFloatSlider(Plugin.Settings.ResultDuration, "Result duration",
            2f, 20f, 1f, "0s", "How long a result remains on screen.");
        page.BindFloatSlider(Plugin.Settings.UiScale, "Result scale",
            0.65f, 1.5f, 0.05f, "0.00x", "Resize the price result panel.",
            _ => PriceScannerController.ApplyConfiguredScale());
        page.BindToggle(Plugin.Settings.ShowCacheAge, "Show cache age",
            "Identify cached results and show their age.");

        page.AddHeader("NETWORK", "Auction searches use SpiritVale's player RPC, not a direct HTTP call.");
        page.BindFloatSlider(Plugin.Settings.RequestTimeout, "Page timeout",
            15f, 120f, 5f, "0s",
            "Maximum wait for each auction result page. Every returned page resets the timer.");
        page.BindToggle(Plugin.Settings.TraceRequests, "Write request trace",
            "Log cache decisions, RPC parameters, response status, result counts, and elapsed time to BepInEx/LogOutput.log.");

        page.AddHeader("CACHE", "Successful results are cached per item for exactly one minute.");
        page.AddButton("ClearCache", "Clear price cache", PriceScannerController.ClearCache,
            "Discard all cached auction results now.");

        Plugin.Info("Registered the Auction Price Scanner page with Mod Configuration Menu.");
    }
}
