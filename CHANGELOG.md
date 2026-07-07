# Changelog

Alle Änderungen werden hier dokumentiert. Format: `YYMMDD`.

---

## [260714] — 2026-07-14

### Neu
- **Erste Quantower-Version.** Port des ATAS-Indikators (`07_ATAS/Indikatoren/TradeRecap`) auf die Quantower-Plattform (`TradingPlatform.BusinessLayer`).
- Identische Recap-Karte (1080×1920, GDI+), identisches Server-Journal und Telegram-Backend wie die ATAS-Version — beide Plattformen schreiben in dieselbe zentrale `TradeRecapTrades.csv` auf dem Munich-Traders-Server.
- Trade-Erfassung über `Core.Instance.TradeAdded` (global, wird auf das Chart-Symbol gefiltert) statt ATAS' `OnNewMyTrade`.
- MAE/MFE-Tracking live über `OnUpdate` mit `UpdateReason.NewTick` (`Symbol.Last`/`Symbol.Bid`).
- Mini-Candlestick-Chart aus `Indicator.Open/High/Low/Close/Volume/Time(offset)` statt ATAS' `GetCandle(bar)`.
- Build-Output geht direkt in den Quantower-Indikatorordner (`Settings\Scripts\Indicators\TradeRecap`) — kein manuelles Kopieren nötig.

### Bekannte Unterschiede zur ATAS-Version
- **Kein 1-Klick-Auto-Update:** Der Indikator zeigt im Status-Panel an, wenn eine neue Version verfügbar ist, installiert sie aber (noch) nicht automatisch. Update = neue Release-DLL manuell in den Indikatorordner kopieren.
- **Tages-P&L rein selbst berechnet:** Quantower liefert keine dokumentierte Account-Realized-PnL-Property wie ATAS' `Portfolio.RealizedProfit`. Die Tages-Statistik basiert vollständig auf der laufenden Summe der vom Indikator selbst erfassten Trades (kein Fallback auf einen Broker-Kontostand).
- **Konto-Größe automatisch:** `Account.Balance` ist in Quantower eine dokumentierte, direkt lesbare Property (kein Reflection-Hack wie bei ATAS nötig).
- **Timezone bei Mini-Chart noch ungetestet:** Die Entry-/Exit-Markierung auf dem Mini-Chart vergleicht `Trade.DateTime` direkt mit `Indicator.Time(offset)`, ohne UTC/Lokalzeit-Konvertierung (anders als die ATAS-Version, die genau das brauchte). Falls die Marker im Live-Test verschoben erscheinen, ist das der erste Ansatzpunkt.
