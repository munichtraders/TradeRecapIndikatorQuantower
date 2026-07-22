# Changelog

Alle Änderungen werden hier dokumentiert. Format: `YYMMDD`.

---

## [2607222] — 2026-07-22 (Update 3)

### Fix
- **"Kein Trade offen" trotz laufendem Trade.** `OnTradeAdded` verglich bisher nur `trade.Symbol.Id == Symbol.Id` und verwarf jeden Fill ohne exakten Id-Treffer komplett stillschweigend (kein Log, egal ob Treffer oder nicht). Bei Futures kann der Chart auf einem Continuous-/Frontmonth-Symbol laufen, dessen `Id` sich von der `Id` des tatsächlich gehandelten Kontrakts unterscheidet — der reine Id-Vergleich hat solche Fills nie erfasst.
- Abgleich läuft jetzt zusätzlich über `Symbol.Root` (z.B. "ES" statt "ES09/26@CME") als Fallback, falls die reine Id nicht matcht.
- Jeder empfangene Fill wird jetzt geloggt (Trade-Symbol vs. Chart-Symbol, Id, Root, Match-Ergebnis, Account-Abgleich) — damit lässt sich ein zukünftiger Mismatch direkt im Quantower-Log nachvollziehen statt zu raten.

---

## [2607221] — 2026-07-22 (Update 2)

### Fix
- **Telegram-Versand schlug lautlos fehl.** `TelegramSender.SendPhotoAsync` brach bei leerem Bot Token/Chat ID komplett stillschweigend ab, ohne irgendeinen Log-Eintrag — auch die neue Quantower-Log-Anbindung aus [260722] hat das nicht erfasst, weil der Fehlerfall nie eine Exception war. `SendPhotoAsync` und `TradeRecapServerSender.SendAsync` geben jetzt eine Fehlermeldung statt `void`/stillem Abbruch zurück; der Indikator loggt Fehlschläge (leere Zugangsdaten, HTTP-Fehler, Exceptions) jetzt als `Error`-Eintrag im Quantower-Log.
- **Bot Token + Chat ID als Standardwerte hinterlegt** (analog zu `ServerUrl`/`ServerToken`) — Nutzer-Entscheidung 2026-07-22, bewusst inklusive der bekannten Abwägung, dass beide Werte damit in der öffentlichen Git-Historie dieses Repos landen.

---

## [260722] — 2026-07-22

### Fix
- **Diagnose für "Indikator verschwindet automatisch beim Neuladen".** `OnInit` und `OnUpdate` waren nicht gegen unbehandelte Exceptions abgesichert — ein Fehler dort führt dazu, dass Quantower den Indikator kommentarlos vom Chart entfernt, ohne dass irgendwo sichtbar wird warum. `OnUpdate` läuft bei JEDEM Tick und war der wahrscheinlichste Auslöser.
- Beide Methoden sind jetzt in try/catch gekapselt: bei einem Fehler bleibt der Indikator auf dem Chart sichtbar (statt zu verschwinden) und zeigt "INIT FEHLER: ..." im Status-Panel an.
- Neu: Logging über die native Quantower-API (`Core.Instance.Loggers`) statt einer eigenen Log-Datei — taucht direkt im Quantower-Log-Panel auf. Geloggt werden: erfolgreicher `OnInit`-Abschluss (mit Version + Symbol), `OnInit`-Fehler (mit Exception), `OnUpdate`-Fehler (nur der erste, um das Log nicht zu fluten) und jeder `OnClear`-Aufruf (zeigt, ob/wann der Indikator sauber entladen wurde — fehlt dieser Eintrag vor einem Verschwinden, war es ein harter Crash statt einer regulären Entfernung).

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
