using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using FocusGate.Core.DTOs;
using FocusGate.Core.Enums;
using FocusGate.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace FocusGate.AT.Services;

public partial class AtCommandService : IAtCommandService
{
    private SerialPort? _serialPort;
    private readonly ILogger<AtCommandService> _logger;
    private readonly IConfigProvider _config;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isOpen;
    private bool _disposed;

    public bool IsOpen => _isOpen;
    public string? ComPort => _serialPort?.PortName;
    public bool IsSmsInboxFull { get; private set; }
    public bool LastRequestFailed { get; private set; }

    public AtCommandService(ILogger<AtCommandService> logger, IConfigProvider config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task OpenAsync(string comPort)
    {
        if (_serialPort?.IsOpen == true) return;

        var baudRates = new[] { 9600, 115200, 57600, 19200 };
        SerialPort? lastPort = null;

        foreach (var baud in baudRates)
        {
            SerialPort? port = null;
            try
            {
                port = new SerialPort(comPort, baud, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = _config.Get<int>("serial.read.timeout", 5000),
                    WriteTimeout = 5000,
                    NewLine = "\r\n",
                    Handshake = Handshake.None
                };

                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                port.WriteLine("ATE0");
                await Task.Delay(800);
                port.DiscardInBuffer();

                port.WriteLine("AT");
                await Task.Delay(500);

                var response = "";
                try
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(2000);
                    while (DateTime.UtcNow < deadline && port.BytesToRead > 0)
                    {
                        response += port.ReadExisting();
                        await Task.Delay(100);
                    }
                }
                catch { }

                if (response.Contains("OK"))
                {
                    _serialPort = port;
                    _isOpen = true;
                    _logger.LogInformation("Opened {Port} at {Baud} baud", comPort, baud);
                    return;
                }

                lastPort = port;
                port.Close();
                port.Dispose();
                port = null;
            }
            catch
            {
                try { port?.Close(); port?.Dispose(); } catch { }
                try { lastPort?.Close(); lastPort?.Dispose(); } catch { }
            }
        }

        throw new InvalidOperationException($"No AT response on {comPort} at any baud rate (9600/115200/57600/19200)");
    }

