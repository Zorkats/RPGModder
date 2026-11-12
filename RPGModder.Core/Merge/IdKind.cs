namespace RPGModder.Core.Merge;

/// <summary>The kinds of id a mod can add and a remap can move (guide 3.11). Step 1 moves <c>MapEvent</c> and <c>Map</c>.</summary>
public enum IdKind
{
    MapEvent,
    Map,
    Item,
    Weapon,
    Armor,
    Skill,
    State,
    Enemy,
    Troop,
    CommonEvent,
    Actor,
    Class,
    Animation,
    Tileset,
    Switch,
    Variable
}
