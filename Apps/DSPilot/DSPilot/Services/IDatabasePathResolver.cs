namespace DSPilot.Services;

/// <summary>
/// Abstracts database path resolution for unified vs split DB modes.
/// In unified mode, both PLC and DSP data share the same plc.db file.
/// In split mode, they use separate files.
/// </summary>
public interface IDatabasePathResolver
{
    /// <summary>
    /// Gets the shared database path (used in unified mode).
    /// This is the single plc.db that contains both EV2 and DSPilot schemas.
    /// </summary>
    string GetSharedDbPath();

    /// <summary>
    /// Gets the PLC database path (used for PlcRepository).
    /// In unified mode, returns GetSharedDbPath().
    /// In split mode, returns the separate PLC database path.
    /// </summary>
    string GetPlcDbPath();

    /// <summary>
    /// Gets the DSPilot database path (used for DspRepository).
    /// In unified mode, returns GetSharedDbPath().
    /// In split mode, returns the separate DSP database path.
    /// </summary>
    string GetDspDbPath();

    /// <summary>
    /// Indicates whether unified mode is enabled.
    /// When true, all repositories use the same database file.
    /// </summary>
    bool IsUnified { get; }
}
