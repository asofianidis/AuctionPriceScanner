using System.Globalization;
using _App.Scripts.Service;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ContractItemType = SpiritVale.Vending.Contracts.ItemType;
using ListingDto = SpiritVale.Vending.Contracts.ListingDto;
using ListingStatus = SpiritVale.Vending.Contracts.ListingStatus;
using ResultCode = SpiritVale.Vending.Contracts.ResultCode;
using SearchRequest = SpiritVale.Vending.Contracts.SearchRequest;
using Il2CppRaycastList = Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>;
using Object = UnityEngine.Object;

namespace NevarisAuctionPriceScanner;

internal static class PriceScannerController
{
    private const double CacheLifetimeSeconds = 60d;
    private const int SearchLimit = 100;
    private const int SearchPageSize = 40;
    private const float PanelWidth = 420f;
    private const float PanelHeight = 146f;

    private static readonly System.Collections.Generic.Dictionary<string, CachedResult> Cache =
        new(StringComparer.Ordinal);
    private static GameObject _canvasObject;
    private static GameObject _panelObject;
    private static Canvas _canvas;
    private static RectTransform _panelRect;
    private static TextMeshProUGUI _titleText;
    private static TextMeshProUGUI _bodyText;
    private static GameObject _sourceContext;
    private static bool _sourceContextBound;
    private static SearchSession _searchSession;
    private static PlayerController _localPlayer;
    private static string _requestKey = string.Empty;
    private static bool _worldReady;
    private static bool _requestInFlight;
    private static float _requestStartedAt;
    private static float _hideAt;
    private static int _lastTickFrame = -1;
    private static int _nextRequestId;

    internal static void Initialize() { }

    internal static void EnterWorld(PlayerController localPlayer)
    {
        _localPlayer = localPlayer;
        _worldReady = true;
        EnsureUi();
        HidePanel();
    }

    internal static void LeaveWorld()
    {
        _worldReady = false;
        _localPlayer = null;
        FinishSearch();
        DestroyUi();
    }

    internal static void Shutdown()
    {
        _worldReady = false;
        DestroyUi();
        ClearCache();
        _searchSession = null;
        _localPlayer = null;
    }

    internal static void Tick()
    {
        if (!_worldReady || _lastTickFrame == Time.frameCount)
            return;
        _lastTickFrame = Time.frameCount;

        EnsureUi();
        DispatchQueuedPage();
        PollSearchTimeout();
        if (_panelObject is not null && _panelObject.activeSelf && !IsSourceContextActive())
        {
            Trace("UI CONTEXT CLOSED cardHidden=true");
            HidePanel();
        }
        if (_panelObject is not null && _panelObject.activeSelf && Time.unscaledTime >= _hideAt)
            HidePanel();

        if (IsTextInputFocused() || !Plugin.Settings.ScanHotkey.Value.IsDown())
            return;

        ScanHoveredItem();
    }

    internal static void ApplyConfiguredScale()
    {
        if (_panelRect is null || _canvas is null)
            return;
        float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float availableWidth = Mathf.Max(1f, Screen.width / canvasScale - 16f);
        float availableHeight = Mathf.Max(1f, Screen.height / canvasScale - 16f);
        float fitScale = Mathf.Min(availableWidth / PanelWidth, availableHeight / PanelHeight);
        float scale = Mathf.Min(Mathf.Clamp(Plugin.Settings.UiScale.Value, 0.65f, 1.5f), fitScale);
        scale = Mathf.Clamp(scale, 0.4f, 1.5f);
        _panelRect.localScale = new Vector3(scale, scale, 1f);
        PositionNearCursor();
    }

    internal static void ClearCache()
    {
        Cache.Clear();
        Plugin.Info("Auction price cache cleared.");
    }

