using FocusGate.Infrastructure.Services;

namespace FocusGate.Tests;

public class MeetMobServiceTests
{
    #region ExtractOtpCode

    [Theory]
    [InlineData("The sms verification code for login eCare is 5764", "5764")]
    [InlineData("The sms verification code for login eCare is 4097", "4097")]
    [InlineData("The sms verification code for login eCare is 2812", "2812")]
    [InlineData("The sms verification code for login eCare is 9833", "9833")]
    [InlineData("The sms verification code for login eCare is 5015", "5015")]
    [InlineData("The sms verification code for login eCare is 4599", "4599")]
    [InlineData("The sms verification code for login eCare is 4286", "4286")]
    [InlineData("The sms verification code for login eCare is 3846", "3846")]
    public void ExtractOtpCode_ValidContent_ReturnsCode(string content, string expected)
    {
        var result = MeetMobService.ExtractOtpCode(content);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Vous avez rechargé 4000.00 DZD DA avec succès")]
    [InlineData("Sama, Solde 17.358,16DA")]
    [InlineData("")]
    [InlineData("No verification code here")]
    public void ExtractOtpCode_NoMatch_ReturnsNull(string content)
    {
        var result = MeetMobService.ExtractOtpCode(content);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("The Sms verification code for reseting your password is 8845 ,", "8845")]
    [InlineData("THE SMS VERIFICATION CODE FOR LOGIN ECARE IS 1234", "1234")]
    public void ExtractOtpCode_CaseInsensitive_ReturnsCode(string content, string expected)
    {
        var result = MeetMobService.ExtractOtpCode(content);
        Assert.Equal(expected, result);
    }

    #endregion

    #region FormatPhone

    [Theory]
    [InlineData(213674168034, "0674168034")]
    [InlineData(213555123456, "0555123456")]
    [InlineData(674168034, "0674168034")]
    [InlineData(555123456, "0555123456")]
    public void FormatPhone_ValidNumber_ReturnsFormatted(long phone, string expected)
    {
        var result = MeetMobService.FormatPhone(phone);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(123, "123")]
    public void FormatPhone_ShortNumber_ReturnsRaw(long phone, string expected)
    {
        var result = MeetMobService.FormatPhone(phone);
        Assert.Equal(expected, result);
    }

    #endregion

    #region NormalizeMeetMobAmount

    [Theory]
    [InlineData("2 004.38", "2004.38")]
    [InlineData("2 388,16", "2388.16")]
    [InlineData("1 000", "1000")]
    [InlineData("500.00", "500.00")]
    [InlineData("1 500,50", "1500.50")]
    [InlineData("0", "0")]
    [InlineData("2 000,00", "2000.00")]
    public void NormalizeMeetMobAmount_RemovesSpacesAndFixesCommas(string input, string expected)
    {
        var result = MeetMobService.NormalizeMeetMobAmount(input);
        Assert.Equal(expected, result);
    }

    #endregion

    #region GetRechargeHistory Record Parsing

    [Fact]
    public void MeetMobRechargeRecordDto_DefaultValues()
    {
        var record = new FocusGate.Infrastructure.Services.MeetMobRechargeRecordDto();
        Assert.Equal(string.Empty, record.TradeTime);
        Assert.Equal("0", record.Amount);
    }

    [Fact]
    public void MeetMobRechargeRecordDto_ParsesFromJson()
    {
        var json = "[{\"TradeTime\":\"05-08-2026 18:34:48\",\"Amount\":\"2160.00\"},{\"TradeTime\":\"31-07-2026 19:45:17\",\"Amount\":\"300.00\"}]";
        var records = System.Text.Json.JsonSerializer.Deserialize<List<FocusGate.Infrastructure.Services.MeetMobRechargeRecordDto>>(json);
        Assert.NotNull(records);
        Assert.Equal(2, records.Count);
        Assert.Equal("05-08-2026 18:34:48", records[0].TradeTime);
        Assert.Equal("2160.00", records[0].Amount);
        Assert.Equal("31-07-2026 19:45:17", records[1].TradeTime);
        Assert.Equal("300.00", records[1].Amount);
    }

    #endregion
}

public class MeetMobTokenStoreTests
{
    private readonly string _testDir;

