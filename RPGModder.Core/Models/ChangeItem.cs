namespace RPGModder.Core.Models;

// View model for displaying changes in the Auto-Packer preview list
public class ChangeItem
{
    public string Type { get; set; }
    public string Path { get; set; }
    public string Color { get; set; }
    public string Size { get; set; }

    public ChangeItem(string type, string path, string color, string? size = null)
    {
        Type = type;
        Path = path;
        Color = color;
        Size = size ?? "";
    }
}
