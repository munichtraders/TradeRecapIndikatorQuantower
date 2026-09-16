using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Zeichnet jeden abgeschlossenen Trade auf und sendet eine gebrandete
/// Recap-Karte an Telegram. Zusätzlich: lokales CSV-Journal, zentrales
/// Server-Journal, Tages-Stats und MAE/MFE.
///
/// Quantower-Port des ATAS-Indikators (siehe 07_ATAS/Indikatoren/TradeRecap) —
/// gleiche Recap-Karte, gleiches Server-/Telegram-Backend, andere Plattform-API.
/// </summary>
public class TradeRecapIndicator : Indicator
{
    // ── Telegram ──────────────────────────────────────────────────────────

    [InputParameter("Telegram: Bot Token", 10)]
    public string BotToken = "7800685401:AAEnsF6E4dtm-4pUO-yjgiRjDQyHvOkoT64";

    [InputParameter("Telegram: Chat ID", 11)]
    public string ChatId = "-4946993985";

    // ── Journal ───────────────────────────────────────────────────────────

    [InputParameter("Journal: CSV-Pfad (z.B. C:\\Trading\\journal.csv)", 20)]
    public string CsvPath = "";

    // ── Server-Journal (zentrale CSV auf dem Munich-Traders-Server) ───────

    [InputParameter("Server-Journal: Server-URL (z.B. http://SERVER:9878/trade)", 30)]
    public string ServerUrl = "http://187.124.10.151:9878/trade";

    [InputParameter("Server-Journal: Server-Token", 31)]
    public string ServerToken = "5029729378e17dfb4284a2e75855f2ca3bd1a8a95805c94c";

    // ── Prop Firm ─────────────────────────────────────────────────────────

    [InputParameter("Prop Firm: Tages-Drawdown-Limit ($)", 40, 0, 1_000_000, 100, 0)]
    public double DailyDrawdownLimit = 0;

    [InputParameter("Prop Firm: Konto-Größe ($, Fallback)", 41, 0, 10_000_000, 100, 0)]
    public double AccountBalanceFallback = 0;

    // ── Design ────────────────────────────────────────────────────────────

    [InputParameter("Design: Logo-Pfad (PNG)", 50)]
    public string LogoPath = "";

    // Fester Trader-Roster (nur Martin/Tobi/Mario nutzen den Indikator) statt Freitext —
    // Variants-Liste macht daraus ein Dropdown im Settings-Dialog. Default für Tobi, da
    // dies die Quantower-Version ist. Dient nur als Fallback, solange der Telegram-
    // Sessioncheck (noch) nicht bestätigt wurde (siehe _sessionTraderName).
    [InputParameter("Design: Trader-Name", 51, variants: new object[] { "Martin", "Tobi", "Mario" })]
    public string TraderName = "Tobi";

    private string? _sessionTraderName;

    // ── Aktiver Trade ─────────────────────────────────────────────────────

    /// <summary>
    /// Vor Trade-Schluss in den Indikator-Einstellungen eintragen (z.B. "FOMC Scalp").
    /// Wird beim nächsten Trade-Close übernommen und danach zurückgesetzt.
    /// </summary>
    [InputParameter("Aktiver Trade: Trade-Tag", 60)]
    public string TradeTag = "";

    /// <summary>
    /// Core.TradeAdded ist ein GLOBALES Event (alle Symbole/Konten der Plattform).
    /// Leer = alle Konten dieses Symbols werden erfasst. Bei mehreren Konten auf
    /// demselben Symbol (z.B. Eval + Funded parallel) hier die gewünschte Account-Id eintragen.
    /// </summary>
    [InputParameter("Aktiver Trade: Account-ID (leer = alle)", 61)]
    public string AccountIdFilter = "";

    // ── Interne Felder ────────────────────────────────────────────────────

    private DailyStats _dailyStats = new();
    private PositionTracker _positionTracker = null!;
    private readonly CsvJournalWriter _csvWriter = new();
    private HttpClient _httpClient = null!;
    private byte[]? _logoBytes;
    private System.Timers.Timer? _connectionTimer;

    // Start-Fragebogen (Trader bestätigen, Zustandscheck, Bias) — läuft über einen
    // eigenen Polling-Timer, siehe PollCheckinUpdatesAsync. Läuft nur in der Instanz, die
    // den Flow über CheckinGate beansprucht hat (siehe OnInit) — andere Instanzen
    // übernehmen das Ergebnis passiv aus dem Gate.
    private readonly SessionCheckinFlow _checkinFlow = new();
    private readonly TelegramUpdatePoller _checkinPoller = new();
    private System.Timers.Timer? _checkinPollTimer;
    private bool _ownsCheckinFlow;
    private bool _checkinSaved; // verhindert Mehrfach-Speichern desselben abgeschlossenen Sessionchecks
    // Aktuell gültige Ampel-Warnung fürs Panel — unabhängig davon, ob sie aus dem eigenen
    // Fragebogen, dem Cache (CheckinGate) oder einer anderen Instanz stammt.
    private AmpelColor? _activeAmpel;

