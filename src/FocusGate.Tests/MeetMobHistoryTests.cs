using System.Text.Json;
using FocusGate.Infrastructure.Services;
using Xunit;

namespace FocusGate.Tests;

public class MeetMobHistoryTests
{
    [Fact]
    public void MeetMobRechargeRecord_CanBeInstantiatedAndMapped()
    {
        var record = new MeetMobRechargeRecord
        {
            TradeTime = "2026-08-09 14:30:00",
            Amount = "1000.00"
        };

        Assert.Equal("2026-08-09 14:30:00", record.TradeTime);
        Assert.Equal("1000.00", record.Amount);
    }

    [Fact]
    public void MeetMobJsonParsing_ExtractsRechargeInfoCorrectly()
    {
        var json = """
        {
          "result": "success",
          "resultBody": {
            "rechargeInfo": [
              {
                "tradeTime": "2026-08-09 14:32:15",
                "rechargeAmount": "100.00"
              },
              {
                "tradeTime": "2026-08-09 10:15:02",
                "rechargeAmount": "500.00"
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("success", root.GetProperty("result").GetString());

        var records = new List<MeetMobRechargeRecord>();
        if (root.GetProperty("resultBody").TryGetProperty("rechargeInfo", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                records.Add(new MeetMobRechargeRecord
                {
                    TradeTime = item.GetProperty("tradeTime").GetString() ?? "",
                    Amount = item.GetProperty("rechargeAmount").GetString() ?? "0"
                });
            }
        }

        Assert.Equal(2, records.Count);
        Assert.Equal("2026-08-09 14:32:15", records[0].TradeTime);
        Assert.Equal("100.00", records[0].Amount);
        Assert.Equal("2026-08-09 10:15:02", records[1].TradeTime);
        Assert.Equal("500.00", records[1].Amount);
    }
}
