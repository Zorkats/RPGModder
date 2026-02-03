using System.Collections.Generic;

namespace RPGModder.Core.Models;

public class ModProfile
{
    // The list of Folder Names (e.g. "WidescreenMod") that are enabled, in load order
    public List<string> EnabledMods { get; set; } = new();
    
    // Full load order for all mods (including disabled ones)
    // This preserves the order when mods are toggled on/off
    public List<string> LoadOrder { get; set; } = new();
}