    public MeetMobTokenStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FocusGate_TokenStore_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    ~MeetMobTokenStoreTests()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public async Task SaveAndGet_ValidToken_ReturnsToken()
    {
        var store = CreateStore();
        var token = new MeetMobToken
        {
            Phone = "0674168034",
            CsrfToken = "abc123",
            Cookie = "JSESSIONID=xyz",
            AccountId = "1010038340351",
            SubscriberKey = "1010037986130",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        await store.SaveAsync("603019053265152", token);
        var result = await store.GetAsync("603019053265152");

        Assert.NotNull(result);
        Assert.Equal("0674168034", result.Phone);
        Assert.Equal("abc123", result.CsrfToken);
        Assert.Equal("1010038340351", result.AccountId);
    }

    [Fact]
    public async Task Get_NonExistentImsi_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ExpiredToken_ReturnsNull()
    {
        var store = CreateStore();
        var token = new MeetMobToken
        {
            Phone = "0674168034",
            CsrfToken = "abc123",
            ExpiresAt = DateTime.UtcNow.AddHours(-1)
        };

        await store.SaveAsync("603019053265152", token);
        var result = await store.GetAsync("603019053265152");
        Assert.Null(result);
    }

    [Fact]
    public async Task Remove_ExistingToken_Removes()
    {
        var store = CreateStore();
        var token = new MeetMobToken
        {
            Phone = "0674168034",
            CsrfToken = "abc123",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        await store.SaveAsync("603019053265152", token);
        await store.RemoveAsync("603019053265152");
        var result = await store.GetAsync("603019053265152");
        Assert.Null(result);
    }

    [Fact]
    public async Task Save_MultipleImsis_Independent()
    {
        var store = CreateStore();
        var token1 = new MeetMobToken { Phone = "0674168034", CsrfToken = "t1", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        var token2 = new MeetMobToken { Phone = "0555123456", CsrfToken = "t2", ExpiresAt = DateTime.UtcNow.AddHours(1) };

        await store.SaveAsync("imsi1", token1);
        await store.SaveAsync("imsi2", token2);

        var r1 = await store.GetAsync("imsi1");
        var r2 = await store.GetAsync("imsi2");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("t1", r1.CsrfToken);
        Assert.Equal("t2", r2.CsrfToken);
    }

    [Fact]
    public async Task Save_PersistsToDisk()
    {
        var store1 = CreateStore();
        var token = new MeetMobToken { Phone = "0674168034", CsrfToken = "persist", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        await store1.SaveAsync("imsi1", token);

        var store2 = CreateStore();
        var result = await store2.GetAsync("imsi1");

        Assert.NotNull(result);
        Assert.Equal("persist", result.CsrfToken);
    }

    [Fact]
    public void ExtractBalance_FromRealMeetMobHarJson_ReturnsExpectedBalance()
    {
        var json = "{\"result\":\"success\",\"resultBody\":{\"acctList\":[{\"acctKey\":\"1010062274161\",\"balanceResult\":[{\"balanceType\":\"C_MAIN_ACCOUNT\",\"balanceTypeName\":\"Balance\",\"totalAmount\":\"6 613,30\",\"depositFlag\":\"N\",\"refundFlag\":\"1\",\"currencyID\":\"1044\",\"balanceDetail\":[{\"balanceInstanceID\":\"193500000084881880\",\"amount\":\"6 613,30\"}]}]}]}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        string amountStr = "";
        if (root.TryGetProperty("resultBody", out var rb))
        {
            if (rb.TryGetProperty("acctList", out var acctList) && acctList.ValueKind == System.Text.Json.JsonValueKind.Array && acctList.GetArrayLength() > 0)
            {
                var firstAcct = acctList[0];
                if (firstAcct.TryGetProperty("balanceResult", out var balRes) && balRes.ValueKind == System.Text.Json.JsonValueKind.Array && balRes.GetArrayLength() > 0)
                {
                    var firstBal = balRes[0];
                    if (firstBal.TryGetProperty("totalAmount", out var elem))
                        amountStr = elem.GetString() ?? "";
                }
            }
        }

        amountStr = MeetMobService.NormalizeMeetMobAmount(amountStr);
        Assert.True(decimal.TryParse(amountStr, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var balance));
        Assert.Equal(6613.30m, balance);
    }

    private MeetMobTokenStore CreateStore()
    {
        var store = new MeetMobTokenStore();
        return store;
    }
}