    private static void ScanHoveredItem()
    {
        if (!TryGetHoveredItem(out HoveredItem hovered))
        {
            _sourceContext = null;
            _sourceContextBound = false;
            ShowMessage("NO ITEM", "Move the cursor over an inventory or equipment item, then press the scan hotkey.", 3f);
            return;
        }

        _sourceContext = hovered.Context;
        _sourceContextBound = _sourceContext is not null;
        ContractItemType type = hovered.Type;

        string itemId = hovered.Data.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemId))
        {
            ShowMessage(hovered.DisplayName, "The hovered item has no searchable item ID.", 3f);
            return;
        }

        string key = BuildCacheKey(type, itemId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(key, out CachedResult cached))
        {
            double age = Math.Max(0d, (now - cached.FetchedAt).TotalSeconds);
            if (age < CacheLifetimeSeconds)
            {
                Trace($"CACHE HIT rpcSent=false itemId={Quote(itemId)} itemType={type} ageSeconds={age:0.0} listings={cached.Count}");
                ShowResult(cached, true, age);
                return;
            }
            Trace($"CACHE EXPIRED rpcSent=false itemId={Quote(itemId)} itemType={type} ageSeconds={age:0.0}");
            Cache.Remove(key);
        }

        if (_requestInFlight)
        {
            string detail = string.Equals(_requestKey, key, StringComparison.Ordinal)
                ? "That item is already being scanned."
                : "Wait for the current auction scan to finish, then try again.";
            ShowMessage(hovered.DisplayName, detail, 3f);
            return;
        }

        _requestInFlight = true;
        _requestKey = key;
        ShowMessage(hovered.DisplayName, "Scanning active auction listings…", 20f);
        _searchSession = new SearchSession(++_nextRequestId, key, itemId, hovered.DisplayName, type);
        RequestNextPage();
    }

    private static void RequestNextPage()
    {
        try
        {
            if (_localPlayer is null || !_localPlayer.IsSpawned || !_localPlayer.IsOwner)
            {
                FailSearch("The local player is not ready to send an auction request.");
                return;
            }

            var request = new SearchRequest
            {
                Query = _searchSession.DisplayName,
                ItemType = new Il2CppSystem.Nullable<ContractItemType>(_searchSession.Type),
                PageSize = Math.Min(SearchPageSize, SearchLimit - _searchSession.Inspected),
                Cursor = _searchSession.Cursor
            };
            int pageNumber = ++_searchSession.PageNumber;
            _requestStartedAt = Time.unscaledTime;
            Il2CppSystem.Action<VendingManager.VendingSearchPage> callback =
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<
                    Il2CppSystem.Action<VendingManager.VendingSearchPage>>(
                    new System.Action<VendingManager.VendingSearchPage>(OnSearchPage));
            _localPlayer.RequestVendorItemList(request, callback);
            Trace($"RPC SEND rpcSent=true request={_searchSession.RequestId} page={pageNumber} "
                + $"method=PlayerController.RequestVendorItemList query={Quote(request.Query)} "
                + $"itemId={Quote(_searchSession.ItemId)} itemType={_searchSession.Type} "
                + $"pageSize={request.PageSize} cursor={DescribeCursor(request.Cursor)} filters=none "
                + $"timeoutSeconds={TimeoutSeconds:0}");
            if (IsSourceContextActive())
            {
                ShowMessage(_searchSession.DisplayName,
                    $"Auction request #{_searchSession.RequestId} sent · page {pageNumber}\n"
                    + $"Waiting up to {TimeoutSeconds:0} seconds for the game server…",
                    TimeoutSeconds + 2f);
            }
        }
        catch (Exception ex)
        {
            Trace($"RPC SEND FAILED rpcSent=false request={_searchSession?.RequestId ?? 0} "
                + $"page={_searchSession?.PageNumber ?? 0} error={Quote(ex.Message)}");
            FailSearch($"The auction search could not be started: {ex.Message}");
        }
    }

    private static void PollSearchTimeout()
    {
        if (!_requestInFlight || Time.unscaledTime - _requestStartedAt < TimeoutSeconds)
            return;
        Trace($"RPC TIMEOUT request={_searchSession?.RequestId ?? 0} "
            + $"page={_searchSession?.PageNumber ?? 0} elapsedSeconds={Time.unscaledTime - _requestStartedAt:0.0}");
        FailSearch($"The game server did not return auction page {_searchSession?.PageNumber ?? 0} "
            + $"within {TimeoutSeconds:0} seconds.");
    }

    private static void DispatchQueuedPage()
    {
        if (!_requestInFlight || _searchSession is null || !_searchSession.NextPageQueued
            || Time.frameCount <= _searchSession.PageQueuedAtFrame)
            return;

        _searchSession.NextPageQueued = false;
        Trace($"RPC PAGE DISPATCH request={_searchSession.RequestId} "
            + $"nextPage={_searchSession.PageNumber + 1} frame={Time.frameCount}");
        RequestNextPage();
    }

    private static void OnSearchPage(VendingManager.VendingSearchPage page)
    {
        if (!_requestInFlight || _searchSession is null)
            return;

        if (page is null)
        {
            Trace($"RPC RECV request={_searchSession.RequestId} page={_searchSession.PageNumber} response=null");
            FailSearch("The game server returned an empty auction response.");
            return;
        }

        float elapsed = Mathf.Max(0f, Time.unscaledTime - _requestStartedAt);
        int returnedItems = page.Items?.Count ?? 0;

        if (!page.Success || page.Code != ResultCode.Success)
        {
            Trace($"RPC RECV request={_searchSession.RequestId} page={_searchSession.PageNumber} "
                + $"success={page.Success} code={page.Code} items={returnedItems} hasMore={page.HasMore} "
                + $"elapsedMs={elapsed * 1000f:0} message={Quote(page.Message)}");
            string reason = page.Message;
            if (string.IsNullOrWhiteSpace(reason))
                reason = $"Auction search failed ({page.Code}).";
            FailSearch(reason);
            return;
        }

        int matchesBeforePage = _searchSession.Prices.Count;
        if (page.Items is not null)
        {
            foreach (VendingManager.VendingItemData vendingItem in page.Items)
            {
                if (_searchSession.Inspected >= SearchLimit)
                    break;
                ListingDto listing = vendingItem?.Listing;
                _searchSession.Inspected++;
                if (listing is null || listing.Status != ListingStatus.Active
                    || listing.AvailableQuantity <= 0 || listing.UnitPrice < 0
                    || listing.Item is null
                    || !string.Equals(listing.Item.ItemId, _searchSession.ItemId, StringComparison.Ordinal))
                    continue;
                _searchSession.Prices.Add(listing.UnitPrice);
            }
        }

        int pageMatches = _searchSession.Prices.Count - matchesBeforePage;
        Trace($"RPC RECV request={_searchSession.RequestId} page={_searchSession.PageNumber} "
            + $"success=true code={page.Code} items={returnedItems} exactMatches={pageMatches} "
            + $"hasMore={page.HasMore} nextCursor={DescribeCursor(page.NextCursor)} elapsedMs={elapsed * 1000f:0}");

        string next = page.NextCursor;
        if (page.HasMore && _searchSession.Inspected < SearchLimit
            && !string.IsNullOrWhiteSpace(next)
            && !string.Equals(next, _searchSession.Cursor, StringComparison.Ordinal))
        {
            _searchSession.Cursor = next;
            _searchSession.NextPageQueued = true;
            _searchSession.PageQueuedAtFrame = Time.frameCount;
            Trace($"RPC PAGE QUEUED request={_searchSession.RequestId} "
                + $"nextPage={_searchSession.PageNumber + 1} queuedFrame={Time.frameCount} "
                + "dispatch=nextFrame");
            return;
        }

        CachedResult result = CachedResult.FromPrices(
            _searchSession.Key, _searchSession.ItemId, _searchSession.DisplayName,
            _searchSession.Type, _searchSession.Prices, DateTimeOffset.UtcNow,
            _searchSession.Inspected >= SearchLimit && page.HasMore);
        int completedRequestId = _searchSession.RequestId;
        Cache[result.Key] = result;
        Trace($"RPC COMPLETE request={completedRequestId} itemId={Quote(result.ItemId)} "
            + $"listings={result.Count} hitSearchLimit={result.HitSearchLimit}");
        bool showResult = IsSourceContextActive();
        FinishSearch();
        if (showResult)
            ShowResult(result, false, 0d);
        Plugin.Info($"Request #{completedRequestId} completed for '{result.ItemId}': "
            + $"{result.Count} active listings.");
    }

    private static void FailSearch(string error)
    {
        string title = _searchSession?.DisplayName ?? "AUCTION PRICE SCANNER";
        bool showError = IsSourceContextActive();
        Plugin.Warning(error);
        FinishSearch();
        if (showError)
            ShowMessage(title, error, 5f);
    }

    private static void FinishSearch()
    {
        _requestInFlight = false;
        _requestKey = string.Empty;
        _searchSession = null;
    }

    private static bool IsSourceContextActive()
    {
        if (!_sourceContextBound)
            return true;
        try { return _sourceContext is not null && _sourceContext.activeInHierarchy; }
        catch { return false; }
    }

    private static float TimeoutSeconds => Mathf.Clamp(
        Plugin.Settings.RequestTimeout.Value, 15f, 120f);

    private static void Trace(string message)
    {
        if (Plugin.Settings.TraceRequests.Value)
            Plugin.Info($"[AuctionRpc] {message}");
    }

    private static string Quote(string value)
    {
        if (value is null)
            return "<null>";
        string safe = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n");
        return $"\"{safe}\"";
    }

    private static string DescribeCursor(string cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return "<none>";
        string prefix = cursor.Length <= 12 ? cursor : cursor[..12] + "…";
        return $"{Quote(prefix)}({cursor.Length}chars)";
    }

    private static bool TryGetHoveredItem(out HoveredItem hovered)
    {
        hovered = default;
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem is null)
            return false;

        var pointer = new PointerEventData(eventSystem) { position = Input.mousePosition };
        var hits = new Il2CppRaycastList();
        eventSystem.RaycastAll(pointer, hits);
        foreach (RaycastResult hit in hits)
        {
            GameObject target = hit.gameObject;
            UIVendingSearchItem searchRow = target?.GetComponentInParent<UIVendingSearchItem>();
            ListingDto rowListing = searchRow?.Data?.Listing;
            if (rowListing is not null)
            {
                InventoryItemData rowItem = VendingUnityExtensions.GetItem(rowListing);
                if (rowItem is not null)
                {
                    string rowName = rowListing.ItemDisplayName;
                    if (string.IsNullOrWhiteSpace(rowName))
                        rowName = searchRow.Item?.Name?.text;
                    if (string.IsNullOrWhiteSpace(rowName))
                        rowName = rowItem.Id ?? "Hovered item";
                    ContractItemType rowType = rowListing.Item?.Type ?? ContractItemType.Unknown;
                    if (rowType == ContractItemType.Unknown
                        && !TryResolveItemType(rowItem, out rowType))
                        continue;
                    hovered = new HoveredItem(rowItem, StripRichText(rowName).Trim(), rowType,
                        searchRow.gameObject);
                    return true;
                }
            }

            UIInventoryItem ui = target?.GetComponentInParent<UIInventoryItem>();
            if (!TryResolveItem(ui?.Data, out InventoryItemData data, out ContractItemType type))
                continue;

            string displayName = ui.Name?.text;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = data.Id ?? "Hovered item";
            hovered = new HoveredItem(data, StripRichText(displayName).Trim(), type,
                ui.gameObject);
            return true;
        }
        return false;
    }

    // IL2CPP can materialize a concrete item through an InventoryItemData proxy.
    // Native casts preserve the actual class; managed `is` checks on that base
    // proxy do not, which made every ordinary inventory item appear unsupported.
    private static bool TryResolveItem(IInfoDrawable source, out InventoryItemData item,
        out ContractItemType type)
    {
        item = null;
        type = ContractItemType.Unknown;
        if (source is null)
            return false;

        try
        {
            if (source.TryCast<JunkData>() is { } junk)
            {
                item = junk;
                type = ContractItemType.Junk;
            }
            else if (source.TryCast<ConsumableData>() is { } consumable)
            {
                item = consumable;
                type = ContractItemType.Consumable;
            }
            else if (source.TryCast<EquipData>() is { } equip)
            {
                item = equip;
                type = ContractItemType.Equip;
            }
            else if (source.TryCast<ArtifactData>() is { } artifact)
            {
                item = artifact;
                type = ContractItemType.Artifact;
            }
            else if (source.TryCast<CardData>() is { } card)
            {
                item = card;
                type = ContractItemType.Card;
            }
            else if (source.TryCast<GemData>() is { } gem)
            {
                item = gem;
                type = ContractItemType.Gem;
            }
            else if (source.TryCast<CosmeticData>() is { } cosmetic)
            {
                item = cosmetic;
                type = ContractItemType.Cosmetic;
            }
        }
        catch { return false; }

        return item is not null;
    }

    private static bool TryResolveItemType(InventoryItemData source, out ContractItemType type)
    {
        type = ContractItemType.Unknown;
        if (source is null)
            return false;
        try
        {
            if (source.TryCast<JunkData>() is not null) type = ContractItemType.Junk;
            else if (source.TryCast<ConsumableData>() is not null) type = ContractItemType.Consumable;
            else if (source.TryCast<EquipData>() is not null) type = ContractItemType.Equip;
            else if (source.TryCast<ArtifactData>() is not null) type = ContractItemType.Artifact;
            else if (source.TryCast<CardData>() is not null) type = ContractItemType.Card;
            else if (source.TryCast<GemData>() is not null) type = ContractItemType.Gem;
            else if (source.TryCast<CosmeticData>() is not null) type = ContractItemType.Cosmetic;
        }
        catch { return false; }
        return type != ContractItemType.Unknown;
    }

    private static bool IsTextInputFocused()
    {
        try
        {
            GameObject selected = EventSystem.current?.currentSelectedGameObject;
            TMP_InputField input = selected?.GetComponentInParent<TMP_InputField>();
            return input is not null && input.isFocused;
        }
        catch { return false; }
    }

    private static void ShowResult(CachedResult result, bool fromCache, double age)
    {
        EnsureUi();
        _titleText.text = result.DisplayName.ToUpperInvariant();
        string source = string.Empty;
        if (Plugin.Settings.ShowCacheAge.Value)
        {
            source = fromCache
                ? $"<color=#9AA9A9>CACHE · {Math.Floor(age):0}s OLD</color>\n"
                : "<color=#9AA9A9>LIVE · CACHED FOR 60s</color>\n";
        }

        if (result.Count == 0)
        {
            _bodyText.text = source + "<color=#D5DADA>No active listings found.</color>";
        }
        else
        {
            string limited = result.HitSearchLimit ? " · FIRST 100 RESULTS" : string.Empty;
            _bodyText.text = source
                + $"<color=#9AA9A9>UNIT PRICE · {result.Count:N0} LISTING{(result.Count == 1 ? string.Empty : "S")}{limited}</color>\n"
                + $"<color=#E6D09E>MIN</color>  {FormatCoins(result.Minimum)}     "
                + $"<color=#E6D09E>AVG</color>  {FormatAverage(result.Average)}     "
                + $"<color=#E6D09E>MAX</color>  {FormatCoins(result.Maximum)}";
        }
        ShowPanel(Mathf.Clamp(Plugin.Settings.ResultDuration.Value, 2f, 20f));
    }

    private static void ShowMessage(string title, string body, float duration)
    {
        EnsureUi();
        _titleText.text = (title ?? "AUCTION PRICE SCANNER").ToUpperInvariant();
        _bodyText.text = $"<color=#D5DADA>{body}</color>";
        ShowPanel(duration);
    }

    private static void ShowPanel(float duration)
    {
        if (_panelObject is null)
            return;
        ApplyConfiguredScale();
        PositionNearCursor();
        _panelObject.SetActive(true);
        _hideAt = Time.unscaledTime + duration;
    }

    private static void HidePanel()
    {
        if (_panelObject is not null)
            _panelObject.SetActive(false);
    }

    private static void EnsureUi()
    {
        if (!_worldReady || _canvasObject is not null)
            return;

        _canvasObject = new GameObject("NevarisAuctionPriceScanner.Canvas");
        Object.DontDestroyOnLoad(_canvasObject);
        _canvas = _canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32010;
        CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _panelObject = new GameObject("PriceResult");
        _panelObject.transform.SetParent(_canvasObject.transform, false);
        _panelRect = _panelObject.AddComponent<RectTransform>();
        _panelRect.anchorMin = Vector2.zero;
        _panelRect.anchorMax = Vector2.zero;
        _panelRect.pivot = new Vector2(0f, 1f);
        _panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        Image background = _panelObject.AddComponent<Image>();
        background.color = new Color(0.035f, 0.045f, 0.06f, 0.96f);
        background.raycastTarget = false;
        Outline outline = _panelObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.68f, 0.54f, 0.28f, 0.78f);
        outline.effectDistance = new Vector2(1f, -1f);

        GameObject accentObject = new("Accent");
        accentObject.transform.SetParent(_panelObject.transform, false);
        RectTransform accentRect = accentObject.AddComponent<RectTransform>();
        accentRect.anchorMin = new Vector2(0f, 1f);
        accentRect.anchorMax = new Vector2(1f, 1f);
        accentRect.pivot = new Vector2(0.5f, 1f);
        accentRect.sizeDelta = new Vector2(0f, 3f);
        Image accent = accentObject.AddComponent<Image>();
        accent.color = new Color(0.73f, 0.58f, 0.3f, 0.95f);
        accent.raycastTarget = false;

        _titleText = CreateText("Title", 15f, FontStyles.Bold);
        RectTransform titleRect = _titleText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = new Vector2(14f, -43f);
        titleRect.offsetMax = new Vector2(-14f, -9f);
        _titleText.color = new Color(0.91f, 0.82f, 0.62f, 1f);
        _titleText.alignment = TextAlignmentOptions.MidlineLeft;

        _bodyText = CreateText("Body", 13f, FontStyles.Normal);
        RectTransform bodyRect = _bodyText.rectTransform;
        bodyRect.anchorMin = Vector2.zero;
        bodyRect.anchorMax = Vector2.one;
        bodyRect.offsetMin = new Vector2(14f, 12f);
        bodyRect.offsetMax = new Vector2(-14f, -48f);
        _bodyText.color = new Color(0.86f, 0.89f, 0.89f, 1f);
        _bodyText.alignment = TextAlignmentOptions.TopLeft;
        _bodyText.lineSpacing = 4f;
        _bodyText.textWrappingMode = TextWrappingModes.Normal;

        ApplyConfiguredScale();
        HidePanel();
    }

    private static TextMeshProUGUI CreateText(string name, float size, FontStyles style)
    {
        GameObject textObject = new(name);
        textObject.transform.SetParent(_panelObject.transform, false);
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.fontStyle = style;
        text.richText = true;
        text.raycastTarget = false;
        return text;
    }

    private static void PositionNearCursor()
    {
        if (_panelRect is null || _canvas is null)
            return;
        float canvasScale = Mathf.Max(0.01f, _canvas.scaleFactor);
        float userScale = _panelRect.localScale.x;
        Vector2 mouse = (Vector2)Input.mousePosition / canvasScale;
        float screenWidth = Screen.width / canvasScale;
        float screenHeight = Screen.height / canvasScale;
        float width = PanelWidth * userScale;
        float height = PanelHeight * userScale;
        Vector2 position = mouse + new Vector2(22f, -18f);
        if (position.x + width > screenWidth - 8f)
            position.x = mouse.x - width - 22f;
        position.x = Mathf.Clamp(position.x, 8f, Mathf.Max(8f, screenWidth - width - 8f));
        position.y = Mathf.Clamp(position.y, height + 8f, screenHeight - 8f);
        _panelRect.anchoredPosition = position;
    }

    private static void DestroyUi()
    {
        if (_canvasObject is not null)
            Object.Destroy(_canvasObject);
        _canvasObject = null;
        _panelObject = null;
        _canvas = null;
        _panelRect = null;
        _titleText = null;
        _bodyText = null;
        _sourceContext = null;
        _sourceContextBound = false;
    }

    private static string BuildCacheKey(ContractItemType type, string itemId) => $"{(int)type}\0{itemId}";

    private static string FormatCoins(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatAverage(decimal value) => value.ToString("#,0.##", CultureInfo.CurrentCulture);

    private static string StripRichText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var result = new System.Text.StringBuilder(value.Length);
        bool inTag = false;
        foreach (char character in value)
        {
            if (character == '<') { inTag = true; continue; }
            if (character == '>') { inTag = false; continue; }
            if (!inTag) result.Append(character);
        }
        return result.ToString();
    }

    private readonly struct HoveredItem
    {
        internal HoveredItem(InventoryItemData data, string displayName, ContractItemType type,
            GameObject context)
        {
            Data = data;
            DisplayName = displayName;
            Type = type;
            Context = context;
        }
        internal InventoryItemData Data { get; }
        internal string DisplayName { get; }
        internal ContractItemType Type { get; }
        internal GameObject Context { get; }
    }

    private sealed class CachedResult
    {
        internal string Key;
        internal string ItemId;
        internal string DisplayName;
        internal ContractItemType Type;
        internal int Count;
        internal long Minimum;
        internal long Maximum;
        internal decimal Average;
        internal DateTimeOffset FetchedAt;
        internal bool HitSearchLimit;

        internal static CachedResult FromPrices(string key, string itemId, string displayName,
            ContractItemType type, System.Collections.Generic.List<long> prices,
            DateTimeOffset fetchedAt, bool hitSearchLimit)
        {
            var result = new CachedResult
            {
                Key = key,
                ItemId = itemId,
                DisplayName = displayName,
                Type = type,
                Count = prices.Count,
                FetchedAt = fetchedAt,
                HitSearchLimit = hitSearchLimit
            };
            if (prices.Count == 0)
                return result;

            long min = long.MaxValue;
            long max = long.MinValue;
            decimal sum = 0m;
            foreach (long price in prices)
            {
                min = Math.Min(min, price);
                max = Math.Max(max, price);
                sum += price;
            }
            result.Minimum = min;
            result.Maximum = max;
            result.Average = sum / prices.Count;
            return result;
        }
    }

    private sealed class SearchSession
    {
        internal SearchSession(int requestId, string key, string itemId, string displayName,
            ContractItemType type)
        {
            RequestId = requestId;
            Key = key;
            ItemId = itemId;
            DisplayName = displayName;
            Type = type;
        }

        internal readonly int RequestId;
        internal readonly string Key;
        internal readonly string ItemId;
        internal readonly string DisplayName;
        internal readonly ContractItemType Type;
        internal readonly System.Collections.Generic.List<long> Prices = new();
        internal string Cursor;
        internal int Inspected;
        internal int PageNumber;
        internal int PageQueuedAtFrame;
        internal bool NextPageQueued;
    }
}
