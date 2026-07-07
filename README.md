# Trade Recap — Quantower Indikator

**Von Munich Traders** · Automatische Trade-Dokumentation direkt aus Quantower

Quantower-Port des [ATAS-Indikators](../../../07_ATAS/Indikatoren/TradeRecap/README.md) — gleiche Recap-Karte, gleiches Telegram-/Server-Backend, andere Plattform-API.

---

## Was macht der Indikator?

Nach jedem geschlossenen Trade rendert der Indikator automatisch eine gebrandete Recap-Karte und sendet sie per Telegram an deinen Kanal.

**Was auf der Karte steht:**
- Symbol, Richtung (Long/Short), Contracts, Einstiegs- und Ausstiegspreis
- PnL in Punkten und USD
- MAE / MFE (maximaler unrealisierter Verlust / Gewinn während des Trades)
- Trade-Dauer
- Optionaler Trade-Tag (z. B. "FOMC Scalp", "VWAP Reclaim")
- Tages-Stats: Anzahl Trades, Tages-PnL, Drawdown-Status
- Mini-Candlestick-Chart mit Entry- und Exit-Markierung
- Dein Logo (optional)

---

## Installation

### Voraussetzung: .NET SDK
Zum Bauen wird das .NET SDK benötigt (getestet mit .NET 8/10).

### Bauen
```
dotnet build TradeRecap.csproj -c Release
```
Das Projekt ist so konfiguriert, dass der Build **direkt** in den Quantower-Indikatorordner schreibt:
```
<Quantower-Installationsordner>\Settings\Scripts\Indicators\TradeRecap\
```
Kein manuelles Kopieren nötig — nach dem Build reicht **Quantower neu starten** (oder den Indikator im Chart neu laden).

### Pfad-Konfiguration (einmalig)
1. `Local.props.example` → `Local.props` kopieren (im übergeordneten `Indikatoren`-Ordner)
2. `QuantowerPath` auf deine Installation anpassen (Ordner mit `bin\TradingPlatform.BusinessLayer.dll`, typisch `C:\Quantower\TradingPlatform\v<Version>`)

### Indikator hinzufügen
In Quantower: Chart → Indikatoren → **Munich Traders** → **Trade Recap (Telegram)** auf den Chart ziehen.

---

## Einrichtung

### Telegram-Bot erstellen
1. In Telegram [@BotFather](https://t.me/BotFather) öffnen → `/newbot`
2. Bot-Token kopieren
3. Den Bot in deinen Kanal/deine Gruppe einladen und dort eine Nachricht schreiben
4. Chat-ID ermitteln: `https://api.telegram.org/bot<TOKEN>/getUpdates`

### Indikator-Einstellungen

| Feld | Beschreibung |
|---|---|
| Telegram: Bot Token | Token vom BotFather |
| Telegram: Chat ID | ID deines Kanals oder privaten Chats |
| Journal: CSV-Pfad | Optionaler Pfad für lokales Trade-Journal (z. B. `C:\Trading\journal.csv`) |
| Server-Journal: Server-URL | Adresse des Munich-Traders-Ingestion-Servers — sammelt alle Trades zentral (ATAS + Quantower gemeinsam) |
| Server-Journal: Server-Token | Auth-Token für den Ingestion-Server |
| Prop Firm: Tages-Drawdown-Limit ($) | Wird als Warnschwelle auf der Karte angezeigt |
| Prop Firm: Konto-Größe ($, Fallback) | Nur nötig falls Quantower keinen Kontostand liefert (z. B. Broker ohne Balance-Feed) |
| Design: Logo-Pfad (PNG) | Dein Logo, erscheint oben links auf der Karte |
| Design: Trader-Name | Erscheint auf der Karte und in der Telegram-Caption |
| Aktiver Trade: Trade-Tag | Vor Trade-Schluss eintragen — wird auf der Karte angezeigt, danach zurückgesetzt |
| Aktiver Trade: Account-ID (leer = alle) | Nur nötig, wenn mehrere Konten auf demselben Symbol parallel laufen (z. B. Eval + Funded) |

**Wichtig:** `Core.Instance.TradeAdded` ist in Quantower ein **globales** Event (alle Symbole/Konten der Plattform). Der Indikator filtert automatisch auf das Symbol des Charts, auf dem er liegt — pro Symbol/Chart also einmal hinzufügen.

---

## Update

Der Indikator prüft beim Start, ob eine neue Version verfügbar ist, und zeigt das im Status-Panel oben rechts im Chart an (`Update vX verfügbar`).

**Anders als bei ATAS gibt es (noch) keinen 1-Klick-Installer:** Neue Release-DLL von GitHub herunterladen und die Datei im Indikatorordner (`Settings\Scripts\Indicators\TradeRecap\`) ersetzen, danach Quantower neu starten.

---

## Unterstützte Märkte

Tick-Größe und Tick-Wert werden automatisch aus dem Quantower-Symbol gelesen (`Symbol.TickSize`, `Symbol.GetTickCost(price)`). Als Fallback sind folgende Futures eingebaut:

| Symbol | Tick-Größe | Tick-Wert |
|---|---|---|
| ES | 0,25 | $12,50 |
| NQ | 0,25 | $5,00 |
| MES | 0,25 | $1,25 |
| MNQ | 0,25 | $0,50 |
| CL | 0,01 | $10,00 |
| GC | 0,10 | $10,00 |
| RTY | 0,10 | $5,00 |
| YM | 1,00 | $5,00 |

Alle anderen Symbole (CFDs, Krypto etc.) werden mit Tick-Wert 1:1 berechnet, sofern Quantower keinen Wert liefert.

---

## Bekannte Unterschiede zur ATAS-Version

Siehe [CHANGELOG.md](CHANGELOG.md) — u. a. kein Auto-Installer, Tages-P&L rein selbstberechnet, Mini-Chart-Timezone noch nicht live getestet.

---

## Versionsschema

Versionen folgen dem Format `YYMMDD` (z. B. `260714` = 14. Juli 2026).

---

## Lizenz & Hinweis

Dieser Indikator ist ein Community-Tool von Munich Traders und wird kostenlos bereitgestellt. Er dient ausschließlich der Dokumentation und sendet keine Handelssignale. Trading mit Futures und CFDs ist mit erheblichen Risiken verbunden.
