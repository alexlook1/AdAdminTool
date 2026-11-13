namespace AdAdminTool.Models;

public class InfoItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    public InfoItem() { }
    public InfoItem(string label, string value)
    {
        Label = label;
        Value = value;
    }
}