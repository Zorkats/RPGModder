namespace RPGModder.Core.Merge;

public enum CollisionKind
{
    ValueClash,
    DeleteVersusEdit,
    IdClash,
    SequenceClash,
    LogicClash,
    PageClash,
    FileClash,
    PluginOrderClash,
    OrderAmbiguous,
    BaseMismatch,
    SwitchBandConflict,
    StateIdOverlap
}
