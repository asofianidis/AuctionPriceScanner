using HarmonyLib;

namespace NevarisAuctionPriceScanner;

[HarmonyPatch(typeof(PlayerController), "Update")]
internal static class RuntimeTickPatch
{
    private static bool _loggedFailure;

    private static void Postfix(PlayerController __instance)
    {
        try
        {
            if (!__instance.IsSpawned || !__instance.IsOwner)
                return;
            PlayerWorldGate.Tick(__instance);
        }
        catch (Exception ex)
        {
            if (_loggedFailure)
                return;
            _loggedFailure = true;
            Plugin.Error($"Auction scanner world gate disabled after an update error: {ex}");
        }
    }
}