    public async Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
    {
        await _lock.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AtCommandService));
            if (_serialPort == null || !_serialPort.IsOpen)
                throw new InvalidOperationException("Port not open");

            _serialPort.DiscardInBuffer();
            _serialPort.WriteLine(command);
            return await ReadResponseAsync(timeoutMs);
        }
        catch (IOException)
        {
            _isOpen = false;
            throw;
        }
        catch (InvalidOperationException)
        {
            _isOpen = false;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> ReadResponseAsync(int timeoutMs)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen) break;

                if (_serialPort.BytesToRead == 0)
                {
                    await Task.Delay(20);
                    continue;
                }

                var raw = _serialPort.ReadExisting();
                if (string.IsNullOrEmpty(raw)) continue;

                var lines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("AT")) continue;
                    sb.AppendLine(trimmed);

                    if (trimmed == "OK" || trimmed.StartsWith("+CME ERROR") || trimmed.StartsWith("+CMS ERROR") || trimmed.StartsWith("+CUSD:"))
                        return sb.ToString().Trim();
                }
            }
            catch (TimeoutException) { await Task.Delay(20); }
            catch (InvalidOperationException) { break; }
        }

        return sb.ToString().Trim();
    }

    public async Task<bool> IsAliveAsync()
    {
        var resp = await SendCommandAsync("AT");
        return resp.Contains("OK");
    }

    public async Task<string> GetImeiAsync()
    {
        var resp = await SendCommandAsync("AT+CGSN");
        var match = Regex.Match(resp, @"\d{15}");
        if (match.Success) return match.Value;

        resp = await SendCommandAsync("AT+GSN");
        match = Regex.Match(resp, @"\d{15}");
        return match.Success ? match.Value : string.Empty;
    }

    public async Task<string> GetImsiAsync()
    {
        var resp = await SendCommandAsync("AT+CIMI");
        foreach (var line in resp.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"^\d{15}$")) return trimmed;
        }
        return string.Empty;
    }

    public async Task<NetworkRegistration> GetNetworkRegistrationAsync()
    {
        foreach (var cmd in new[] { "AT+CREG?", "AT+CEREG?", "AT+CGREG?" })
        {
            var resp = await SendCommandAsync(cmd);
            var match = Regex.Match(resp, @"\+\w+:\s*\d+,(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var val))
            {
                return val switch
                {
                    1 => NetworkRegistration.Registered,
                    5 => NetworkRegistration.Registered,
                    3 => NetworkRegistration.Denied,
                    _ => NetworkRegistration.Unknown
                };
            }
        }
        return NetworkRegistration.Unknown;
    }

    public async Task<string> GetPhoneNumberViaUssdAsync()
    {
        var phoneCode = _config.Get("modem.ussd.phone_code", "*101#");
        var resp = await SendUssdAsync(phoneCode, 10000);

        var decoded = DecodeUssdResponse(resp);

        if (!string.IsNullOrEmpty(decoded))
        {
            var phoneMatch = Regex.Match(decoded, @"(\d{10,12})");
            if (phoneMatch.Success) return phoneMatch.Value;
        }

        // Fallback: try CNUM
        var cnum = await GetPhoneNumberViaCnumAsync();
        if (!string.IsNullOrEmpty(cnum))
            return cnum;

        _logger.LogWarning("[USSD] Phone detection unavailable (modem does not support USSD via serial)");
        return string.Empty;
    }

    public async Task<string> GetPhoneNumberViaCnumAsync()
    {
        var resp = await SendCommandAsync("AT+CNUM");
        var match = CnumRegex().Match(resp);
        return match.Success ? match.Groups[2].Value : string.Empty;
    }

    public async Task<decimal?> GetBalanceAsync()
    {
        var balanceCode = _config.Get("modem.ussd.balance_code", "*222#");
        var resp = await SendUssdAsync(balanceCode, 10000);

        var decoded = DecodeUssdResponse(resp);

        if (string.IsNullOrEmpty(decoded)) return null;

        // Only parse balance if the response actually contains balance keywords
        if (!decoded.Contains("Solde") && !decoded.Contains("solde") && !decoded.Contains("DA") && !decoded.Contains("DZD"))
        {
            _logger.LogWarning("[USSD] Response does not contain balance: {Decoded}", decoded);
            return null;
        }

        var balanceMatch = BalanceRegex().Match(decoded);
        if (balanceMatch.Success)
        {
            var numStr = balanceMatch.Groups[1].Value;
            if (numStr.Contains(',') && numStr.Contains('.'))
            {
                var lastComma = numStr.LastIndexOf(',');
                var lastDot = numStr.LastIndexOf('.');
                if (lastComma > lastDot)
                    numStr = numStr.Replace(".", "").Replace(",", ".");
                else
                    numStr = numStr.Replace(",", "");
            }
            else if (numStr.Contains(','))
            {
                numStr = numStr.Replace(",", ".");
            }
            if (decimal.TryParse(numStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
                return amount;
        }
        return null;
    }

    private static string? DecodeUssdResponse(string rawResponse)
    {
        // Try hex-encoded UTF-16 response: +CUSD: 0,"hex...",72
        var hexMatch = Regex.Match(rawResponse, @"\+CUSD:\s*\d+,""([0-9A-Fa-f]+)""");
        if (hexMatch.Success)
        {
            var hex = hexMatch.Groups[1].Value;
            // Check if it looks like UTF-16 (pairs of 00xx for ASCII)
            if (hex.Length >= 4 && hex.Substring(0, 2) == "00")
            {
                var sb = new StringBuilder();
                for (int i = 0; i + 3 < hex.Length; i += 4)
                {
                    var code = Convert.ToInt32(hex.Substring(i, 4), 16);
                    if (code > 0) sb.Append((char)code);
                }
                return sb.ToString();
            }
            else
            {
                // Plain ASCII hex
                var sb = new StringBuilder();
                for (int i = 0; i + 1 < hex.Length; i += 2)
                {
                    var code = Convert.ToInt32(hex.Substring(i, 2), 16);
                    if (code > 0) sb.Append((char)code);
                }
                return sb.ToString();
            }
        }

        // Try plain text response: +CUSD: 0,"plain text",...
        var plainMatch = Regex.Match(rawResponse, @"\+CUSD:\s*\d+,""([^""]+)""");
        if (plainMatch.Success) return plainMatch.Groups[1].Value;

        // Return the whole response if it contains readable text
        if (rawResponse.Contains("Solde") || rawResponse.Contains("solde") ||
            rawResponse.Contains("numéro") || rawResponse.Contains("numero"))
            return rawResponse;

        return null;
    }

    public async Task<List<RawSmsMessage>> ReadAllSmsAsync()
    {
        var messages = new List<RawSmsMessage>();

        try
        {
            var resp = await SendCommandAsync("AT+CMGL=\"ALL\"");

            if (string.IsNullOrWhiteSpace(resp) || resp.Contains("+CMS ERROR") || resp.Contains("+CME ERROR"))
                return messages;

            var lines = resp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var partsByRef = new Dictionary<int, List<(int PartNum, RawSmsMessage Msg)>>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "OK" || !line.StartsWith("+CMGL:")) continue;

                var cmglParts = ParseCmglLine(line);
                if (cmglParts == null) continue;

                // Read all subsequent lines until next +CMGL: or OK
                var contentLines = new List<string>();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].Trim();
                    if (next == "OK" || next.StartsWith("+CMGL:")) break;
                    if (next.Length > 0) contentLines.Add(next);
                }
                var rawHex = contentLines.Count > 0 ? contentLines[0] : string.Empty;

                // Check for concatenated SMS UDH in raw hex
                var concatInfo = TryParseConcatenatedInfo(rawHex);

                string content;
                if (concatInfo != null)
                {
                    // Skip UDH bytes, decode only the text part
                    var skipBytes = concatInfo.Value.UdhLength + 1;
                    var hexCharsToSkip = skipBytes * 2;
                    var textHex = rawHex.Length > hexCharsToSkip ? rawHex[hexCharsToSkip..] : string.Empty;
                    content = DecodeSmsContent(textHex);
                }
                else
                {
                    content = DecodeSmsContent(rawHex);
                }

                // Parse SCTS timestamp from modem; fall back to DateTime.UtcNow
                var tzOffset = _config.Get<int>("modem.timezone_offset_hours", 1);
                var receivedAt = ParseSctsTimestamp(cmglParts.Value.Scts ?? "", tzOffset) ?? DateTime.UtcNow;

                var msg = new RawSmsMessage
                {
                    Index = cmglParts.Value.Index,
                    Status = cmglParts.Value.Status,
                    Sender = cmglParts.Value.Sender,
                    ReceivedAt = receivedAt,
                    Content = content
                };

                if (concatInfo != null)
                {
                    var key = concatInfo.Value.Reference;
                    if (!partsByRef.ContainsKey(key))
                        partsByRef[key] = new List<(int, RawSmsMessage)>();
                    partsByRef[key].Add((concatInfo.Value.PartNumber, msg));
                }
                else
                {
                    messages.Add(msg);
                }
            }

            // Merge concatenated SMS parts in order (UDH-based)
            foreach (var kv in partsByRef)
            {
                var sorted = kv.Value.OrderBy(p => p.PartNum).ToList();
                var merged = new StringBuilder();
                foreach (var (_, part) in sorted)
                    merged.Append(part.Content);

                messages.Add(new RawSmsMessage
                {
                    Index = sorted[0].Msg.Index,
                    Status = sorted[0].Msg.Status,
                    Sender = sorted[0].Msg.Sender,
                    ReceivedAt = sorted[0].Msg.ReceivedAt,
                    Content = merged.ToString()
                });
            }

            // Pass 2: merge consecutive-index messages from same sender + same second
            // Handles Mobilis SMSC splitting long SMS without UDH headers
            var mergedFinal = new List<RawSmsMessage>();
            var timeGroups = messages
                .GroupBy(m => (m.Sender, Time: new DateTime(m.ReceivedAt.Year, m.ReceivedAt.Month, m.ReceivedAt.Day, m.ReceivedAt.Hour, m.ReceivedAt.Minute, m.ReceivedAt.Second)))
                .ToList();

            foreach (var group in timeGroups)
            {
                var sorted = group.OrderBy(m => m.Index).ToList();
                var current = sorted[0];

                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].Index == sorted[i - 1].Index + 1)
                    {
                        current = new RawSmsMessage
                        {
                            Index = current.Index,
                            Status = current.Status,
                            Sender = current.Sender,
                            ReceivedAt = current.ReceivedAt,
                            Content = current.Content + sorted[i].Content
                        };
                    }
                    else
                    {
                        mergedFinal.Add(current);
                        current = sorted[i];
                    }
                }
                mergedFinal.Add(current);
            }

            messages = mergedFinal;

            _logger.LogInformation("[CMGL] Parsed {Total} SMS messages (UDH={UdhGroups}, consecutive={ConsecGroups})",
                messages.Count, partsByRef.Count, timeGroups.Count(g => g.Count() > 1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[CMGL] error: {Msg}", ex.Message);
        }

        return messages;
    }

    private static (int Reference, int TotalParts, int PartNumber, int UdhLength)? TryParseConcatenatedInfo(string hexContent)
    {
        if (string.IsNullOrEmpty(hexContent) || hexContent.Length < 4 || !IsHexString(hexContent))
            return null;

        // Byte 0: UDH length (number of bytes after this byte)
        if (!TryParseHexByte(hexContent, 0, out var udhLen))
            return null;

        // UDH length should be small (typically <= 0x0E for concatenated SMS)
        if (udhLen < 2 || udhLen > 0x0E)
            return null;

        // Need at least udhLen bytes of UDH data after the length byte
        var udhDataBytes = udhLen;
        if (hexContent.Length < (1 + udhDataBytes) * 2)
            return null;

        // Scan through UDH Information Elements
        var offset = 1; // byte offset (start after UDH length byte)
        while (offset < 1 + udhDataBytes)
        {
            if (offset + 1 >= 1 + udhDataBytes) break;
            if (!TryParseHexByte(hexContent, offset * 2, out var iei)) break;
            if (!TryParseHexByte(hexContent, (offset + 1) * 2, out var iedl)) break;

            // Check if this IE is concatenated SMS
            if (iei == 0x00 || iei == 0x08)
            {
                var refLen = (iei == 0x08) ? 2 : 1;
                if (iedl < refLen + 2 || offset + 2 + iedl > 1 + udhDataBytes)
                    return null;

                // Reference number
                int reference;
                if (refLen == 1)
                {
                    if (!TryParseHexByte(hexContent, (offset + 2) * 2, out var ref8))
                        return null;
                    reference = ref8;
                }
                else
                {
                    if (!TryParseHexByte(hexContent, (offset + 2) * 2, out var refHi)) return null;
                    if (!TryParseHexByte(hexContent, (offset + 3) * 2, out var refLo)) return null;
                    reference = (refHi << 8) | refLo;
                }

                // Total parts
                var totalOffset = offset + 2 + refLen;
                if (!TryParseHexByte(hexContent, totalOffset * 2, out var totalParts)) return null;

                // Part number
                if (!TryParseHexByte(hexContent, (totalOffset + 1) * 2, out var partNumber)) return null;

                return (reference, totalParts, partNumber, udhLen);
            }

            offset += 2 + iedl;
        }

        return null;
    }

    private static bool TryParseHexByte(string hex, int charIndex, out byte value)
    {
        value = 0;
        if (charIndex < 0 || charIndex + 1 >= hex.Length) return false;
        if (byte.TryParse(hex.Substring(charIndex, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            value = b;
            return true;
        }
        return false;
    }

    private static (int Index, string Status, string Sender, string? Scts)? ParseCmglLine(string line)
    {
        // Format: +CMGL: <index>,<status>,"<sender>",[<alpha>],[<scts>]
        // scts format: "yy/MM/dd,HH:mm:ss+/-zz" (zz = timezone in quarter hours)
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("+CMGL:")) return null;

        var body = trimmed.Substring(6).Trim();
        var fields = SplitCmglFields(body);

        if (fields.Count < 3) return null;

        if (!int.TryParse(fields[0].Trim(), out var index)) return null;

        var status = fields[1].Trim().Trim('"');
        var sender = fields[2].Trim().Trim('"');

        // SCTS is typically at field[4] (after alpha at field[3]), but may also be at field[3] if alpha is empty
        string? scts = null;
        if (fields.Count >= 5)
            scts = fields[4].Trim().Trim('"');
        else if (fields.Count >= 4)
        {
            var candidate = fields[3].Trim().Trim('"');
            if (candidate.Contains('/') && candidate.Contains(':'))
                scts = candidate;
        }

        return (index, status, sender, scts);
    }

    private static DateTime? ParseSctsTimestamp(string scts, int tzOffsetHours)
    {
        // Format: "yy/MM/dd,HH:mm:ss" or "yy/MM/dd,HH:mm:ss+/-zz" (zz in quarter hours)
        try
        {
            var trimmed = scts.Trim();

            // Split date and time by comma
            var commaIdx = trimmed.IndexOf(',');
            if (commaIdx < 0) return null;

            var datePart = trimmed[..commaIdx].Trim();
            var timePart = trimmed[(commaIdx + 1)..].Trim();

            // Parse date: yy/MM/dd
            var dateParts = datePart.Split('/');
            if (dateParts.Length != 3) return null;
            if (!int.TryParse(dateParts[0], out var yy)) return null;
            if (!int.TryParse(dateParts[1], out var MM)) return null;
            if (!int.TryParse(dateParts[2], out var dd)) return null;
            var year = yy < 100 ? 2000 + yy : yy;

            // Check for timezone in time part: HH:mm:ss+/-zz
            int modemTzQuarterHours = 0;
            var timeStr = timePart;
            var tzSep = timePart.LastIndexOfAny(['+', '-']);
            if (tzSep > 0 && int.TryParse(timePart[(tzSep + 1)..], out var qh))
            {
                modemTzQuarterHours = timePart[tzSep] == '+' ? qh : -qh;
                timeStr = timePart[..tzSep];
            }

            var timeParts = timeStr.Split(':');
            if (timeParts.Length < 3) return null;
            if (!int.TryParse(timeParts[0], out var hh)) return null;
            if (!int.TryParse(timeParts[1], out var mm)) return null;
            if (!int.TryParse(timeParts[2], out var ss)) return null;

            var dt = new DateTime(year, MM, dd, hh, mm, ss, DateTimeKind.Utc);

            // If modem provided its own timezone, use that; otherwise use config offset
            if (modemTzQuarterHours != 0)
            {
                var modemOffsetHours = modemTzQuarterHours / 4.0;
                dt = dt.AddHours(-modemOffsetHours);
            }
            else if (tzOffsetHours != 0)
            {
                dt = dt.AddHours(-tzOffsetHours);
            }

            return dt;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SplitCmglFields(string text)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            fields.Add(current.ToString());

        return fields;
    }

    private static string DecodeSmsContent(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return rawContent;

        var trimmed = rawContent.Trim();

        // Strategy 1: Try UTF-16 hex decode (4 hex chars = 1 char)
        if (trimmed.Length >= 4 && trimmed.Length % 4 == 0 && IsHexString(trimmed))
        {
            try
            {
                var sb = new StringBuilder(trimmed.Length / 2);
                for (int i = 0; i < trimmed.Length; i += 4)
                {
                    var codePoint = Convert.ToInt32(trimmed.Substring(i, 4), 16);
                    if (codePoint > 0) sb.Append((char)codePoint);
                }
                var decoded = sb.ToString();
                if (!string.IsNullOrWhiteSpace(decoded) && !LooksLikeGarbage(decoded))
                    return decoded;
            }
            catch { }
        }

        // Strategy 2: Try plain byte decode (2 hex chars = 1 byte, ISO-8859-1 / IRA)
        if (trimmed.Length >= 2 && trimmed.Length % 2 == 0 && IsHexString(trimmed))
        {
            try
            {
                var bytes = new byte[trimmed.Length / 2];
                for (int i = 0; i < trimmed.Length; i += 2)
                    bytes[i / 2] = Convert.ToByte(trimmed.Substring(i, 2), 16);

                // Try GSM 7-bit unpack first
                var gsm = DecodeGsm7BitPacked(bytes);
                if (!string.IsNullOrEmpty(gsm) && !LooksLikeGarbage(gsm))
                    return gsm;

                // Then try ISO-8859-1 (IRA charset)
                var iso = Encoding.GetEncoding("iso-8859-1").GetString(bytes);
                if (!string.IsNullOrWhiteSpace(iso) && !LooksLikeGarbage(iso))
                    return iso;
            }
            catch { }
        }

        return rawContent;
    }

    private static bool LooksLikeGarbage(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        int bad = 0;
        foreach (var c in s)
        {
            if (c < 0x20 && c != '\r' && c != '\n' && c != '\t') bad++;
            else if (c == 0xFFFD) bad++;
            else if (c > 0x7F && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) bad++;
        }
        return bad > s.Length * 0.3;
    }

    private static string? DecodeGsm7BitPacked(byte[] data)
    {
        try
        {
            if (data.Length == 0) return null;

            const string GSM_BASIC = "@£$¥èéùìòç\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ ÆæßÉ"
                + " !\"#¤%&'()*+,-./0123456789:;<=>?¡"
                + "ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿"
                + "abcdefghijklmnopqrstuvwxyzäöñüà";

            var bits = new bool[data.Length * 8];
            for (int i = 0; i < data.Length; i++)
                for (int b = 0; b < 8; b++)
                    bits[i * 8 + b] = (data[i] & (1 << b)) != 0;

            var result = new StringBuilder();
            int charCount = (data.Length * 8) / 7;
            for (int i = 0; i < charCount; i++)
            {
                int value = 0;
                for (int b = 0; b < 7; b++)
                {
                    if (bits[i * 7 + b])
                        value |= (1 << b);
                }
                if (value < GSM_BASIC.Length)
                    result.Append(GSM_BASIC[value]);
                else
                    result.Append('?');
            }

            return result.ToString();
        }
        catch { return null; }
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    public async Task DeleteAllSmsAsync()
    {
        var storages = new[] { "SM", "ME" };
        foreach (var storage in storages)
        {
            try
            {
                await SendCommandAsync($"AT+CPMS=\"{storage}\"");
                var resp = await SendCommandAsync("AT+CMGL=\"ALL\"");
                if (string.IsNullOrWhiteSpace(resp) || resp.Contains("ERROR")) continue;

                var indices = new List<int>();
                var lines = resp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("+CMGL:")) continue;
                    var parts = SplitCmglFields(trimmed.Substring(6).Trim());
                    if (parts.Count > 0 && int.TryParse(parts[0].Trim(), out var idx))
                        indices.Add(idx);
                }

                if (indices.Count > 0)
                {
                    foreach (var idx in indices)
                    {
                        try { await SendCommandAsync($"AT+CMGD={idx}"); }
                        catch (Exception ex) { _logger.LogError(ex, "[CMGD] Delete index {Idx} failed", idx); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[CMGD] Storage={Storage} error: {Msg}", storage, ex.Message);
            }
        }
        try { await SendCommandAsync("AT+CPMS=\"SM\""); } catch { }
    }

    public async Task<string> SendUssdAsync(string code, int timeoutMs = 30000)
    {
        await _lock.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AtCommandService));
            if (_serialPort == null || !_serialPort.IsOpen)
                throw new InvalidOperationException("Port not open");

            var savedReadTimeout = _serialPort.ReadTimeout;
            try
            {
                _serialPort.ReadTimeout = 2000;
                _serialPort.DiscardInBuffer();
                _serialPort.WriteLine($"AT+CUSD=1,\"{code}\",15");

                var fullResponse = new StringBuilder();
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                bool gotOk = false;

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (_serialPort == null || !_serialPort.IsOpen) break;

                        if (_serialPort.BytesToRead > 0)
                        {
                            var raw = _serialPort.ReadExisting();
                            if (!string.IsNullOrEmpty(raw))
                            {
                                _logger.LogDebug("[USSD] Raw ({Len}): {Raw}", raw.Length, raw.Replace("\r", "\\r").Replace("\n", "\\n"));
                                fullResponse.Append(raw);

                                var text = fullResponse.ToString();
                                if (!gotOk && (text.Contains("OK") || text.Contains("ERROR")))
                                {
                                    gotOk = true;
                                }

                                if (text.Contains("+CUSD:"))
                                {
                                    break;
                                }

                                if (text.Contains("+CME ERROR") || text.Contains("+CMS ERROR"))
                                    break;
                            }
                        }

                        await Task.Delay(100);
                    }
                    catch (TimeoutException) { await Task.Delay(100); }
                    catch (InvalidOperationException) { break; }
                }

                var response = fullResponse.ToString().Trim();
                return response;
            }
            finally
            {
                if (!_disposed && _serialPort != null && _serialPort.IsOpen)
                    _serialPort.ReadTimeout = savedReadTimeout;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TrySetCharsetAsync(string charset)
    {
        var resp = await SendCommandAsync($"AT+CSCS=\"{charset}\"");
        return resp.Contains("OK");
    }

    public Task<bool> TryRefreshSessionAsync() => Task.FromResult(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isOpen = false;
        try { _serialPort?.Close(); } catch { }
        try { _serialPort?.Dispose(); } catch { }
        try { _lock.Dispose(); } catch { }
    }

    [GeneratedRegex(@"\+CNUM:\s*""([^""]*)"",\s*""(\+?\d+)""")]
    private static partial Regex CnumRegex();

    [GeneratedRegex(@"(\d[\d.,]+)")]
    private static partial Regex BalanceRegex();
}
