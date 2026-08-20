using TradingPlatform.BusinessLayer;

namespace MunichTraders.TradeRecap;

// Eigenes Enum — vermeidet Namenskonflikt mit TradingPlatform.BusinessLayer.Side
public enum PositionDirection { Long, Short }

public record FillInfo(decimal Price, int Qty, DateTime Time);

public class PositionRecord
{
    public string Symbol { get; set; } = "";
    public PositionDirection Direction { get; set; }
    public List<FillInfo> OpenFills { get; } = new();
    public List<FillInfo> CloseFills { get; } = new();
    public decimal AvgEntryPrice { get; set; }
    public decimal AvgExitPrice { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime CloseTime { get; set; }
    public int Contracts { get; set; }
    public decimal PnlPoints { get; set; }
    public decimal PnlUsd { get; set; }
    public decimal MAE { get; set; }
    public decimal MFE { get; set; }
    // Kontraktgewichtetes Exposure (Punkte * tatsächlich offene Kontrakte zum jeweiligen Tick) —
    // TickCost-unabhängig, damit auch vor dem Fallback im Indikator korrekt getrackt werden kann.
    public decimal MAEExposure { get; set; }
    public decimal MFEExposure { get; set; }
    public string TradeTag { get; set; } = "";
    public string AccountId { get; set; } = "";
    // Tick-Daten aus Symbol beim ersten Fill — 0 bedeutet unbekannt (Fallback greift)
    public decimal TickSize { get; set; }
    public decimal TickCost { get; set; }
    public TimeSpan Duration => CloseTime - OpenTime;

    // Tick-basierte Werte (gerundet)
    public long PnlTicks  => TickSize > 0 ? (long)Math.Round(PnlPoints / TickSize) : (long)PnlPoints;
    public long MAETicks  => TickSize > 0 ? (long)Math.Round(MAE / TickSize) : (long)MAE;
    public long MFETicks  => TickSize > 0 ? (long)Math.Round(MFE / TickSize) : (long)MFE;

    // USD-Werte für MAE/MFE — basieren auf MAEExposure/MFEExposure, also der tatsächlich
    // offenen Kontraktzahl zum Zeitpunkt des jeweiligen Extrempunkts (nicht der finalen Gesamtgröße).
    public decimal MAEUsd => TickSize > 0 && TickCost > 0
        ? MAEExposure / TickSize * TickCost : 0m;
    public decimal MFEUsd => TickSize > 0 && TickCost > 0
        ? MFEExposure / TickSize * TickCost : 0m;


    public int OpenQty => OpenFills.Sum(f => f.Qty);
    public int CloseQty => CloseFills.Sum(f => f.Qty);

    public static decimal WeightedAvg(List<FillInfo> fills)
    {
        int totalQty = fills.Sum(f => f.Qty);
        if (totalQty == 0) return 0;
        return fills.Sum(f => f.Price * f.Qty) / totalQty;
    }
}

public class DailyStats
{
    public int TradesCount { get; private set; }
    public int Wins { get; private set; }
    public decimal TotalPnlToday { get; private set; }
    public DateTime LastResetDate { get; private set; } = DateTime.Today;

    public decimal WinRate => TradesCount == 0 ? 0 : (decimal)Wins / TradesCount * 100;

    public void ResetIfNewDay()
    {
        if (DateTime.Today != LastResetDate)
        {
            TradesCount = 0;
            Wins = 0;
            TotalPnlToday = 0;
            LastResetDate = DateTime.Today;
        }
    }

    public void AddTrade(PositionRecord record)
    {
        TradesCount++;
        TotalPnlToday += record.PnlUsd;
        if (record.PnlUsd > 0) Wins++;
    }

    public DailyStatsSnapshot Snapshot(decimal accountClosedPnl = 0m) =>
        new(TradesCount, TotalPnlToday, accountClosedPnl);
}

public record DailyStatsSnapshot(int TradesCount, decimal TotalPnlToday, decimal AccountClosedPnl)
{
    // Anzeigewert: Account-PnL wenn vorhanden, sonst berechneter Wert
    public decimal DisplayPnl => AccountClosedPnl != 0m ? AccountClosedPnl : TotalPnlToday;
};

public class PositionTracker
{
    private PositionRecord? _active;
    private readonly DailyStats _dailyStats;
    private string _pendingTag = "";

    // Trade-IDs, die bereits durch die Open/Close-Pairing-Logik gelaufen sind.
    // Core.TradeAdded ist ein globales Event (alle Symbole/Konten) — ohne diese
    // Sperre könnte ein doppelt zugestellter Fill die Pairing-Logik verwirren.
    private readonly HashSet<string> _processedFillIds = new();

    public event Action<PositionRecord>? PositionClosed;

    public bool IsPositionOpen  => _active != null;
    public PositionRecord? ActiveRecord => _active;

    public PositionTracker(DailyStats dailyStats)
    {
        _dailyStats = dailyStats;
    }

    public void SetPendingTag(string tag) => _pendingTag = tag;