    // Verhindert doppelte Trade-Verarbeitung, wenn derselbe Markt in mehreren Charts
    // gleichzeitig offen ist (siehe MarketOwnerGate). Nur die Owner-Instanz verarbeitet Fills.
    private readonly Guid _instanceId = Guid.NewGuid();
    private string _symbol = "";
    private bool _isMarketOwner = true;

    // Letzter bekannter Kontostand (aus dem Trade-Fill gelesen) — Fallback-Feld überschreibt nur wenn 0
    private decimal _lastAccountBalance;

    // Zeitstempel des Indikator-Starts — historische Trades davor werden nicht verschickt
    private DateTime _initTime;

    // Fingerprints bereits verschickter Trades — Schutz gegen doppelte Zustellung
    private readonly HashSet<string> _sentTradeKeys = new();

    // Drittes Release am 2026-07-22 — VersionChecker vergleicht als int (r > c),
    // deshalb Ziffernanhang statt Buchstabensuffix, um YYMMDD-Schema kompatibel zu halten.
    private const string CurrentVersion = "20260916";

    // 0 = unbekannt, 1 = verbunden, 2 = Fehler
    private volatile int _tgStatus;

    // null = aktuell, sonst neue Versionsnummer verfügbar
    private string? _updateVersion;

    // Diagnose: wenn OnInit fehlschlägt, bleibt der Indikator sichtbar (statt vom
    // Chart zu verschwinden) und zeigt den Fehler im Status-Panel statt geräuschlos zu sterben.
    private bool _initFailed;
    private string? _initErrorMessage;
    private bool _updateErrorLogged;

