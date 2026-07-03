using FocusGate.Core.Enums;
using FocusGate.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Infrastructure.Services;

public static class ModemHelper
{
    public static ModemBrand DetectBrand(string manufacturer, string model)
    {
        var mfg = (manufacturer ?? "").ToLowerInvariant();
        var mdl = (model ?? "").ToLowerInvariant();

        if (mfg.Contains("zte") || mdl.Contains("zte")) return ModemBrand.ZTE;
        if (mfg.Contains("huawei") || mdl.Contains("huawei")) return ModemBrand.Huawei;
        if (mfg.Contains("quectel") || mdl.Contains("quectel")) return ModemBrand.Quectel;
        if (mfg.Contains("simcom") || mdl.Contains("simcom")) return ModemBrand.SIMCom;
        if (mfg.Contains("sierra") || mdl.Contains("sierra")) return ModemBrand.SierraWireless;
        if (mfg.Contains("ericsson") || mdl.Contains("ericsson")) return ModemBrand.Ericsson;
        if (mfg.Contains("mediatek") || mdl.Contains("mtk")) return ModemBrand.MediaTek;

        if (!string.IsNullOrEmpty(mfg)) return ModemBrand.Other;
        return ModemBrand.Unknown;
    }

    public static async Task<long?> ResolveUserIdForModemAsync(FocusGateDbContext db, int modemId, CancellationToken ct = default)
    {
        var um = await db.UserModems
            .Where(um => um.ModemId == modemId && um.RemovedAt == null)
            .FirstOrDefaultAsync(ct);
        return um?.UserId;
    }
}
