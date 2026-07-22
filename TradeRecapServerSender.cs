using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Sendet jeden abgeschlossenen Trade als JSON an den Munich-Traders-Server,
/// der die Daten in die zentrale CSV (TradeRecapTrades.csv) anhaengt.
/// </summary>
internal static class TradeRecapServerSender
{
    /// <summary>
    /// Sendet den Trade an den zentralen Server. Rückgabe: null bei Erfolg, sonst
    /// eine Fehlerbeschreibung (auch wenn Server-URL/Token fehlen).
    /// </summary>
    internal static async Task<string?> SendAsync(
        string serverUrl,
        string authToken,
        PositionRecord record,
        DailyStatsSnapshot stats,
        string traderName,
        HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(authToken))
            return "Server-URL oder Token nicht gesetzt";

        var payload = new Dictionary<string, object?>
        {
            ["TraderName"]       = traderName,
            ["Date"]             = record.CloseTime.ToString("yyyy-MM-dd"),
            ["Time"]             = record.CloseTime.ToString("HH:mm:ss"),
            ["Symbol"]           = record.Symbol,
            ["Direction"]        = record.Direction == PositionDirection.Long ? "LONG" : "SHORT",
            ["Entry"]            = record.AvgEntryPrice.ToString("F5"),
            ["Exit"]             = record.AvgExitPrice.ToString("F5"),
            ["Contracts"]        = record.Contracts,
            ["PnL_Points"]       = record.PnlPoints.ToString("F4"),
            ["PnL_Ticks"]        = record.PnlTicks,
            ["PnL_USD"]          = record.PnlUsd.ToString("F2"),
            ["MAE_Points"]       = record.MAE.ToString("F4"),
            ["MAE_Ticks"]        = record.MAETicks,
            ["MAE_USD"]          = record.MAEUsd.ToString("F2"),
            ["MFE_Points"]       = record.MFE.ToString("F4"),
            ["MFE_Ticks"]        = record.MFETicks,
            ["MFE_USD"]          = record.MFEUsd.ToString("F2"),
            ["Duration_Seconds"] = (int)record.Duration.TotalSeconds,
            ["OpenTime"]         = record.OpenTime.ToString("O"),
            ["CloseTime"]        = record.CloseTime.ToString("O"),
            ["TradeTag"]         = record.TradeTag,
            ["AccountId"]        = record.AccountId,
            ["TickSize"]         = record.TickSize.ToString("F5"),
            ["TickCost"]         = record.TickCost.ToString("F5"),
            ["DailyPnL_USD"]     = stats.DisplayPnl.ToString("F2"),
            ["DailyTradesCount"] = stats.TradesCount,
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, serverUrl) { Content = content };
            req.Headers.Add("X-Auth-Token", authToken);

            var response = await client.SendAsync(req).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                string error = $"HTTP {(int)response.StatusCode}: {body}";
                Console.Error.WriteLine($"[TradeRecap] Server-Fehler {error}");
                return error;
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Server-Exception: {ex.Message}");
            return ex.Message;
        }
    }
}