    private static readonly Color _colorGold      = Color.FromArgb(255, 184, 150, 72);
    private static readonly Color _colorGreen     = Color.FromArgb(255, 34,  197, 94);
    private static readonly Color _colorRed       = Color.FromArgb(255, 239, 68,  68);
    private static readonly Color _colorYellow    = Color.FromArgb(255, 245, 158, 11);
    private static readonly Color _colorMuted     = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color _colorBg        = Color.FromArgb(210, 15,  15,  15);
    private static readonly Color _colorCardBg    = Color.FromArgb(238, 17,  16,  15);
    private static readonly Color _colorBorder    = Color.FromArgb(255, 48,  43,  34);
    private static readonly Color _colorAvatarBg  = Color.FromArgb(255, 28,  26,  22);
    private static readonly Color _colorTextPrime = Color.FromArgb(255, 236, 232, 222);
    private static readonly Color _colorTextMuted = Color.FromArgb(255, 152, 147, 138);
    private static readonly Font  _statusFont  = new("Calibri", 10f, GraphicsUnit.Pixel);
    private static readonly Font  _titleFont   = new("Calibri", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font  _smallFont   = new("Calibri", 9f,  GraphicsUnit.Pixel);
    private static readonly Font  _avatarFont  = new("Calibri", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Image? _logoImage; // aus _logoBytes, lazy für den Avatar-Badge
    private bool  _logoImageLoadAttempted;

    // Frei verschiebbares Status-Panel: Drag auf die Kopfzeile, Klick auf den Pfeil klappt
    // ein/aus. Position wird nur für die laufende Chart-Session gehalten (kein verifizierter
    // Weg, ein Feld ohne [InputParameter] dauerhaft mit dem Chart-Template zu speichern —
    // siehe CHANGELOG).
    private int   _panelX = int.MinValue; // int.MinValue = noch nie verschoben -> Standardposition oben rechts
    private int   _panelY = int.MinValue;
    private bool  _panelCollapsed;
    private bool  _isDraggingPanel;
    private Point _dragMouseStart;
    private Point _dragPanelStart;
    private Rectangle _lastPanelRect;
    private Rectangle _lastHeaderRect;
    private Rectangle _lastChevronRect;

    // ── Konstruktor ───────────────────────────────────────────────────────

    public TradeRecapIndicator() : base()
    {
        Name = "Trade Recap (Telegram)";
        Description = "Zeichnet jeden abgeschlossenen Trade auf und sendet eine gebrandete Recap-Karte an Telegram (Munich Traders).";
        SeparateWindow = false;
    }

    // ── Initialisierung ───────────────────────────────────────────────────

    protected override void OnInit()
    {
        // Alles in try/catch: eine unbehandelte Exception hier führt dazu, dass
        // Quantower den Indikator kommentarlos vom Chart entfernt. Lieber den Fehler
        // fangen, ins Quantower-Log schreiben und im Status-Panel sichtbar machen.
        try
        {
            _initTime    = DateTime.UtcNow;
            _dailyStats  = new DailyStats();
            _positionTracker = new PositionTracker(_dailyStats);
            _positionTracker.PositionClosed += OnPositionClosed;

            // HttpClient einmalig erstellen (Socket-Exhaustion vermeiden)
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            _logoBytes = TryLoadLogo(LogoPath);

            if (!string.IsNullOrWhiteSpace(CsvPath))
                _csvWriter.Initialize(CsvPath);

            Core.Instance.TradeAdded += OnTradeAdded;

            if (CurrentChart != null)
            {
                CurrentChart.MouseDown += OnChartMouseDown;
                CurrentChart.MouseMove += OnChartMouseMove;
                CurrentChart.MouseUp   += OnChartMouseUp;
            }

            // Nur eine Instanz pro Symbol verarbeitet Trades — verhindert doppelte Recap-Karten,
            // wenn derselbe Markt in mehreren Charts gleichzeitig offen ist.
            _symbol = Symbol?.Id ?? Symbol?.Name ?? "";
            _isMarketOwner = MarketOwnerGate.TryClaim(_symbol, _instanceId);

            // Sofort und dann alle 60s Telegram-Verbindung prüfen
            _ = CheckTelegramAsync();
            _connectionTimer = new System.Timers.Timer(60_000) { AutoReset = true };
            _connectionTimer.Elapsed += (_, _) => _ = CheckTelegramAsync();
            _connectionTimer.Start();

            _ = CheckVersionAsync();

            // Start-Fragebogen nur anstoßen, wenn CheckinGate diese Instanz als Fragesteller
            // beansprucht (kein aktuelles Ergebnis vorhanden, keine andere Instanz fragt gerade).
            // Sonst läuft nur das 3s-Polling weiter, das ein Ergebnis aus dem Gate übernimmt.
            _ownsCheckinFlow = CheckinGate.TryClaimFlow(out var cachedCheckin);
            if (_ownsCheckinFlow)
            {
                _ = _checkinFlow.StartAsync(TraderName, BotToken, ChatId, _httpClient);
            }
            else if (cachedCheckin != null)
            {
                _sessionTraderName = cachedCheckin.TraderName;
                _activeAmpel = cachedCheckin.Ampel is AmpelColor.Yellow or AmpelColor.Red ? cachedCheckin.Ampel : null;
            }
            _checkinPollTimer = new System.Timers.Timer(3_000) { AutoReset = true };
            _checkinPollTimer.Elapsed += (_, _) => _ = PollCheckinUpdatesAsync();
            _checkinPollTimer.Start();

            Log($"OnInit OK — Version {CurrentVersion}, Symbol {Symbol?.Name ?? "?"}, MarketOwner={_isMarketOwner}, OwnsCheckinFlow={_ownsCheckinFlow}");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initErrorMessage = ex.Message;
            LogError(ex, "OnInit fehlgeschlagen");
        }
    }

    private async Task PollCheckinUpdatesAsync()
    {
        try
        {
            if (_ownsCheckinFlow)
            {
                var updates = await _checkinPoller.PollAsync(BotToken, _httpClient).ConfigureAwait(false);
                if (updates.Count > 0)
                {
                    string? error = await _checkinFlow.ProcessUpdatesAsync(updates, BotToken, ChatId, _httpClient).ConfigureAwait(false);
                    if (error != null)
                        Log($"Sessioncheck-Antwort fehlgeschlagen: {error}", LoggingLevel.Error);
                }

                if (_checkinFlow.Result != null && !_checkinSaved)
                {
                    _checkinSaved = true;
                    _sessionTraderName = _checkinFlow.Result.TraderName;
                    CheckinGate.Save(_checkinFlow.Result);
                    _csvWriter.AppendCheckin(_checkinFlow.Result);
                    string? serverError = await TradeRecapServerSender.SendCheckinAsync(ServerUrl, ServerToken, _checkinFlow.Result, _httpClient)
                        .ConfigureAwait(false);
                    if (serverError != null)
                        Log($"Sessioncheck Server-Journal fehlgeschlagen: {serverError}", LoggingLevel.Error);
                }

                _activeAmpel = _checkinFlow.PendingAmpel is AmpelColor.Yellow or AmpelColor.Red
                    ? _checkinFlow.PendingAmpel : null;
            }
            else if (_sessionTraderName == null)
            {
                // Eigenes Ergebnis noch nicht übernommen — regelmäßig prüfen, ob die
                // fragestellende Instanz (gleicher Prozess) inzwischen fertig ist.
                if (CheckinGate.TryGetValid(out var record) && record != null)
                {
                    _sessionTraderName = record.TraderName;
                    _activeAmpel = record.Ampel is AmpelColor.Yellow or AmpelColor.Red ? record.Ampel : null;
                }
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "Sessioncheck-Polling Fehler");
        }
    }

    // ── Preis-/Tick-Updates (MAE/MFE-Tracking + Trade-Tag-Sync) ──────────

    protected override void OnUpdate(UpdateArgs args)
    {
        // Läuft bei JEDEM Tick — ungefangene Exceptions hier sind der wahrscheinlichste
        // Weg, wie ein Indikator in einer Endlosschleife crasht und vom Chart fliegt.
        try
        {
            // Läuft bei jedem OnUpdate mit, damit das zuletzt eingetragene Trade-Tag
            // spätestens beim nächsten Fill übernommen wird (kein Property-Setter verfügbar).
            _positionTracker?.SetPendingTag(TradeTag);

            if (args.Reason != UpdateReason.NewTick) return;
            if (_positionTracker?.IsPositionOpen != true) return;

            double price = Symbol?.Last ?? double.NaN;
            if (double.IsNaN(price)) price = Symbol?.Bid ?? double.NaN;
            if (!double.IsNaN(price))
                _positionTracker.UpdateMAEMFEFromTick((decimal)price);

            // Sicherheitsnetz: Kerzen-High/Low der laufenden Kerze zusätzlich zum
            // Live-Tick-Stream prüfen (siehe UpdateMAEMFEFromBar in PositionTracker.cs)
            try
            {
                _positionTracker.UpdateMAEMFEFromBar((decimal)High(0), (decimal)Low(0), Time(0));
            }
            catch { /* Kerzendaten evtl. noch nicht verfügbar */ }
        }
        catch (Exception ex)
        {
            // Nur einmal loggen — sonst flutet ein dauerhafter Fehler das Quantower-Log
            // mit hunderten Einträgen pro Sekunde.
            if (!_updateErrorLogged)
            {
                _updateErrorLogged = true;
                LogError(ex, "OnUpdate Fehler (wird nur einmal geloggt)");
            }
        }
    }

    // ── Trade-Erkennung ───────────────────────────────────────────────────

    // Core.TradeAdded ist global (alle Symbole/Konten) — auf das Symbol dieses
    // Chart-Indikators und optional eine bestimmte Account-Id einschränken.
    private void OnTradeAdded(Trade trade)
    {
        try
        {
            bool symbolMatch = SymbolMatches(trade.Symbol);
            bool accountMatch = string.IsNullOrWhiteSpace(AccountIdFilter) ||
                string.Equals(trade.Account?.Id, AccountIdFilter, StringComparison.OrdinalIgnoreCase);

            // Diagnose-Log für jeden empfangenen Fill (Trades sind selten genug, um
            // hier NICHT nur den ersten Fehler zu loggen wie bei OnUpdate).
            Log($"TradeAdded: Trade-Symbol={trade.Symbol?.Name ?? "?"} (Id={trade.Symbol?.Id}, Root={trade.Symbol?.Root}) " +
                $"vs Chart-Symbol={Symbol?.Name ?? "?"} (Id={Symbol?.Id}, Root={Symbol?.Root}) " +
                $"— SymbolMatch={symbolMatch}, AccountMatch={accountMatch} (Filter='{AccountIdFilter}', Trade-Account={trade.Account?.Id})");

            if (!symbolMatch || !accountMatch) return;

            // Passive Instanz (gleicher Markt bereits in einem anderen Chart aktiv) verarbeitet
            // keine Fills — siehe MarketOwnerGate.
            if (!_isMarketOwner) return;

            if (trade.Account?.Balance is double bal && bal > 0)
                _lastAccountBalance = (decimal)bal;

            _positionTracker.ProcessFill(trade);
        }
        catch (Exception ex)
        {
            LogError(ex, "OnTradeAdded Fehler");
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] OnTradeAdded Fehler: {ex.Message}");
        }
    }

    // Manche Futures-Charts laufen auf einem Continuous-/Frontmonth-Symbol, dessen Id
    // sich von der Id des tatsächlich gehandelten Kontrakts unterscheidet — deshalb
    // zusätzlich über Root vergleichen (z.B. "ES" statt "ES09/26@CME"), falls die reine
    // Id nicht matcht.
    private bool SymbolMatches(Symbol? tradeSymbol)
    {
        if (Symbol == null || tradeSymbol == null) return false;
        if (tradeSymbol.Id == Symbol.Id) return true;
        if (!string.IsNullOrEmpty(tradeSymbol.Root) && !string.IsNullOrEmpty(Symbol.Root) &&
            string.Equals(tradeSymbol.Root, Symbol.Root, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // ── Trade abgeschlossen → Karte + Telegram + CSV + Server ────────────

    private void OnPositionClosed(PositionRecord record)
    {
        // Historische Trades beim Neuladen/Neustart ignorieren
        if (DateTime.SpecifyKind(record.CloseTime, DateTimeKind.Utc) < _initTime) return;

        // Replay-/Dubletten-Schutz: denselben Trade nicht zweimal verschicken
        string tradeKey = $"{record.Symbol}|{record.Direction}|{record.OpenTime:O}|{record.CloseTime:O}|{record.AvgEntryPrice}|{record.AvgExitPrice}|{record.Contracts}";
        if (!_sentTradeKeys.Add(tradeKey)) return;

        // Tick-Daten: primär aus dem Trade-Fill (Symbol), Fallback statische Tabelle
        decimal tickSize = record.TickSize > 0 ? record.TickSize : GetTickSizeFallback(record.Symbol);
        decimal tickCost = record.TickCost > 0 ? record.TickCost : GetTickCostFallback(record.Symbol);
        if (record.TickSize == 0) record.TickSize = tickSize;
        record.PnlUsd = tickSize > 0 && tickCost > 0
            ? record.PnlPoints / tickSize * tickCost * record.Contracts
            : record.PnlPoints * record.Contracts;

        // DailyStats NACH PnlUsd-Berechnung updaten
        _dailyStats.AddTrade(record);

        TradeTag = "";

        byte[]? chartBytes = BuildMiniChart(record);

        // Snapshots für Background-Thread (immutable)
        var recordSnapshot = record;
        decimal ddLimit    = (decimal)DailyDrawdownLimit;
        decimal balance    = _lastAccountBalance > 0 ? _lastAccountBalance : (decimal)AccountBalanceFallback;
        byte[]? logoSnap   = _logoBytes;
        string botToken    = BotToken;
        string chatId      = ChatId;
        string traderName  = _sessionTraderName ?? TraderName;
        string serverUrl   = ServerUrl;
        string serverToken = ServerToken;
        // Kein Account-Realized-PnL-Feed verfügbar (anders als ATAS Portfolio-API) —
        // Tages-P&L basiert vollständig auf der eigenen laufenden Summe.
        var statsSnapshot = _dailyStats.Snapshot(0m);

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] cardBytes = CardRenderer.RenderCard(
                    recordSnapshot, statsSnapshot, logoSnap, chartBytes, ddLimit, balance, traderName);

                string caption = TelegramSender.BuildCaption(recordSnapshot, statsSnapshot, traderName);

                string? tgError = await TelegramSender.SendPhotoAsync(botToken, chatId, cardBytes, caption, _httpClient)
                    .ConfigureAwait(false);
                if (tgError != null)
                    Log($"Telegram-Versand fehlgeschlagen: {tgError}", LoggingLevel.Error);

                _csvWriter.AppendTrade(recordSnapshot, statsSnapshot);

                string? serverError = await TradeRecapServerSender.SendAsync(
                    serverUrl, serverToken, recordSnapshot, statsSnapshot, traderName, _httpClient)
                    .ConfigureAwait(false);
                if (serverError != null)
                    Log($"Server-Journal fehlgeschlagen: {serverError}", LoggingLevel.Error);
            }
            catch (Exception ex)
            {
                LogError(ex, "Fehler beim Senden der Recap-Karte");
                System.Diagnostics.Debug.WriteLine($"[TradeRecap] Fehler: {ex.Message}");
            }
        });
    }

