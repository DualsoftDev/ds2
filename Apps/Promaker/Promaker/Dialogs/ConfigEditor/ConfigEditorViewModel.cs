using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.SymbolImport.Matching;
using Promaker.Dialogs.ConfigEditor.Models;

namespace Promaker.Dialogs.ConfigEditor;

public partial class ConfigEditorViewModel : ObservableObject
{
    private readonly string _configPath;
    private JsonNode? _rootNode;
    private bool _loadingRawJson;
    private bool _rawJsonDirty;

    /// <summary>
    /// 저장 완료 후 창 닫기 요청 이벤트
    /// </summary>
    public event Action? CloseRequested;

    // ============================================================
    // FilterExclusions (기존)
    // ============================================================
    [ObservableProperty]
    private string _deviceKeywords = "";

    [ObservableProperty]
    private string _apiKeywords = "";

    [ObservableProperty]
    private string _flowKeywords = "";

    // ============================================================
    // FlowInclusions (기본 체크할 Flow)
    // ============================================================
    [ObservableProperty]
    private string _flowInclusions = "";

    // ============================================================
    // WorkNaming (Work 분할 깊이)
    // ============================================================
    [ObservableProperty]
    private int _workSplitDepth = 0;  // 0 = 자동 (CompoundSuffixes 기반)

    // ============================================================
    // ApiNaming (복합 API 접미사, I/O 타입 토큰)
    // ============================================================
    [ObservableProperty]
    private string _compoundSuffixes = "";

    [ObservableProperty]
    private string _ioTypeTokens = "";

    // ============================================================
    // DisplayNaming (Export 시 이름 간소화 규칙) - 단일 토큰 설정으로 통합
    // ============================================================
    [ObservableProperty]
    private string _removeTokensForDisplay = "";

    // ============================================================
    // MappingSets (Common)
    // ============================================================
    public ObservableCollection<MappingSetCategory> MappingCategories { get; } = new();

    // ============================================================
    // SymmetryRules
    // ============================================================
    public ObservableCollection<SymmetryRuleItem> SymmetryRules { get; } = new();

    // ============================================================
    // ExplicitMappings
    // ============================================================
    public ObservableCollection<ExplicitMappingItem> ExplicitMappings { get; } = new();

    // ============================================================
    // NodeConnectionRules
    // ============================================================
    public ObservableCollection<CallSequenceItem> CallSequences { get; } = new();
    public ObservableCollection<WorkConnectionItem> WorkConnections { get; } = new();
    public ObservableCollection<DevicePairItem> DevicePairs { get; } = new();
    public ObservableCollection<AutoPreRuleItem> AutoPreRules { get; } = new();

    // ============================================================
    // Status
    // ============================================================
    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private Brush _statusColor = Brushes.Gray;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _rawJson = "";

    [ObservableProperty]
    private string _previewSampleSymbol = "CV_1_ADV";

    [ObservableProperty]
    private string _previewTokens = "";

    [ObservableProperty]
    private string _previewFlowName = "";

    [ObservableProperty]
    private string _previewWorkName = "";

    [ObservableProperty]
    private string _previewDeviceName = "";

    [ObservableProperty]
    private string _previewApiName = "";

    [ObservableProperty]
    private string _previewCallName = "";

    [ObservableProperty]
    private string _previewDisplayName = "";

    [ObservableProperty]
    private string _previewSummary = "";

    public ConfigEditorViewModel()
        : this(Path.Combine(AppContext.BaseDirectory, "input-matching-config.json"))
    {
    }

    public ConfigEditorViewModel(string configPath)
    {
        _configPath = configPath;
        LoadConfig();
        RefreshPreview();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                StatusMessage = "Config 파일이 없습니다. 새로 생성됩니다.";
                StatusColor = Brushes.Orange;
                return;
            }

            var jsonText = File.ReadAllText(_configPath);
            _rootNode = JsonNode.Parse(jsonText);

            if (_rootNode == null)
            {
                StatusMessage = "Config 파일 파싱 실패";
                StatusColor = Brushes.Red;
                return;
            }

            SetRawJsonSnapshot(jsonText);

            // FilterExclusions 로드
            LoadFilterExclusions();

            // MappingSets 로드 (Common)
            LoadMappingSets();

            // SymmetryRules 로드
            LoadSymmetryRules();

            // ExplicitMappings 로드
            LoadExplicitMappings();

            // NodeConnectionRules 로드
            LoadNodeConnectionRules();

