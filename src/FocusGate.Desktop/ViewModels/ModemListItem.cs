namespace FocusGate.Desktop.ViewModels;

public class ModemListItem
{
    public int Id { get; set; }
    public int RowNumber { get; set; }
    public string Imei { get; set; } = "---";
    public string PhoneNumber { get; set; } = "---";
    public bool IsOnline { get; set; }
    public decimal Balance { get; set; }
    public string BalanceFormatted => $"{Balance:N2} DA";
}