    public void ProcessFill(Trade trade)
    {
        // Replay-/Dubletten-Schutz: denselben Fill nicht nochmal durch die Pairing-Logik laufen lassen.
        if (!string.IsNullOrEmpty(trade.Id) && !_processedFillIds.Add(trade.Id))
            return;

        bool isBuy = trade.Side == Side.Buy;
        var direction = isBuy ? PositionDirection.Long : PositionDirection.Short;
        var fill = new FillInfo((decimal)trade.Price, (int)trade.Quantity, trade.DateTime);

        // Quantower liefert das reine Root-Symbol (z.B. "ES" statt "ES09/26@CME") bevorzugt,
        // fällt auf Name/Id zurück falls Root leer ist (z.B. bei Krypto/CFD-Symbolen).
        string symbol = trade.Symbol?.Root ?? "";
        if (string.IsNullOrEmpty(symbol)) symbol = trade.Symbol?.Name ?? trade.Symbol?.Id ?? "";

        decimal tickSize = trade.Symbol != null ? (decimal)trade.Symbol.TickSize : 0m;
        decimal tickCost = 0m;
        try
        {
            if (trade.Symbol != null) tickCost = (decimal)trade.Symbol.GetTickCost((double)fill.Price);
        }
        catch { /* manche Connectoren liefern keinen Tick-Wert — Fallback-Tabelle im Indikator greift */ }

        if (_active == null)
        {
            _active = new PositionRecord
            {
                Symbol    = symbol,
                Direction = direction,
                OpenTime  = fill.Time,
                MAE       = 0,
                MFE       = 0,
                MAEExposure = 0,
                MFEExposure = 0,
                TradeTag  = _pendingTag,
                AccountId = trade.Account?.Id ?? "",
                TickSize  = tickSize,
                TickCost  = tickCost,
            };
            _active.OpenFills.Add(fill);
            _active.AvgEntryPrice = fill.Price;
            _active.Contracts = fill.Qty;
            return;
        }

        if (direction == _active.Direction)
        {
            // Scale-In
            _active.OpenFills.Add(fill);
            _active.AvgEntryPrice = PositionRecord.WeightedAvg(_active.OpenFills);
            _active.Contracts = _active.OpenQty;
            return;
        }

        // Close (partial oder full)
        _active.CloseFills.Add(fill);
        int openQty = _active.OpenQty;
        int closeQty = _active.CloseQty;

        if (closeQty >= openQty)
        {
            FinalizeRecord(_active);

            if (closeQty > openQty)
            {
                // Flip: neue Position in Gegenrichtung mit Überschuss
                int excess = closeQty - openQty;
                var flipped = new PositionRecord
                {
                    Symbol = _active.Symbol,
                    Direction = direction,
                    OpenTime = fill.Time,
                    MAE = 0,
                    MFE = 0,
                    MAEExposure = 0,
                    MFEExposure = 0,
                    TradeTag = _pendingTag
                };
                flipped.OpenFills.Add(fill with { Qty = excess });
                flipped.AvgEntryPrice = fill.Price;
                flipped.Contracts = excess;
                _active = flipped;
            }
            else
            {
                _active = null;
                _pendingTag = "";
            }
        }
    }

    private void FinalizeRecord(PositionRecord record)
    {
        record.CloseTime = record.CloseFills.Last().Time;
        record.AvgExitPrice = PositionRecord.WeightedAvg(record.CloseFills);
        record.Contracts = record.OpenQty;

        decimal pointDiff = record.Direction == PositionDirection.Long
            ? record.AvgExitPrice - record.AvgEntryPrice
            : record.AvgEntryPrice - record.AvgExitPrice;

        record.PnlPoints = pointDiff;

        // PnlUsd wird erst im OnPositionClosed-Handler gesetzt (TickCost-Fallback im Indikator).
        // AddTrade MUSS DANACH aufgerufen werden — deshalb hier nur das Event feuern.
        PositionClosed?.Invoke(record);
    }

    /// <summary>
    /// Wird bei jedem Markt-Tick aufgerufen (OnUpdate mit UpdateReason.NewTick).
    /// Tracked live wie weit der Kurs seit dem Entry gegen/in Richtung des Trades lief.
    /// Kein Kerzen-Bezug → funktioniert auch für 5-Sekunden-Trades korrekt.
    /// </summary>
    public void UpdateMAEMFEFromTick(decimal price)
    {
        if (_active == null) return;

        // Nur die zu diesem Zeitpunkt tatsächlich offene Größe zählt — nicht die finale
        // Gesamtgröße des Trades (die evtl. erst durch spätere Scale-Ins erreicht wird).
        int openQtyNow = _active.OpenQty - _active.CloseQty;
        if (openQtyNow <= 0) return;

        decimal move = _active.Direction == PositionDirection.Long
            ? price - _active.AvgEntryPrice
            : _active.AvgEntryPrice - price;

        decimal exposure = move * openQtyNow;   // kontraktgewichteter Punkte-Exposure zu diesem Tick

        if (exposure < _active.MAEExposure)   // negativer = weiter gegen dich
        {
            _active.MAEExposure = exposure;
            _active.MAE = move;
        }
        if (exposure > _active.MFEExposure)   // positiver = weiter in deine Richtung
        {
            _active.MFEExposure = exposure;
            _active.MFE = move;
        }
    }

    /// <summary>
    /// Sicherheitsnetz gegen verpasste Live-Ticks: prüft zusätzlich High/Low der
    /// aktuell laufenden Kerze gegen den Einstiegspreis. Die Kerzen-Engine der
    /// Plattform verpasst nie einen Preis, im Gegensatz zum Live-Tick-Stream
    /// (OnUpdate/NewTick), der bei schnellen Bewegungen einzelne Ticks auslassen kann.
    /// </summary>
    public void UpdateMAEMFEFromBar(decimal high, decimal low, DateTime barTime)
    {
        if (_active == null) return;
        if (barTime < _active.OpenTime) return;   // Kerze liegt (teilweise) vor dem Entry

        UpdateMAEMFEFromTick(low);
        UpdateMAEMFEFromTick(high);
    }
}
