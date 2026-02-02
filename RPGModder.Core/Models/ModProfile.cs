using System.Collections.Generic;

namespace RPGModder.Core.Models;

public class ModProfile
{
    // The list of Folder Names (e.g. "WidescreenMod") that are enabled
    public List<string> EnabledMods { get; set; } = new();
}