            StatusMessage = "Config 파일 로드 완료";
            StatusColor = Brushes.Green;
        }
        catch (Exception ex)
        {
            StatusMessage = $"로드 오류: {ex.Message}";
            StatusColor = Brushes.Red;
        }
    }

    partial void OnRawJsonChanged(string value)
    {
        if (!_loadingRawJson)
            _rawJsonDirty = true;
    }

    partial void OnPreviewSampleSymbolChanged(string value) => RefreshPreview();
    partial void OnDeviceKeywordsChanged(string value) => RefreshPreview();
    partial void OnApiKeywordsChanged(string value) => RefreshPreview();
    partial void OnFlowInclusionsChanged(string value) => RefreshPreview();
    partial void OnFlowKeywordsChanged(string value) => RefreshPreview();
    partial void OnWorkSplitDepthChanged(int value) => RefreshPreview();
    partial void OnCompoundSuffixesChanged(string value) => RefreshPreview();
    partial void OnIoTypeTokensChanged(string value) => RefreshPreview();
    partial void OnRemoveTokensForDisplayChanged(string value) => RefreshPreview();

    private void SetRawJsonSnapshot(string jsonText)
    {
        _loadingRawJson = true;
        RawJson = jsonText;
        _loadingRawJson = false;
        _rawJsonDirty = false;
    }

    private void SetRawJsonSnapshot(JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        SetRawJsonSnapshot(node.ToJsonString(options));
    }

    private void LoadFilterExclusions()
    {
        var filterExclusions = _rootNode?["FilterExclusions"];
        if (filterExclusions != null)
        {
            DeviceKeywords = GetKeywordsAsString(filterExclusions["DeviceKeywords"]);
            ApiKeywords = GetKeywordsAsString(filterExclusions["ApiKeywords"]);
            FlowKeywords = GetKeywordsAsString(filterExclusions["FlowKeywords"]);
        }

        // FlowInclusions 로드
        var flowInclusions = _rootNode?["FlowInclusions"];
        if (flowInclusions != null)
        {
            FlowInclusions = GetKeywordsAsString(flowInclusions["Flows"]);
        }

        // WorkNaming 로드
        var workNaming = _rootNode?["WorkNaming"];
        if (workNaming != null)
        {
            WorkSplitDepth = workNaming["SplitDepth"]?.GetValue<int>() ?? 0;
        }
        else
        {
            WorkSplitDepth = 0;  // 기본값: 자동 (CompoundSuffixes 기반)
        }

        // ApiNaming 로드
        var apiNaming = _rootNode?["ApiNaming"];
        if (apiNaming != null)
        {
            CompoundSuffixes = GetKeywordsAsString(apiNaming["CompoundSuffixes"]);
            IoTypeTokens = GetKeywordsAsString(apiNaming["IOTypeTokens"]);
        }
        else
        {
            // 기본값
            CompoundSuffixes = "OK\nCOMP\nPOS\nRST\nEND\nCHK";
            IoTypeTokens = "Q\nI\nO\nY\nX\nM";
        }

        // IOTypeTokens가 비어있으면 기본값 설정
        if (string.IsNullOrWhiteSpace(IoTypeTokens))
        {
            IoTypeTokens = "Q\nI\nO\nY\nX\nM";
        }

        // DisplayNaming 로드 - 단일 토큰 설정으로 통합
        var displayNaming = _rootNode?["DisplayNaming"];
        if (displayNaming != null)
        {
            // 새 형식 (RemoveTokens) 우선 사용, 없으면 기존 3개 필드 병합
            var removeTokens = displayNaming["RemoveTokens"];
            if (removeTokens != null)
            {
                RemoveTokensForDisplay = GetKeywordsAsString(removeTokens);
            }
            else
            {
                // 기존 형식에서 마이그레이션: 3개 필드를 병합
                var tokens = new List<string>();
                var flowPrefixes = GetKeywordsAsString(displayNaming["RemoveFlowPrefixes"]);
                var ioTokens = GetKeywordsAsString(displayNaming["RemoveIOTypeTokensForDisplay"]);
                var deviceTokens = GetKeywordsAsString(displayNaming["RemoveDeviceTypeTokens"]);

                if (!string.IsNullOrWhiteSpace(flowPrefixes))
                    tokens.AddRange(flowPrefixes.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                if (!string.IsNullOrWhiteSpace(ioTokens))
                    tokens.AddRange(ioTokens.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                if (!string.IsNullOrWhiteSpace(deviceTokens))
                    tokens.AddRange(deviceTokens.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

                RemoveTokensForDisplay = string.Join("\n", tokens.Distinct());
            }
        }
        else
        {
            // 기본값: 주요 디바이스 타입만
            RemoveTokensForDisplay = "SOL\nWRS\nRS\nLS";
        }
    }

    private void LoadMappingSets()
    {
        MappingCategories.Clear();

        var commonMappingSets = _rootNode?["Common"]?["MappingSets"]?.AsArray();
        if (commonMappingSets != null)
        {
            // 카테고리별 그룹핑
            var categories = new Dictionary<string, MappingSetCategory>
            {
                ["범용 디바이스"] = new MappingSetCategory { Name = "범용 디바이스" },
                ["실린더/SOL"] = new MappingSetCategory { Name = "실린더/SOL" },
                ["로봇"] = new MappingSetCategory { Name = "로봇" },
                ["안전/특수 장비"] = new MappingSetCategory { Name = "안전/특수 장비" },
                ["라인/상태"] = new MappingSetCategory { Name = "라인/상태" },
                ["기타"] = new MappingSetCategory { Name = "기타" }
            };

            foreach (var mappingSet in commonMappingSets)
            {
                if (mappingSet == null) continue;

                var item = new MappingSetItem
                {
                    Name = mappingSet["Name"]?.GetValue<string>() ?? "",
                    DeviceKeywords = GetKeywordsAsString(mappingSet["DeviceKeywords"])
                };

                // APIs 로드
                var apis = mappingSet["Apis"]?.AsArray();
                if (apis != null)
                {
                    foreach (var api in apis)
                    {
                        if (api == null) continue;
                        item.Apis.Add(new ApiMapping
                        {
                            Name = api["Name"]?.GetValue<string>() ?? "",
                            OutputKeywords = GetKeywordsAsString(api["OutputKeywords"]),
                            InputKeywords = GetKeywordsAsString(api["InputKeywords"])
                        });
                    }
                }

                // 카테고리 분류
                var name = item.Name.ToLower();
                var category = name switch
                {
                    var n when n.Contains("범용") || n.Contains("common") || n.Contains("패턴") => "범용 디바이스",
                    var n when n.Contains("sol") || n.Contains("cyl") || n.Contains("실린더") => "실린더/SOL",
                    var n when n.Contains("로봇") || n.Contains("robot") || n.Contains("rb") || n.Contains("in_ok") || n.Contains("out_ok") => "로봇",
                    var n when n.Contains("safety") || n.Contains("safty") || n.Contains("안전") || n.Contains("light") || n.Contains("lamp") || n.Contains("buzzer") => "안전/특수 장비",
                    var n when n.Contains("line") || n.Contains("라인") || n.Contains("gate") || n.Contains("total") || n.Contains("status") || n.Contains("상태") => "라인/상태",
                    _ => "기타"
                };

                categories[category].MappingSets.Add(item);
            }

            // 비어있지 않은 카테고리만 추가
            foreach (var cat in categories.Values.Where(c => c.MappingSets.Count > 0))
            {
                MappingCategories.Add(cat);
            }
        }
    }

    private void LoadSymmetryRules()
    {
        SymmetryRules.Clear();

        var rules = _rootNode?["SymmetryRules"]?.AsArray();
        if (rules == null) return;

        foreach (var rule in rules)
        {
            if (rule == null) continue;
            SymmetryRules.Add(new SymmetryRuleItem
            {
                OutputPattern = rule["OutputPattern"]?.GetValue<string>() ?? "",
                InputPattern = rule["InputPattern"]?.GetValue<string>() ?? "",
                Description = rule["Description"]?.GetValue<string>() ?? ""
            });
        }
    }

    private void LoadExplicitMappings()
    {
        ExplicitMappings.Clear();

        var mappings = _rootNode?["ExplicitMappings"]?.AsArray();
        if (mappings == null) return;

        foreach (var mapping in mappings)
        {
            if (mapping == null) continue;
            ExplicitMappings.Add(new ExplicitMappingItem
            {
                OutputKeyword = mapping["OutputKeyword"]?.GetValue<string>() ?? "",
                InputKeyword = mapping["InputKeyword"]?.GetValue<string>() ?? "",
                Description = mapping["Description"]?.GetValue<string>() ?? ""
            });
        }
    }

    private void LoadNodeConnectionRules()
    {
        CallSequences.Clear();
        WorkConnections.Clear();
        DevicePairs.Clear();
        AutoPreRules.Clear();

        var rules = _rootNode?["NodeConnectionRules"];
        if (rules == null) return;

        // CallSequences 로드
        var callSeqs = rules["CallSequences"]?.AsArray();
        if (callSeqs != null)
        {
            foreach (var seq in callSeqs)
            {
                if (seq == null) continue;
                CallSequences.Add(new CallSequenceItem
                {
                    Name = seq["Name"]?.GetValue<string>() ?? "",
                    Steps = seq["Steps"]?.GetValue<string>() ?? "",
                    Type = seq["Type"]?.GetValue<string>() ?? "StartReset"
                });
            }
        }

        // WorkConnections 로드
        var workConns = rules["WorkConnections"]?.AsArray();
        if (workConns != null)
        {
            foreach (var conn in workConns)
            {
                if (conn == null) continue;
                WorkConnections.Add(new WorkConnectionItem
                {
                    FromPattern = conn["FromPattern"]?.GetValue<string>() ?? "*",
                    ToPattern = conn["ToPattern"]?.GetValue<string>() ?? "*",
                    Type = conn["Type"]?.GetValue<string>() ?? "StartReset",
                    Mode = conn["Mode"]?.GetValue<string>() ?? "Sequential"
                });
            }
        }

        // DevicePairs 로드
        var devicePairs = rules["DevicePairs"]?.AsArray();
        if (devicePairs != null)
        {
            foreach (var pair in devicePairs)
            {
                if (pair == null) continue;
                DevicePairs.Add(new DevicePairItem
                {
                    Device1 = pair["Device1"]?.GetValue<string>() ?? "",
                    Device2 = pair["Device2"]?.GetValue<string>() ?? "",
                    Type = pair["Type"]?.GetValue<string>() ?? "Interlock"
                });
            }
        }

        // AutoPreRules 로드
        var autoPreRules = rules["AutoPreRules"]?.AsArray();
        if (autoPreRules != null)
        {
            foreach (var pre in autoPreRules)
            {
                if (pre == null) continue;
                AutoPreRules.Add(new AutoPreRuleItem
                {
                    SourcePattern = pre["SourcePattern"]?.GetValue<string>() ?? "",
                    TargetPattern = pre["TargetPattern"]?.GetValue<string>() ?? "",
                    Type = pre["Type"]?.GetValue<string>() ?? "AutoPre"
                });
            }
        }
    }

    private static string GetKeywordsAsString(JsonNode? node)
    {
        if (node is not JsonArray array)
            return "";

        var keywords = array.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s));
        return string.Join("\n", keywords);
    }

    private static JsonArray StringToJsonArray(string text)
    {
        var items = text.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct();

        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }
        return array;
    }

    private static readonly char[] PreviewSeparators = { '_', '.', '-', ' ' };

    private static string[] SplitConfigTokens(string text, params string[] fallback)
    {
        var tokens = text.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return tokens.Length > 0 ? tokens : fallback;
    }

    private static string[] SplitSymbol(string symbol) =>
        symbol.Split(PreviewSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsStationCode(string text) =>
        text.Length >= 2
        && char.ToUpperInvariant(text[0]) == 'S'
        && text[1..].All(char.IsDigit);

    private static bool HasNumericSuffix(string text) =>
        text.Length >= 2
        && char.IsLetter(text[0])
        && char.IsDigit(text[^1])
        && !text.All(char.IsDigit);

    private static bool MatchesWildcard(string pattern, string input)
    {
        var normalizedPattern = pattern.Trim().ToUpperInvariant();
        var normalizedInput = input.Trim().ToUpperInvariant();
        if (!normalizedPattern.Contains('*') && !normalizedPattern.Contains('?'))
            return normalizedPattern == normalizedInput;

        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedInput, regex, RegexOptions.IgnoreCase);
    }

    private static string[] TrimBoundaryIoTokens(IReadOnlyList<string> tokens, HashSet<string> ioTokens)
    {
        var start = tokens.Count > 0 && ioTokens.Contains(tokens[0]) ? 1 : 0;
        var end = tokens.Count;
        if (end - start >= 2 && ioTokens.Contains(tokens[end - 1]))
            end--;
        return tokens.Skip(start).Take(end - start).ToArray();
    }

    private static string GetPreviewFlowName(IReadOnlyList<string> tokens)
    {
        if (tokens.Count <= 1)
            return "DEFAULT";
        if (tokens.Count >= 2 && IsStationCode(tokens[0]) && tokens[1].All(char.IsDigit))
            return $"{tokens[0]}_{tokens[1]}";
        return tokens[0];
    }

    private static string GetPreviewWorkName(IReadOnlyList<string> tokens, HashSet<string> ioTokens)
    {
        if (tokens.Count < 2)
            return "DEFAULT";

        var level2 = tokens[1];
        if (ioTokens.Contains(level2))
            return tokens.Count >= 3 ? tokens[2] : "DEFAULT";
        if (HasNumericSuffix(level2))
            return level2;
        if (level2.All(char.IsDigit))
        {
            if (!IsStationCode(tokens[0]))
                return tokens[0];
            return tokens.Count >= 3 ? tokens[2] : "DEFAULT";
        }

        return level2;
    }

    private static string GetPreviewDisplayName(string deviceName, HashSet<string> removeTokens)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return deviceName;

        var processed = deviceName
            .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment =>
            {
                if (!segment.Contains('-', StringComparison.Ordinal))
                    return removeTokens.Contains(segment) ? null : segment;

                var parts = segment
                    .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(part => !removeTokens.Contains(part))
                    .ToArray();
                return parts.Length == 0 ? null : string.Join("-", parts);
            })
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return processed.Length == 0 ? deviceName : string.Join("_", processed);
    }

    private void RefreshPreview()
    {
        try
        {
            var sample = string.IsNullOrWhiteSpace(PreviewSampleSymbol)
                ? "CV_1_ADV"
                : PreviewSampleSymbol.Trim();
            var rawTokens = SplitSymbol(sample);
            var ioTokenList = SplitConfigTokens(IoTypeTokens, "Q", "QX", "QW", "QB", "QD", "I", "IX", "IW", "IB", "ID", "O", "Y", "X", "M");
            var ioTokens = new HashSet<string>(ioTokenList, StringComparer.OrdinalIgnoreCase);
            var compoundSuffixes = SplitConfigTokens(CompoundSuffixes, "OK", "COMP", "POS", "RST", "END", "CHK", "READY", "IN", "ERR", "EXT", "WORK", "1ST", "?ND", "?RD", "?TH");
            var removeTokens = new HashSet<string>(SplitConfigTokens(RemoveTokensForDisplay, "SOL", "WRS", "RS", "LS"), StringComparer.OrdinalIgnoreCase);
            var semanticTokens = TrimBoundaryIoTokens(rawTokens, ioTokens);

            var suffixCount = 0;
            for (var i = semanticTokens.Length - 1; i > 0; i--)
            {
                if (!compoundSuffixes.Any(pattern => MatchesWildcard(pattern, semanticTokens[i])))
                    break;
                suffixCount++;
            }

            var autoApiCount = semanticTokens.Length >= 2 ? Math.Max(1, suffixCount) : 1;
            var autoDeviceCount = Math.Max(0, semanticTokens.Length - autoApiCount);
            var finalApiCount = WorkSplitDepth > 0 && autoDeviceCount > WorkSplitDepth
                ? semanticTokens.Length - WorkSplitDepth
                : autoApiCount;
            finalApiCount = Math.Clamp(finalApiCount, 1, Math.Max(1, semanticTokens.Length));
            var finalDeviceCount = Math.Max(0, semanticTokens.Length - finalApiCount);

            var deviceParts = finalDeviceCount > 0
                ? semanticTokens.Take(finalDeviceCount).ToArray()
                : semanticTokens;
            var apiParts = semanticTokens.Skip(Math.Max(0, semanticTokens.Length - finalApiCount)).ToArray();
            var deviceName = deviceParts.Length == 0 ? sample : string.Join("_", deviceParts);
            var apiName = apiParts.Length == 0 ? "DO" : string.Join("_", apiParts);
            var deviceTokens = SplitSymbol(deviceName);

            PreviewTokens = rawTokens.Length == 0 ? "(empty)" : string.Join(" / ", rawTokens);
            PreviewFlowName = rawTokens.Length == 0 ? "DEFAULT" : GetPreviewFlowName(rawTokens);
            PreviewWorkName = GetPreviewWorkName(deviceTokens, ioTokens);
            PreviewDeviceName = deviceName;
            PreviewApiName = apiName;
            PreviewCallName = $"{deviceName}.{apiName}";
            PreviewDisplayName = $"{GetPreviewDisplayName(deviceName, removeTokens)}.{apiName}";

            var flowMode = SplitConfigTokens(FlowInclusions).Length == 0 ? "자동" : $"{SplitConfigTokens(FlowInclusions).Length}개 지정";
            PreviewSummary =
                $"Flow 선택: {flowMode}\n" +
                $"제외 키워드: Device {SplitConfigTokens(DeviceKeywords).Length}, API {SplitConfigTokens(ApiKeywords).Length}, Flow {SplitConfigTokens(FlowKeywords).Length}\n" +
                $"분해 옵션: WorkDepth {WorkSplitDepth}, API 접미사 {compoundSuffixes.Length}, IO 토큰 {ioTokenList.Length}, 표시 제거 {removeTokens.Count}";
        }
        catch (Exception ex)
        {
            PreviewTokens = "(preview failed)";
            PreviewFlowName = "";
            PreviewWorkName = "";
            PreviewDeviceName = "";
            PreviewApiName = "";
            PreviewCallName = "";
            PreviewDisplayName = "";
            PreviewSummary = ex.Message;
        }
    }

    private void WriteConfig(JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var jsonText = node.ToJsonString(options);
        File.WriteAllText(_configPath, jsonText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetRawJsonSnapshot(jsonText);
    }

    private void InvalidateMatchingCaches()
    {
        // Config 경로 설정 및 모든 캐시 무효화 (F#에서 캐시됨)
        InputMatching.setConfigPath(_configPath);
        DeviceGroupingUtils.setConfigPath(_configPath);
        InputMatching.invalidateAllCaches();
        DeviceGroupingUtils.invalidateCompoundSuffixesCache();
        DeviceGroupingUtils.invalidateNodeConnectionRulesCache();
        DeviceGroupingUtils.invalidateIOKeywordsCache();
        DeviceGroupingUtils.invalidateIOTypeTokensCache();
        DeviceGroupingUtils.invalidateWorkSplitDepthCache();
        DeviceGroupingUtils.invalidateDisplayNamingCache();
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            if (_rootNode == null)
            {
                _rootNode = JsonNode.Parse("{}")!;
            }

            if (_rawJsonDirty)
            {
                var parsed = JsonNode.Parse(RawJson);
                if (parsed == null)
                {
                    StatusMessage = "Raw JSON 파싱 실패";
                    StatusColor = Brushes.Red;
                    return;
                }

                _rootNode = parsed;
            }

            // FilterExclusions 저장
            SaveFilterExclusions();

            // FlowInclusions 저장
            SaveFlowInclusions();

            // WorkNaming 저장
            SaveWorkNaming();

            // ApiNaming 저장
            SaveApiNaming();

            // DisplayNaming 저장
            SaveDisplayNaming();

            // MappingSets 저장
            SaveMappingSets();

            // SymmetryRules 저장
            SaveSymmetryRules();

            // ExplicitMappings 저장
            SaveExplicitMappings();

            // NodeConnectionRules 저장
            SaveNodeConnectionRules();

            // 파일 저장
            WriteConfig(_rootNode);

            InvalidateMatchingCaches();

            StatusMessage = "저장 완료!";
            StatusColor = Brushes.Green;

            // 저장 완료 후 창 닫기
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 오류: {ex.Message}";
            StatusColor = Brushes.Red;
        }
    }

    private void SaveFilterExclusions()
    {
        if (_rootNode?["FilterExclusions"] == null)
        {
            _rootNode!["FilterExclusions"] = JsonNode.Parse("{}")!;
        }

        var filterExclusions = _rootNode["FilterExclusions"]!;
        filterExclusions["Description"] = "UI 필터에서 기본 체크 해제할 키워드 목록 (대소문자 무시, 부분 일치)";
        filterExclusions["DeviceKeywords"] = StringToJsonArray(DeviceKeywords);
        filterExclusions["ApiKeywords"] = StringToJsonArray(ApiKeywords);
        filterExclusions["FlowKeywords"] = StringToJsonArray(FlowKeywords);
    }

    private void SaveFlowInclusions()
    {
        if (_rootNode?["FlowInclusions"] == null)
        {
            _rootNode!["FlowInclusions"] = JsonNode.Parse("{}")!;
        }

        var flowInclusions = _rootNode["FlowInclusions"]!;
        flowInclusions["Description"] = "Flow 필터링: 비어있으면 S{숫자} 패턴 사용, 항목 있으면 해당 Flow만 체크됨";
        flowInclusions["Flows"] = StringToJsonArray(FlowInclusions);
    }

    private void SaveWorkNaming()
    {
        if (_rootNode?["WorkNaming"] == null)
        {
            _rootNode!["WorkNaming"] = JsonNode.Parse("{}")!;
        }

        var workNaming = _rootNode["WorkNaming"]!;
        workNaming["Description"] = "Work 이름 추출 규칙 - SplitDepth: 0=자동(CompoundSuffixes기반), N=앞에서 N개 토큰을 Work로";
        workNaming["SplitDepth"] = WorkSplitDepth;
    }

    private void SaveApiNaming()
    {
        if (_rootNode?["ApiNaming"] == null)
        {
            _rootNode!["ApiNaming"] = JsonNode.Parse("{}")!;
        }

        var apiNaming = _rootNode["ApiNaming"]!;
        apiNaming["Description"] = "API 이름 추출 규칙 - 마지막 토큰이 CompoundSuffixes에 있으면 앞 토큰과 합침";
        apiNaming["CompoundSuffixes"] = StringToJsonArray(CompoundSuffixes);
        apiNaming["IOTypeTokens"] = StringToJsonArray(IoTypeTokens);
    }

    private void SaveDisplayNaming()
    {
        if (_rootNode?["DisplayNaming"] == null)
        {
            _rootNode!["DisplayNaming"] = JsonNode.Parse("{}")!;
        }

        var displayNaming = _rootNode["DisplayNaming"]!;
        displayNaming["Description"] = "UI 표시용 DisplayName 생성 규칙 - 이름 앞부분에서 지정된 토큰(Flow 접두사, I/O 타입, 디바이스 타입)을 순차적으로 제거";
        displayNaming["RemoveTokens"] = StringToJsonArray(RemoveTokensForDisplay);

        // 기존 필드 제거 (마이그레이션 완료)
        if (displayNaming["RemoveFlowPrefixes"] != null)
            displayNaming.AsObject().Remove("RemoveFlowPrefixes");
        if (displayNaming["RemoveIOTypeTokensForDisplay"] != null)
            displayNaming.AsObject().Remove("RemoveIOTypeTokensForDisplay");
        if (displayNaming["RemoveDeviceTypeTokens"] != null)
            displayNaming.AsObject().Remove("RemoveDeviceTypeTokens");
    }

    private void SaveMappingSets()
    {
        if (_rootNode?["Common"] == null)
        {
            _rootNode!["Common"] = JsonNode.Parse("{}")!;
        }

        var mappingSetsArray = new JsonArray();

        foreach (var category in MappingCategories)
        {
            foreach (var mappingSet in category.MappingSets)
            {
                var setNode = new JsonObject
                {
                    ["Name"] = mappingSet.Name,
                    ["DeviceKeywords"] = StringToJsonArray(mappingSet.DeviceKeywords)
                };

                var apisArray = new JsonArray();
                foreach (var api in mappingSet.Apis)
                {
                    apisArray.Add(new JsonObject
                    {
                        ["Name"] = api.Name,
                        ["OutputKeywords"] = StringToJsonArray(api.OutputKeywords),
                        ["InputKeywords"] = StringToJsonArray(api.InputKeywords)
                    });
                }
                setNode["Apis"] = apisArray;

                mappingSetsArray.Add(setNode);
            }
        }

        _rootNode["Common"]!["MappingSets"] = mappingSetsArray;
    }

    private void SaveSymmetryRules()
    {
        var rulesArray = new JsonArray();
        foreach (var rule in SymmetryRules)
        {
            rulesArray.Add(new JsonObject
            {
                ["OutputPattern"] = rule.OutputPattern,
                ["InputPattern"] = rule.InputPattern,
                ["Description"] = rule.Description
            });
        }
        _rootNode!["SymmetryRules"] = rulesArray;
    }

    private void SaveExplicitMappings()
    {
        var mappingsArray = new JsonArray();
        foreach (var mapping in ExplicitMappings)
        {
            mappingsArray.Add(new JsonObject
            {
                ["OutputKeyword"] = mapping.OutputKeyword,
                ["InputKeyword"] = mapping.InputKeyword,
                ["Description"] = mapping.Description
            });
        }
        _rootNode!["ExplicitMappings"] = mappingsArray;
    }

    private void SaveNodeConnectionRules()
    {
        if (_rootNode?["NodeConnectionRules"] == null)
        {
            _rootNode!["NodeConnectionRules"] = JsonNode.Parse("{}")!;
        }

        var rules = _rootNode["NodeConnectionRules"]!;
        rules["Description"] = "노드 간 연결 규칙 정의 (Export 시 자동 연결에 사용)";

        // CallSequences 저장
        var callSeqsArray = new JsonArray();
        foreach (var seq in CallSequences)
        {
            callSeqsArray.Add(new JsonObject
            {
                ["Name"] = seq.Name,
                ["Steps"] = seq.Steps,
                ["Type"] = seq.Type
            });
        }
        rules["CallSequences"] = callSeqsArray;

        // WorkConnections 저장
        var workConnsArray = new JsonArray();
        foreach (var conn in WorkConnections)
        {
            workConnsArray.Add(new JsonObject
            {
                ["FromPattern"] = conn.FromPattern,
                ["ToPattern"] = conn.ToPattern,
                ["Type"] = conn.Type,
                ["Mode"] = conn.Mode
            });
        }
        rules["WorkConnections"] = workConnsArray;

        // DevicePairs 저장
        var devicePairsArray = new JsonArray();
        foreach (var pair in DevicePairs)
        {
            devicePairsArray.Add(new JsonObject
            {
                ["Device1"] = pair.Device1,
                ["Device2"] = pair.Device2,
                ["Type"] = pair.Type
            });
        }
        rules["DevicePairs"] = devicePairsArray;

        // AutoPreRules 저장
        var autoPreArray = new JsonArray();
        foreach (var pre in AutoPreRules)
        {
            autoPreArray.Add(new JsonObject
            {
                ["SourcePattern"] = pre.SourcePattern,
                ["TargetPattern"] = pre.TargetPattern,
                ["Type"] = pre.Type
            });
        }
        rules["AutoPreRules"] = autoPreArray;
    }

    [RelayCommand]
    private void AddSymmetryRule()
    {
        SymmetryRules.Add(new SymmetryRuleItem
        {
            OutputPattern = "_NEW_",
            InputPattern = "_NEW_",
            Description = "새 대칭 규칙"
        });
    }

    [RelayCommand]
    private void RemoveSymmetryRule(SymmetryRuleItem rule)
    {
        if (rule != null)
            SymmetryRules.Remove(rule);
    }

    [RelayCommand]
    private void AddExplicitMapping()
    {
        ExplicitMappings.Add(new ExplicitMappingItem
        {
            OutputKeyword = "NEW_OUTPUT",
            InputKeyword = "NEW_INPUT",
            Description = "새 명시적 매핑"
        });
    }

    [RelayCommand]
    private void RemoveExplicitMapping(ExplicitMappingItem mapping)
    {
        if (mapping != null)
            ExplicitMappings.Remove(mapping);
    }

    // ============================================================
    // NodeConnectionRules Commands
    // ============================================================
    [RelayCommand]
    private void AddCallSequence()
    {
        CallSequences.Add(new CallSequenceItem
        {
            Name = "새 시퀀스",
            Steps = "START,*_IN_OK,WORK_COMP_RST",
            Type = "StartReset"
        });
    }

    [RelayCommand]
    private void RemoveCallSequence(CallSequenceItem item)
    {
        if (item != null)
            CallSequences.Remove(item);
    }

    [RelayCommand]
    private void AddWorkConnection()
    {
        WorkConnections.Add(new WorkConnectionItem
        {
            FromPattern = "*_W*",
            ToPattern = "*_WorkFinish*",
            Type = "StartReset",
            Mode = "Sequential"
        });
    }

    [RelayCommand]
    private void RemoveWorkConnection(WorkConnectionItem item)
    {
        if (item != null)
            WorkConnections.Remove(item);
    }

    [RelayCommand]
    private void AddDevicePair()
    {
        DevicePairs.Add(new DevicePairItem
        {
            Device1 = "SV",
            Device2 = "CT",
            Type = "Interlock"
        });
    }

    [RelayCommand]
    private void RemoveDevicePair(DevicePairItem item)
    {
        if (item != null)
            DevicePairs.Remove(item);
    }

    [RelayCommand]
    private void AddAutoPreRule()
    {
        AutoPreRules.Add(new AutoPreRuleItem
        {
            SourcePattern = "*BACK_PNL_PART*.ON",
            TargetPattern = "*.1ST_IN_OK",
            Type = "AutoPre"
        });
    }

    [RelayCommand]
    private void RemoveAutoPreRule(AutoPreRuleItem item)
    {
        if (item != null)
            AutoPreRules.Remove(item);
    }

    [RelayCommand]
    private void AddMappingSet(MappingSetCategory category)
    {
        if (category == null) return;

        var newSet = new MappingSetItem
        {
            Name = "새 매핑 세트",
            DeviceKeywords = "*NEW*",
            IsExpanded = true
        };
        newSet.Apis.Add(new ApiMapping
        {
            Name = "API1",
            OutputKeywords = "OUT",
            InputKeywords = "IN"
        });

        category.MappingSets.Add(newSet);
    }

    [RelayCommand]
    private void RemoveMappingSet(MappingSetItem item)
    {
        if (item == null) return;

        foreach (var category in MappingCategories)
        {
            if (category.MappingSets.Remove(item))
                break;
        }
    }

    [RelayCommand]
    private void AddApi(MappingSetItem mappingSet)
    {
        mappingSet?.Apis.Add(new ApiMapping
        {
            Name = "NEW_API",
            OutputKeywords = "OUT",
            InputKeywords = "IN"
        });
    }

    [RelayCommand]
    private void RemoveApi(ApiMapping api)
    {
        if (api == null) return;

        foreach (var category in MappingCategories)
        {
            foreach (var set in category.MappingSets)
            {
                if (set.Apis.Remove(api))
                    return;
            }
        }
    }

    [RelayCommand]
    private void ViewJson()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var psi = new ProcessStartInfo(_configPath)
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            else
            {
                MessageBox.Show(
                    $"Config 파일이 없습니다.\n경로: {_configPath}",
                    "파일 없음",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"파일 열기 오류: {ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ============================================================
    // Move Up/Down Commands - Generic helper
    // ============================================================
    private static void MoveItemUp<T>(ObservableCollection<T> collection, T item)
    {
        if (item == null) return;
        var index = collection.IndexOf(item);
        if (index > 0)
        {
            collection.Move(index, index - 1);
        }
    }

    private static void MoveItemDown<T>(ObservableCollection<T> collection, T item)
    {
        if (item == null) return;
        var index = collection.IndexOf(item);
        if (index >= 0 && index < collection.Count - 1)
        {
            collection.Move(index, index + 1);
        }
    }

    // ============================================================
    // SymmetryRules Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveSymmetryRuleUp(SymmetryRuleItem item) => MoveItemUp(SymmetryRules, item);

    [RelayCommand]
    private void MoveSymmetryRuleDown(SymmetryRuleItem item) => MoveItemDown(SymmetryRules, item);

    // ============================================================
    // ExplicitMappings Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveExplicitMappingUp(ExplicitMappingItem item) => MoveItemUp(ExplicitMappings, item);

    [RelayCommand]
    private void MoveExplicitMappingDown(ExplicitMappingItem item) => MoveItemDown(ExplicitMappings, item);

    // ============================================================
    // CallSequences Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveCallSequenceUp(CallSequenceItem item) => MoveItemUp(CallSequences, item);

    [RelayCommand]
    private void MoveCallSequenceDown(CallSequenceItem item) => MoveItemDown(CallSequences, item);

    // ============================================================
    // WorkConnections Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveWorkConnectionUp(WorkConnectionItem item) => MoveItemUp(WorkConnections, item);

    [RelayCommand]
    private void MoveWorkConnectionDown(WorkConnectionItem item) => MoveItemDown(WorkConnections, item);

    // ============================================================
    // DevicePairs Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveDevicePairUp(DevicePairItem item) => MoveItemUp(DevicePairs, item);

    [RelayCommand]
    private void MoveDevicePairDown(DevicePairItem item) => MoveItemDown(DevicePairs, item);

    // ============================================================
    // AutoPreRules Move Commands
    // ============================================================
    [RelayCommand]
    private void MoveAutoPreRuleUp(AutoPreRuleItem item) => MoveItemUp(AutoPreRules, item);

    [RelayCommand]
    private void MoveAutoPreRuleDown(AutoPreRuleItem item) => MoveItemDown(AutoPreRules, item);
}
