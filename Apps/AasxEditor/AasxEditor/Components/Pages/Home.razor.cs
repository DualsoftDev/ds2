using AasxEditor.Models;
using AasxEditor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Environment = AasCore.Aas3_1.Environment;

namespace AasxEditor.Components.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private AasxConverterService Converter { get; set; } = default!;
    [Inject] private AasTreeBuilderService TreeBuilder { get; set; } = default!;
    [Inject] private AasEntityExtractor EntityExtractor { get; set; } = default!;
    [Inject] private IAasMetadataStore MetadataStore { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    // ===== State =====
    private string? _fileName;
    private string _statusMessage = "";
    private string _statusClass = "";
    private DotNetObjectReference<Home>? _dotnetRef;
    private bool _editorInitialized;
    private bool _contentLoaded;
    private long _currentFileId;
    private string _currentJson = "";
    private AasCore.Aas3_1.Environment? _currentEnv;

    private List<AasTreeNode> _treeNodes = [];
    private AasTreeNode? _selectedNode;
    private string _centerTab = "explorer";
    private bool _propsDirty;
    private List<AasTreeNode> _explorerPath = [];

    private List<AasxFileRecord> _loadedFiles = [];
    private long _selectedFileId;
    private string _searchText = "";
    private List<AasEntityRecord> _searchResults = [];

    // 드래그앤드롭
    private bool _isDragOver;
    private bool _showDropChoice;
    private string[] _pendingDropFileNames = [];

    // 모달
    private bool _showBatchEdit;
    private string _batchNewValue = "";
    private bool _showSaveAs;
    private string _saveAsName = "";

    private static bool _sessionStarted;
    private bool HasContent => _contentLoaded;
    private List<AasTreeNode> ExplorerNodes => _explorerPath.Count == 0 ? _treeNodes : _explorerPath[^1].Children;

    // ===== Helpers =====
    private static string IconClass(AasTreeNode node)
        => node.Icon.ToLower().Replace("{}", "smc").Replace("[]", "sml");

    private void SelectNode(AasTreeNode node)
    {
        _selectedNode = node;
        _propsDirty = false;
    }

    private void SetStatus(string message, string cssClass)
    {
        _statusMessage = message;
        _statusClass = cssClass;
        StateHasChanged();
    }

    private static string TruncateValue(string val, int max = 60)
        => val.Length > max ? val[..max] + "..." : val;

    private string GetFileName(long fileId)
        => _loadedFiles.FirstOrDefault(f => f.Id == fileId)?.FileName ?? "";

    private async Task SyncJsonToEditorAsync(string json)
    {
        _currentJson = json;
        await JS.InvokeVoidAsync("MonacoInterop.setValue", json);
    }

    private void RebuildTree()
    {
        if (_currentEnv is not null)
            _treeNodes = TreeBuilder.BuildTree(_currentEnv);
        _selectedNode = null;
        _explorerPath.Clear();
    }

    private async Task ApplyEnvironmentAsync(Environment env, string json, string fileName)
    {
        _contentLoaded = true;
        _fileName = fileName;
        _currentEnv = env;
        await SyncJsonToEditorAsync(json);
        RebuildTree();
    }

    private async Task RegisterInDbAsync(string fileName, Environment env, string json)
    {
        var shellCount = env.AssetAdministrationShells?.Count ?? 0;
        var submodelCount = env.Submodels?.Count ?? 0;
        var fileRecord = await MetadataStore.AddFileAsync(fileName, fileName, shellCount, submodelCount, json);
        _currentFileId = fileRecord.Id;
        var entities = EntityExtractor.Extract(env);
        await MetadataStore.AddEntitiesAsync(_currentFileId, entities);
    }

    private async Task ResetForNewOpenAsync()
    {
        await ClearDbAsync();
        _searchResults.Clear();
        _searchText = "";
    }

    private async Task ClearDbAsync()
    {
        try
        {
            var files = await MetadataStore.GetFilesAsync();
            foreach (var f in files)
            {
                await MetadataStore.RemoveEntitiesByFileAsync(f.Id);
                await MetadataStore.RemoveFileAsync(f.Id);
            }
        }
        catch { }
    }

    private static int Do(Action action) { action(); return 1; }

    // ===== Lifecycle =====
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotnetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("MonacoInterop.init", "monaco-editor", _dotnetRef);
            _editorInitialized = true;

            await JS.InvokeVoidAsync("ResizeHandle.init", "resize-left", "panel-tree", "left");
            await JS.InvokeVoidAsync("ResizeHandle.init", "resize-right", "panel-props", "right");
            await JS.InvokeVoidAsync("DropZone.init", _dotnetRef);

            if (_sessionStarted)
                await RestoreFromDbAsync();
            else
            {
                await ClearDbAsync();
                _sessionStarted = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_editorInitialized)
        {
            try { await JS.InvokeVoidAsync("MonacoInterop.dispose"); } catch { }
        }
        _dotnetRef?.Dispose();
    }
}
