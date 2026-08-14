# Auction Price Scanner

Hover any inventory, equipment, or auction item and press **Left Ctrl+A** to search the
SpiritVale auction house. The result panel reports the minimum, maximum, and arithmetic
average unit sell price across active listings for that exact item ID.

Successful searches, including searches with no listings, are cached for exactly 60 seconds.
Pressing the shortcut again during that minute displays the cached result without sending
another auction request.

## Configuration

Open **Settings → Mods → Auction Price Scanner** to change the scan hotkey, result duration,
panel scale, per-page network timeout, request tracing, or cache-age label. The same page also
includes a button to clear the cache. The default timeout is 45 seconds and can be set from
15 to 120 seconds.

When request tracing is enabled, `BepInEx/LogOutput.log` distinguishes cache hits from actual
network traffic. Each real call records the request number, page, RPC method, query, exact item
ID and type, page size, cursor presence, response code, returned and matching item counts,
pagination state, and elapsed time. Searches use SpiritVale's
`PlayerController.RequestVendorItemList` FishNet RPC rather than a direct HTTP endpoint.

## Build

```powershell
dotnet build .\AuctionPriceScanner\AuctionPriceScanner.csproj -c Release
```

Copy `bin\Release\net6.0\NevarisAuctionPriceScanner.dll` into a folder under
`SpiritVale\BepInEx\plugins`. Mod Configuration Menu API 2 or newer is required.

The auction service returns at most 100 search results. If that cap is reached, the panel
labels the result as based on the first 100 results.
