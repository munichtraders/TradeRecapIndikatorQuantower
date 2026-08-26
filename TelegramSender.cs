using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MunichTraders.TradeRecap;

/// <summary>Ein Button für ein Telegram-Inline-Keyboard.</summary>
public readonly record struct TelegramButton(string Label, string CallbackData);

public static class TelegramSender
{
    private const string ApiBase = "https://api.telegram.org/bot";

    /// <summary>
    /// Sendet die Recap-Karte an Telegram. Rückgabe: null bei Erfolg, sonst eine
    /// Fehlerbeschreibung (auch wenn Bot Token/Chat ID fehlen) — der Aufrufer
    /// entscheidet, wie/wo das geloggt wird (Plattform-Log, Konsole, etc.).
    /// </summary>
    public static async Task<string?> SendPhotoAsync(
        string botToken,
        string chatId,
        byte[] imageBytes,
        string caption,
        HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return "Bot Token oder Chat ID nicht gesetzt";

        string url = $"{ApiBase}{botToken}/sendPhoto";

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        content.Add(new ByteArrayContent(imageBytes), "photo", "trade_recap.png");
        content.Add(new StringContent(caption), "caption");
        content.Add(new StringContent("HTML"), "parse_mode");

        try
        {
            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                string error = $"HTTP {(int)response.StatusCode}: {body}";
                Console.Error.WriteLine($"[TradeRecap] Telegram Fehler {error}");
                return error;
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Telegram Exception: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>
    /// Sendet eine Textnachricht mit Inline-Keyboard (für den Start-Fragebogen). Gibt bei
    /// Erfolg die message_id der gesendeten Nachricht zurück (der Aufrufer braucht sie, um
    /// einen späteren Button-Tap eindeutig diesem Schritt zuzuordnen), sonst eine
    /// Fehlerbeschreibung als zweiten Wert.
    /// </summary>
    public static async Task<(long? MessageId, string? Error)> SendMessageWithKeyboardAsync(
        string botToken,
        string chatId,
        string text,
        IReadOnlyList<TelegramButton> buttons,
        HttpClient client,
        int buttonsPerRow = 1)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return (null, "Bot Token oder Chat ID nicht gesetzt");

        string url = $"{ApiBase}{botToken}/sendMessage";

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = BuildInlineKeyboard(buttons, buttonsPerRow),
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            long messageId = doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
            return (messageId, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Bearbeitet eine bereits gesendete Nachricht (Text + Keyboard) statt eine neue zu
    /// senden — hält den Start-Fragebogen als EINE Nachricht, die sich pro Schritt
    /// aktualisiert, statt den Chat mit 4 Einzelnachrichten vollzuschreiben. Leere
    /// Buttons-Liste entfernt das Keyboard (für die Abschluss-Zusammenfassung).
    /// </summary>
    public static async Task<string?> EditMessageWithKeyboardAsync(
        string botToken,
        string chatId,
        long messageId,
        string text,
        IReadOnlyList<TelegramButton> buttons,
        HttpClient client,
        int buttonsPerRow = 1)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return "Bot Token oder Chat ID nicht gesetzt";

        string url = $"{ApiBase}{botToken}/editMessageText";

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = BuildInlineKeyboard(buttons, buttonsPerRow),
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return $"HTTP {(int)response.StatusCode}: {body}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static Dictionary<string, object> BuildInlineKeyboard(IReadOnlyList<TelegramButton> buttons, int buttonsPerRow)
    {
        var rows = new List<object>();
        for (int i = 0; i < buttons.Count; i += buttonsPerRow)
        {
            var row = buttons.Skip(i).Take(buttonsPerRow)
                .Select(b => new Dictionary<string, object> { ["text"] = b.Label, ["callback_data"] = b.CallbackData })
                .ToArray();
            rows.Add(row);
        }
        return new Dictionary<string, object> { ["inline_keyboard"] = rows };
    }

    /// <summary>
    /// Pflicht-Call nach jedem Button-Tap, sonst bleibt der Ladeindikator am Button in
    /// Telegram hängen. Rein informativ (kein Popup-Text nötig).
    /// </summary>
    public static async Task<string?> AnswerCallbackQueryAsync(string botToken, string callbackQueryId, HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(callbackQueryId))
            return "Bot Token oder callback_query_id fehlt";

        string url = $"{ApiBase}{botToken}/answerCallbackQuery";
        try
        {
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("callback_query_id", callbackQueryId),
            });
            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return $"HTTP {(int)response.StatusCode}: {body}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string BuildCaption(PositionRecord record, DailyStatsSnapshot stats, string traderName = "")
    {
        bool isProfit = record.PnlUsd >= 0;
        string emoji = isProfit ? "🟢" : "🔴";
        string dir   = record.Direction == PositionDirection.Long ? "LONG" : "SHORT";
        string sign  = isProfit ? "+" : "";

        var lines = new List<string>();

        // Tatsächlicher Start-/Schluss-Fill (nicht der mengengewichtete Ø-Preis) —
        // das ist der Preis, bei dem der Trader real ein-/ausgestiegen ist.
        decimal actualEntryPrice = record.OpenFills.Count  > 0 ? record.OpenFills[0].Price  : record.AvgEntryPrice;
        decimal actualExitPrice  = record.CloseFills.Count > 0 ? record.CloseFills[^1].Price : record.AvgExitPrice;
        string entrySuffix = record.OpenFills.Count  > 1 ? $" (Ø {record.AvgEntryPrice:F2})" : "";
        string exitSuffix  = record.CloseFills.Count > 1 ? $" (Ø {record.AvgExitPrice:F2})"  : "";

        lines.Add($"{emoji} <b>{record.Symbol} {dir}</b>");

        if (!string.IsNullOrWhiteSpace(traderName))
            lines.Add($"👤 <b>{traderName}</b>");

        lines.AddRange(new[]
        {
            $"P&amp;L: <b>{sign}{record.PnlUsd:F2} $ ({sign}{record.PnlTicks} Ticks)</b>",
            $"Entry: {record.OpenTime:HH:mm:ss} @ {actualEntryPrice:F2}{entrySuffix}",
            $"Exit:  {record.CloseTime:HH:mm:ss} @ {actualExitPrice:F2}{exitSuffix}",
            $"Kontrakte: {record.Contracts}  |  Dauer: {FormatDuration(record.Duration)}",
        });

        if (record.OpenFills.Count > 1 || record.CloseFills.Count > 1)
        {
            string opens  = string.Join(", ", record.OpenFills.Select(f  => $"+{f.Qty}@{f.Price:F2}"));
            string closes = string.Join(", ", record.CloseFills.Select(f => $"-{f.Qty}@{f.Price:F2}"));
            lines.Add($"Fills: {opens} → {closes}");
        }

        lines.Add($"Min: {record.MAETicks:+0;-0} Ticks ({record.MAEUsd:+0.00;-0.00} $)  |  Max: {record.MFETicks:+0;-0} Ticks ({record.MFEUsd:+0.00;-0.00} $)");

        if (!string.IsNullOrWhiteSpace(record.TradeTag))
            lines.Add($"Tag: <i>{record.TradeTag}</i>");

        if (!string.IsNullOrWhiteSpace(record.AccountId))
        {
            string maskedId = record.AccountId.Length > 4
                ? record.AccountId[..4] + new string('*', record.AccountId.Length - 4)
                : record.AccountId;
            lines.Add($"Konto: <i>{maskedId}</i>");
        }

        lines.Add("");
        lines.Add($"📊 Heute: {(stats.DisplayPnl >= 0 ? "+" : "")}{stats.DisplayPnl:F2} $  |  Trades: {stats.TradesCount}");
        lines.Add($"<i>Munich Traders · {DateTime.Now:dd.MM.yyyy HH:mm} CET</i>");

        return string.Join("\n", lines);
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.Seconds}s";
        if (d.TotalHours < 1)  return $"{d.Minutes}m {d.Seconds:D2}s";
        return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
    }
}
