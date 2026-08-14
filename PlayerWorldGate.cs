using UnityEngine;

namespace NevarisAuctionPriceScanner;

internal static class PlayerWorldGate
{
    private const float ReadyGraceSeconds = 0.5f;
    private static PlayerController _localPlayer;
    private static float _readySince = -1f;
    private static bool _enteredWorld;

    internal static void Tick(PlayerController player)
    {
        bool ready;
        try
        {
            ready = player.IsSpawned && player.IsCharacterLoaded && player.gameObject.activeInHierarchy;
        }
        catch { ready = false; }

        if (!ready)
        {
            if (SamePlayer(_localPlayer, player))
                LeaveWorld();
            return;
        }

        if (!SamePlayer(_localPlayer, player))
        {
            LeaveWorld();
            _localPlayer = player;
            _readySince = -1f;
        }

        if (_readySince < 0f)
        {
            _readySince = Time.unscaledTime;
            return;
        }

        if (!_enteredWorld)
        {
            if (Time.unscaledTime - _readySince < ReadyGraceSeconds)
                return;
            _enteredWorld = true;
            PriceScannerController.EnterWorld(player);
        }

        PriceScannerController.Tick();
    }

    internal static void Shutdown()
    {
        LeaveWorld();
        _localPlayer = null;
        _readySince = -1f;
    }

    private static void LeaveWorld()
    {
        if (!_enteredWorld)
            return;
        _enteredWorld = false;
        _readySince = -1f;
        PriceScannerController.LeaveWorld();
    }

    private static bool SamePlayer(PlayerController left, PlayerController right)
    {
        if (left is null || right is null)
            return false;
        if (left == right)
            return true;
        try { return left.ObjectId == right.ObjectId; }
        catch { return false; }
    }
}
