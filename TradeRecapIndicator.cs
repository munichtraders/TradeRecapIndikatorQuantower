using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using TradingPlatform.BusinessLayer;

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

    [InputParameter("Design: Trader-Name (z.B. @MunichTraders)", 51)]
    public string TraderName = "";

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

    // Letzter bekannter Kontostand (aus dem Trade-Fill gelesen) — Fallback-Feld überschreibt nur wenn 0
    private decimal _lastAccountBalance;

    // Zeitstempel des Indikator-Starts — historische Trades davor werden nicht verschickt
    private DateTime _initTime;

    // Fingerprints bereits verschickter Trades — Schutz gegen doppelte Zustellung
    private readonly HashSet<string> _sentTradeKeys = new();

    // Drittes Release am 2026-07-22 — VersionChecker vergleicht als int (r > c),
    // deshalb Ziffernanhang statt Buchstabensuffix, um YYMMDD-Schema kompatibel zu halten.
    private const string CurrentVersion = "2607222";

    // 0 = unbekannt, 1 = verbunden, 2 = Fehler
    private volatile int _tgStatus;

    // null = aktuell, sonst neue Versionsnummer verfügbar
    private string? _updateVersion;

    // Diagnose: wenn OnInit fehlschlägt, bleibt der Indikator sichtbar (statt vom
    // Chart zu verschwinden) und zeigt den Fehler im Status-Panel statt geräuschlos zu sterben.
    private bool _initFailed;
    private string? _initErrorMessage;
    private bool _updateErrorLogged;

    private static readonly Color _colorGold   = Color.FromArgb(255, 184, 150, 72);
    private static readonly Color _colorGreen  = Color.FromArgb(255, 34,  197, 94);
    private static readonly Color _colorRed    = Color.FromArgb(255, 239, 68,  68);
    private static readonly Color _colorYellow = Color.FromArgb(255, 245, 158, 11);
    private static readonly Color _colorMuted  = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color _colorBg     = Color.FromArgb(210, 15,  15,  15);
    private static readonly Font  _statusFont  = new("Calibri", 10f, GraphicsUnit.Pixel);

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

            // Sofort und dann alle 60s Telegram-Verbindung prüfen
            _ = CheckTelegramAsync();
            _connectionTimer = new System.Timers.Timer(60_000) { AutoReset = true };
            _connectionTimer.Elapsed += (_, _) => _ = CheckTelegramAsync();
            _connectionTimer.Start();

            _ = CheckVersionAsync();

            Log($"OnInit OK — Version {CurrentVersion}, Symbol {Symbol?.Name ?? "?"}");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initErrorMessage = ex.Message;
            LogError(ex, "OnInit fehlgeschlagen");
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
        string traderName  = TraderName;
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

    private void DrawStatusPanel(Graphics g, Rectangle clip)
    {
        const int LineH = 20;
        const int PadX  = 8;
        const int PadY  = 6;
        bool hasUpdate  = _updateVersion != null;
        int  PanelW     = 240;
        int  lines      = (hasUpdate ? 3 : 2) + (_initFailed ? 1 : 0);
        int  PanelH     = LineH * lines + PadY * 2;

        if (clip.Width < 100) return;

        int panX = clip.Right - PanelW - 12;
        int panY = clip.Top   + 12;

        var accentRect = new Rectangle(panX, panY, PanelW, 2);
        var bgRect     = new Rectangle(panX, panY + 2, PanelW, PanelH - 2);
        using (var goldBrush = new SolidBrush(_colorGold)) g.FillRectangle(goldBrush, accentRect);
        using (var bgBrush   = new SolidBrush(_colorBg))   g.FillRectangle(bgBrush, bgRect);

        if (_initFailed)
        {
            using var b = new SolidBrush(_colorRed);
            g.DrawString($"INIT FEHLER: {_initErrorMessage}", _statusFont, b, panX + PadX, panY + PadY);
            panY += LineH;
        }

        // Zeile 1 — Telegram-Status
        string tgText;
        Color  tgColor;
        switch (_tgStatus)
        {
            case 1:  tgText = "TG  OK  Verbunden";         tgColor = _colorGreen;  break;
            case 2:  tgText = "TG  ERR  Token/ID prüfen";  tgColor = _colorRed;    break;
            default: tgText = "TG  ...  Prüfe Verbindung"; tgColor = _colorYellow; break;
        }
        using (var b = new SolidBrush(tgColor)) g.DrawString(tgText, _statusFont, b, panX + PadX, panY + PadY);

        // Zeile 2 — Trade-Status
        var active = _positionTracker?.ActiveRecord;
        string tradeText;
        Color  tradeColor;
        if (active != null)
        {
            string dir = active.Direction == PositionDirection.Long ? "LONG" : "SHORT";
            tradeText  = $"{dir}  {active.Contracts}K  @  {active.AvgEntryPrice:F2}";
            tradeColor = _colorGold;
        }
        else
        {
            tradeText  = "Kein Trade offen";
            tradeColor = _colorMuted;
        }
        using (var b = new SolidBrush(tradeColor))
            g.DrawString(tradeText, _statusFont, b, panX + PadX, panY + PadY + LineH);

        if (hasUpdate)
        {
            string updateLine = $"Update v{_updateVersion} verfügbar";
            using var b = new SolidBrush(_colorYellow);
            g.DrawString(updateLine, _statusFont, b, panX + PadX, panY + PadY + LineH * 2);
        }
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

            _connectionTimer?.Stop();
            _connectionTimer?.Dispose();
            _connectionTimer = null;

            _httpClient?.Dispose();
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