    // ── Status-Overlay ────────────────────────────────────────────────────

    private async Task CheckVersionAsync()
    {
        _updateVersion = await VersionChecker.CheckAsync(_httpClient, CurrentVersion).ConfigureAwait(false);
    }

    private async Task CheckTelegramAsync()
    {
        if (string.IsNullOrWhiteSpace(BotToken)) { _tgStatus = 0; return; }
        try
        {
            var r = await _httpClient
                .GetAsync($"https://api.telegram.org/bot{BotToken}/getMe")
                .ConfigureAwait(false);
            _tgStatus = r.IsSuccessStatusCode ? 1 : 2;
        }
        catch { _tgStatus = 2; }
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);
        try { DrawStatusPanel(args.Graphics, args.Rectangle); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] OnPaintChart Fehler: {ex.Message}");
        }
    }

    private readonly record struct StatusRow(Color Dot, string Text, Color TextColor);

    private void DrawStatusPanel(Graphics g, Rectangle clip)
    {
        if (!_logoImageLoadAttempted)
        {
            _logoImageLoadAttempted = true;
            _logoImage = TryLoadLogoImage(_logoBytes);
        }

        if (clip.Width < 100) return;

        const int PadX     = 10;
        const int RowH     = 20;
        const int HeaderH  = 36;
        const int AccentH  = 3;
        const int AvatarSz = 24;
        const int Radius   = 10;

        // ── Zeilen zusammenstellen ───────────────────────────────────────
        var rows = new List<StatusRow>();

        if (_initFailed)
            rows.Add(new StatusRow(_colorRed, $"INIT FEHLER: {_initErrorMessage}", _colorRed));

        if (!_isMarketOwner)
        {
            rows.Add(new StatusRow(_colorMuted, "Passiv – aktiv in anderem Chart", _colorTextMuted));
        }
        else
        {
            (Color dot, string text) tg = _tgStatus switch
            {
                1 => (_colorGreen,  "Telegram verbunden"),
                2 => (_colorRed,    "Telegram: Token/ID prüfen"),
                _ => (_colorYellow, "Telegram: Verbindung wird geprüft"),
            };
            rows.Add(new StatusRow(tg.dot, tg.text, _colorTextPrime));

            var active = _positionTracker?.ActiveRecord;
            if (active != null)
            {
                string dir = active.Direction == PositionDirection.Long ? "LONG" : "SHORT";
                rows.Add(new StatusRow(_colorGold,
                    $"{dir}  {active.Contracts}K  @ {active.AvgEntryPrice:F2}", _colorTextPrime));
            }
            else
            {
                rows.Add(new StatusRow(_colorMuted, "Kein Trade offen", _colorTextMuted));
            }
        }

        if (_activeAmpel is AmpelColor.Yellow or AmpelColor.Red)
        {
            bool isRed = _activeAmpel == AmpelColor.Red;
            rows.Add(new StatusRow(
                isRed ? _colorRed : _colorYellow,
                isRed ? "Kein Trading heute (Zustandscheck)" : "Risiko halbieren (Zustandscheck)",
                isRed ? _colorRed : _colorYellow));
        }

        if (_updateVersion != null)
            rows.Add(new StatusRow(_colorYellow, $"Update v{_updateVersion} verfügbar", _colorYellow));

        // ── Geometrie ────────────────────────────────────────────────────
        int maxTextLen = rows.Count == 0 ? 0 : rows.Max(r => r.Text.Length);
        int panelW  = Math.Clamp(maxTextLen * 7 + 60, 230, 460);
        int bodyH   = _panelCollapsed ? 0 : rows.Count * RowH + 8;
        int footerH = _panelCollapsed ? 0 : 16;
        int panelH  = HeaderH + bodyH + footerH;

        int defaultX = clip.Right - panelW - 12;
        int defaultY = clip.Top   + 12;
        int panX = _panelX == int.MinValue ? defaultX : _panelX;
        int panY = _panelY == int.MinValue ? defaultY : _panelY;
        panX = Math.Max(clip.Left, Math.Min(panX, clip.Right  - 60));
        panY = Math.Max(clip.Top,  Math.Min(panY, clip.Bottom - HeaderH));

        _lastPanelRect  = new Rectangle(panX, panY, panelW, panelH);
        _lastHeaderRect = new Rectangle(panX, panY, panelW, HeaderH);

        // ── Karte ────────────────────────────────────────────────────────
        using (var borderBrush = new SolidBrush(_colorBorder))
        using (var borderPath  = RoundedRectPath(new Rectangle(panX - 1, panY - 1, panelW + 2, panelH + 2), Radius + 1))
            g.FillPath(borderBrush, borderPath);
        using (var cardBrush = new SolidBrush(_colorCardBg))
        using (var cardPath  = RoundedRectPath(_lastPanelRect, Radius))
            g.FillPath(cardBrush, cardPath);
        using (var accentBrush = new SolidBrush(_colorGold))
            g.FillRectangle(accentBrush, new Rectangle(panX, panY, panelW, AccentH));

        // Avatar-Badge (Logo, sonst "MT"-Monogramm)
        var avatarRect = new Rectangle(panX + PadX, panY + AccentH + (HeaderH - AccentH - AvatarSz) / 2, AvatarSz, AvatarSz);
        using (var avatarBrush = new SolidBrush(_colorAvatarBg))
        using (var avatarPath  = RoundedRectPath(avatarRect, AvatarSz / 2))
            g.FillPath(avatarBrush, avatarPath);
        if (_logoImage != null)
        {
            const int inset = 4;
            g.DrawImage(_logoImage, new Rectangle(avatarRect.X + inset, avatarRect.Y + inset, AvatarSz - 2 * inset, AvatarSz - 2 * inset));
        }
        else
        {
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var b   = new SolidBrush(_colorGold);
            g.DrawString("MT", _avatarFont, b, avatarRect, fmt);
        }

        // Titel + Untertitel (Symbol + Aktiv-/Passiv-Status)
        int textX = avatarRect.Right + 8;
        using (var titleBrush = new SolidBrush(_colorTextPrime))
            g.DrawString("Munich Traders", _titleFont, titleBrush, textX, panY + AccentH + 2);
        string subtitle = string.IsNullOrEmpty(_symbol)
            ? "Trade Recap"
            : $"{Symbol?.Name ?? _symbol} · {(_isMarketOwner ? "aktiv" : "passiv")}";
        using (var subBrush = new SolidBrush(_colorTextMuted))
            g.DrawString(subtitle, _smallFont, subBrush, textX, panY + AccentH + 19);

        // Einklapp-Pfeil oben rechts
        int chevCx = panX + panelW - PadX - 6;
        int chevCy = panY + HeaderH / 2;
        _lastChevronRect = new Rectangle(chevCx - 10, chevCy - 10, 20, 20);
        Point[] tri = _panelCollapsed
            ? new[] { new Point(chevCx - 3, chevCy - 5), new Point(chevCx - 3, chevCy + 5), new Point(chevCx + 4, chevCy) }
            : new[] { new Point(chevCx - 5, chevCy - 3), new Point(chevCx + 5, chevCy - 3), new Point(chevCx, chevCy + 4) };
        using (var chevBrush = new SolidBrush(_colorTextMuted))
            g.FillPolygon(chevBrush, tri);

        if (_panelCollapsed) return;

        using (var linePen = new Pen(_colorBorder))
            g.DrawLine(linePen, panX + PadX, panY + HeaderH, panX + panelW - PadX, panY + HeaderH);

        // ── Statuszeilen ─────────────────────────────────────────────────
        int y = panY + HeaderH + 6;
        foreach (var row in rows)
        {
            var dotRect = new Rectangle(panX + PadX, y + 6, 7, 7);
            using (var dotBrush = new SolidBrush(row.Dot)) g.FillEllipse(dotBrush, dotRect);
            using (var textBrush = new SolidBrush(row.TextColor))
                g.DrawString(row.Text, _statusFont, textBrush, panX + PadX + 14, y);
            y += RowH;
        }

        // ── Footer ───────────────────────────────────────────────────────
        using (var linePen = new Pen(_colorBorder))
            g.DrawLine(linePen, panX + PadX, y, panX + panelW - PadX, y);
        using (var footerBrush = new SolidBrush(_colorTextMuted))
            g.DrawString($"Munich Traders  ·  v{CurrentVersion}", _smallFont, footerBrush, panX + PadX, y + 2);
    }

    private static GraphicsPath RoundedRectPath(Rectangle rect, int radius)
    {
        int d = Math.Max(1, radius) * 2;
        d = Math.Min(d, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Image? TryLoadLogoImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return Image.FromStream(new MemoryStream(bytes)); }
        catch { return null; }
    }

    // ── Panel-Interaktion (Drag & Einklappen) ────────────────────────────

    private void OnChartMouseDown(object? sender, ChartMouseNativeEventArgs e)
    {
        if (_lastChevronRect.Contains(e.Location))
        {
            _panelCollapsed = !_panelCollapsed;
            e.Handled = true;
            CurrentChart?.RedrawBuffer();
            return;
        }
        if (_lastHeaderRect.Contains(e.Location))
        {
            _isDraggingPanel = true;
            _dragMouseStart  = e.Location;
            _dragPanelStart  = new Point(_lastPanelRect.X, _lastPanelRect.Y);
            e.Handled = true;
            e.NeedMouseCapture = true;
        }
    }

    private void OnChartMouseMove(object? sender, ChartMouseNativeEventArgs e)
    {
        if (!_isDraggingPanel) return;
        _panelX = _dragPanelStart.X + (e.Location.X - _dragMouseStart.X);
        _panelY = _dragPanelStart.Y + (e.Location.Y - _dragMouseStart.Y);
        e.Handled   = true;
        e.NeedRedraw = true;
    }

    private void OnChartMouseUp(object? sender, ChartMouseNativeEventArgs e)
    {
        if (!_isDraggingPanel) return;
        _isDraggingPanel = false;
        e.Handled = true;
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────────────

    private byte[]? BuildMiniChart(PositionRecord record)
    {
        const int MinCandleCount   = 100;  // Mindestanzahl sichtbarer Kerzen
        const int MinCandlesBefore = 30;   // Mindestvorlauf vor der Entry-Kerze

        int total = Count;
        if (total < 3) return null;

        // Quantower zählt Offsets vom aktuellsten Balken aus (0 = jetzt, steigend = weiter zurück).
        // Entry-Balken suchen: erster Balken (von jetzt aus rückwärts), dessen Zeit <= Entry-Zeit ist.
        int entryOffset = -1;
        int maxSearch = Math.Min(total - 1, 500);
        for (int off = 0; off <= maxSearch; off++)
        {
            try
            {
                if (Time(off) <= record.OpenTime) { entryOffset = off; break; }
            }
            catch { break; }
        }

        // Basis-Fenster: die letzten 100 Kerzen (Offsets 0..99)
        int windowMaxOffset = Math.Min(total - 1, MinCandleCount - 1);

        // Fenster nach hinten erweitern wenn Entry-Vorlauf < 30 Kerzen
        if (entryOffset >= 0 && (windowMaxOffset - entryOffset) < MinCandlesBefore)
            windowMaxOffset = Math.Min(total - 1, entryOffset + MinCandlesBefore);

        var candles = new List<CandleData>(windowMaxOffset + 1);
        for (int off = windowMaxOffset; off >= 0; off--)   // chronologisch: älteste zuerst
        {
            try
            {
                candles.Add(new CandleData(
                    (decimal)Open(off), (decimal)High(off), (decimal)Low(off), (decimal)Close(off),
                    (decimal)Volume(off), Time(off)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradeRecap] Bar-Zugriff (offset {off}) Fehler: {ex.Message}");
                break;
            }
        }

        if (candles.Count < 3) return null;

        try   { return MiniChartRenderer.Render(candles, record); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] MiniChart Fehler: {ex.Message}");
            return null;
        }
    }

    private static byte[]? TryLoadLogo(string path)
    {
        // 1. Nutzerpfad (überschreibt Standard-Logo)
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { return File.ReadAllBytes(path); }
            catch { }
        }
        // 2. Eingebettetes Standard-Logo als Fallback
        return LoadEmbeddedLogo();
    }

    private static byte[]? LoadEmbeddedLogo()
    {
        try
        {
            var asm  = typeof(TradeRecapIndicator).Assembly;
            string resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("munich-traders-logo.png", StringComparison.OrdinalIgnoreCase))
                ?? "";
            if (string.IsNullOrEmpty(resourceName)) return null;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static decimal GetTickCostFallback(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "ES"  or "ESZ" or "ESM" or "ESH" or "ESU" => 12.50m,
            "NQ"  or "NQZ" or "NQM" or "NQH" or "NQU" => 5.00m,
            "CL"  or "CLZ" or "CLM" or "CLH" or "CLU" => 10.00m,
            "GC"  or "GCZ" or "GCM" or "GCH" or "GCU" => 10.00m,
            "MES"                                       => 1.25m,
            "MNQ"                                       => 0.50m,
            "MCL"                                       => 1.00m,
            "MGC"                                       => 1.00m,
            "RTY"                                       => 5.00m,
            "YM"                                        => 5.00m,
            _                                           => 1.00m,
        };

    private static decimal GetTickSizeFallback(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "ES"  or "ESZ" or "ESM" or "ESH" or "ESU" => 0.25m,
            "NQ"  or "NQZ" or "NQM" or "NQH" or "NQU" => 0.25m,
            "CL"  or "CLZ" or "CLM" or "CLH" or "CLU" => 0.01m,
            "GC"  or "GCZ" or "GCM" or "GCH" or "GCU" => 0.10m,
            "MES"                                       => 0.25m,
            "MNQ"                                       => 0.25m,
            "MCL"                                       => 0.01m,
            "MGC"                                       => 0.10m,
            "RTY"                                       => 0.10m,
            "YM"                                        => 1.00m,
            _                                           => 1.00m,
        };

    // ── Cleanup ───────────────────────────────────────────────────────────

    protected override void OnClear()
    {
        // Zeigt im Quantower-Log, WANN und OB der Indikator sauber entladen wurde —
        // fehlt dieser Eintrag vor einem Verschwinden, war es ein harter Crash statt
        // einer regulären Entfernung (Chart geschlossen, Indikator gelöscht, Neuladen).
        Log("OnClear aufgerufen — Indikator wird entladen");

        try
        {
            Core.Instance.TradeAdded -= OnTradeAdded;
            if (_positionTracker != null)
                _positionTracker.PositionClosed -= OnPositionClosed;

            if (CurrentChart != null)
            {
                CurrentChart.MouseDown -= OnChartMouseDown;
                CurrentChart.MouseMove -= OnChartMouseMove;
                CurrentChart.MouseUp   -= OnChartMouseUp;
            }

            if (_isMarketOwner)
                MarketOwnerGate.Release(_symbol, _instanceId);
            if (_ownsCheckinFlow)
                CheckinGate.ReleaseFlow();

            _connectionTimer?.Stop();
            _connectionTimer?.Dispose();
            _connectionTimer = null;

            _checkinPollTimer?.Stop();
            _checkinPollTimer?.Dispose();
            _checkinPollTimer = null;

            _httpClient?.Dispose();
            _logoImage?.Dispose();
        }
        catch (Exception ex)
        {
            LogError(ex, "OnClear Fehler beim Aufräumen");
        }

        base.OnClear();
    }

    // ── Quantower-natives Logging (sichtbar im Quantower-Log-Panel) ───────

    private static void Log(string message, LoggingLevel level = LoggingLevel.System)
    {
        try { Core.Instance?.Loggers?.Log($"[TradeRecap] {message}", level); }
        catch { /* Logging darf den Indikator nie zum Absturz bringen */ }
    }

    private static void LogError(Exception ex, string message)
    {
        try { Core.Instance?.Loggers?.Log(ex, $"[TradeRecap] {message}", LoggingLevel.Error); }
        catch { /* Logging darf den Indikator nie zum Absturz bringen */ }
    }
}
