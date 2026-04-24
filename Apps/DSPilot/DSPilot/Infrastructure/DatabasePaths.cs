namespace DSPilot.Infrastructure;

/// <summary>
/// 통합(Unified) 모드 DSP DB 경로 설정.
/// 모든 테이블이 하나의 SQLite 파일에 공존 (EV2 base + DSP extensions).
/// </summary>
public sealed class DatabasePaths
{
    public string SharedDbPath { get; }
    public bool DspTablesEnabled { get; }

    public DatabasePaths(string sharedDbPath, bool dspTablesEnabled)
    {
        SharedDbPath = sharedDbPath;
        DspTablesEnabled = dspTablesEnabled;
    }

    public string GetFlowTableName() => "dspFlow";
    public string GetCallTableName() => "dspCall";
}
