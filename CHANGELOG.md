# Changelog

Alle Änderungen werden hier dokumentiert. Format ab `[20260731]`: `YYYYMMDD` (siehe Hinweis dort zum Formatwechsel). Ältere Einträge: `YYMMDD`, teils mit angehängter Revisionsziffer für mehrere Releases am selben Tag.

---

## [20260820] — 2026-08-20

### Fix — Min/Max Ticks verpassten schnelle Kursbewegungen
- **Problem:** `MAE`/`MFE` ("Min/Max Ticks" auf der Karte) wurden ausschließlich aus dem Live-Tick-Stream berechnet (`OnUpdate`/`UpdateReason.NewTick`). Bei schnellen Bewegungen kann dieser Stream einzelne Ticks auslassen — der MiniChart (aus den regulären Kerzendaten) zeigte dann einen deutlich größeren Ausschlag, als die Karte als Min/Max Ticks auswies (in einem beobachteten Fall auf der ATAS-Version: ~14 Ticks angezeigt bei tatsächlich ~220 Ticks laut Kerzen-Docht; Fix 1:1 auf Quantower übertragen).
- **Fix:** Neue Methode `PositionTracker.UpdateMAEMFEFromBar(high, low, barTime)` prüft zusätzlich bei jedem Tick `High(0)`/`Low(0)` der aktuell laufenden Kerze gegen den Einstiegspreis — die Kerzen-Engine der Plattform verpasst nie einen Preis, im Gegensatz zum Tick-Stream. Aufgerufen aus dem bestehenden `OnUpdate`/`NewTick`-Zweig direkt neben dem bisherigen Tick-Update.
- Betrifft `PositionTracker.cs`, `TradeRecapIndicator.cs`.

---

## [20260805] — 2026-08-05

### Fix — MAE/MFE-USD berücksichtigt jetzt tatsächlich offene Größe statt finaler Gesamtgröße
- **Problem:** `MAEUsd`/`MFEUsd` ("Min/Max Ticks · $" auf der Karte) multiplizierten die maximale Preis-Exkursion (in Ticks) immer mit der **finalen Gesamt-Kontraktzahl** des Trades (`record.Contracts`, gesetzt bei Close = Summe aller Open-Fills). War der Preis-Extrempunkt zu einem Zeitpunkt erreicht, an dem noch nicht alle Kontrakte aufgebaut waren (z. B. Scale-In später) oder bereits ein Teil geschlossen war (Scale-Out vorher), zeigte die Karte einen zu hohen theoretischen Betrag — so als wäre die volle Positionsgröße die ganze Zeit aktiv gewesen.
- **Fix:** `PositionTracker.UpdateMAEMFEFromTick` trackt jetzt bei jedem Tick das kontraktgewichtete Exposure (`Preis-Bewegung × tatsächlich offene Kontrakte zu diesem Zeitpunkt`, neue Felder `MAEExposure`/`MFEExposure`) statt nur die reine Preis-Distanz. `MAEUsd`/`MFEUsd` leiten sich jetzt aus diesem Exposure ab — `MAE`/`MFE` (Punkte) und damit `MAETicks`/`MFETicks` beziehen sich jetzt auf denselben Zeitpunkt wie der USD-Wert (den tatsächlichen Tiefst-/Höchststand des offenen Positionswerts), nicht mehr auf die global größte Preisbewegung unabhängig von der Positionsgröße.
- Betrifft `PositionTracker.cs`; `CardRenderer.cs`, `TelegramSender.cs`, `TradeRecapServerSender.cs`, `CsvJournalWriter.cs` unverändert (nutzen weiterhin dieselben Property-Namen).

---

## [20260731] — 2026-07-31

### Neu — Port der ATAS-Fixes vom selben Tag
- **Scale-In/Scale-Out sichtbar im MiniChart:** Jeder Nachkauf (Scale-In) und jeder Teilverkauf (Scale-Out) innerhalb eines Trades bekommt jetzt einen eigenen Pfeil im generierten Mini-Chart — kleiner (26px statt 44px) und transparenter (~47% Deckkraft) als die Haupt-Entry-/Exit-Pfeile, positioniert am tatsächlichen Fill-Preis/-Zeitpunkt. Mehrere Fills im selben Balken werden per kleinem Rechts-Versatz auseinandergezogen.
- **Fill-Aufschlüsselung in der Telegram-Caption:** Bei Trades mit mehr als einem Open- oder Close-Fill zeigt die Caption zusätzlich eine `Fills:`-Zeile.
- **Entry/Exit zeigen jetzt den tatsächlichen Fill-Preis statt Ø:** `Entry:`/`Exit:` (Caption), die `ENTRY`/`EXIT`-Linien+Haupt-Pfeile (MiniChart) und die `EINSTIEG`/`AUSSTIEG`-Felder (Karte) zeigten bisher den mengengewichteten Durchschnittspreis statt des Preises, wo der Trade tatsächlich begann/endete. Jetzt zeigen alle drei Stellen den echten ersten Open-Fill- bzw. letzten Close-Fill-Preis; die Caption ergänzt bei mehreren Fills zusätzlich den Ø-Preis in Klammern.
- `MiniChartRenderer.cs` und `CardRenderer.cs` sind nach diesem Fix wieder 1:1-identisch mit der ATAS-Version; `TelegramSender.cs` unterscheidet sich weiterhin bewusst nur in der `Task<string?>`-Fehlerrückgabe von `SendPhotoAsync` (Quantower-Logging-Anbindung aus `[2607221]`).
- Zugrunde liegende Trade-Erkennung (`PositionTracker`) brauchte keine Änderung — berechnete Scale-In/Scale-Out bereits vorher korrekt (mengengewichteter Ø-Entry/Ø-Exit, korrekter Gesamt-PnL), siehe ATAS-Repo-Changelog `[260731]`/`[260802]`/`[260803]` für die volle Fehleranalyse.

### Wichtig — Versionsschema-Wechsel auf `YYYYMMDD`
Frühere Mehrfach-Releases am selben Tag hängten eine Revisionsziffer an (`260722` → `2607221` → `2607222`). Das bricht den Auto-Updater dauerhaft: `VersionChecker` vergleicht Versionen als reine Ganzzahl (`int.TryParse`, größer = neuer), und ein 7-stelliger Wert wie `2607222` ist als Zahl **größer** als jeder künftige korrekt formatierte 6-stellige `YYMMDD`-Wert (z. B. `260801` oder `261231`) — das hätte den Update-Check auf unbestimmte Zeit stillgelegt. Fix: ab sofort 8-stelliges `YYYYMMDD` (`20260731`), garantiert dauerhaft monoton steigend und zugleich größer als der zuletzt ausgelieferte Wert `2607222`.

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
