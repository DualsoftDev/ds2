using AasxEditor.Models;
using AasxEditor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AasxEditor.Components.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private AasxConverterService Converter { get; set; } = default!;
    [Inject] private AasTreeBuilderService TreeBuilder { get; set; } = default!;
    [Inject] private AasEntityExtractor EntityExtractor { get; set; } = default!;
    [Inject] private IAasMetadataStore MetadataStore { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private CircuitTracker CircuitTracker { get; set; } = default!;

    // ===== State =====
    private string? _fileName;
    private string _statusMessage = "";
    private string _statusClass = "";
    private DotNetObjectReference<Home>? _dotnetRef;
    private bool _editorInitialized;
    private bool _contentLoaded;
    private long _currentFileId;
    private string _currentJson = "";
    private AasCore.Aas3_0.Environment? _currentEnv;

    private List<AasTreeNode> _treeNodes = [];
    private AasTreeNode? _selectedNode;
    private string _centerTab = "explorer";
    private List<AasTreeNode> _explorerPath = [];
    private readonly Stack<List<AasTreeNode>> _navBack = new();
    private readonly Stack<List<AasTreeNode>> _navForward = new();

    private List<AasxFileRecord> _loadedFiles = [];
    private long _selectedFileId;
    private string _searchText = "";
    private List<AasEntityRecord> _searchResults = [];

    // 드래그앤드롭
    private bool _isDragOver;
    private bool _scrollColumnsToEnd;
    private bool _showDropChoice;
    private string[] _pendingDropFileNames = [];

    // 모달
    private bool _showBatchEdit;
    private string _batchNewValue = "";
    private string _batchTargetField = "Value";
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
        _navBack.Clear();
        _navForward.Clear();
    }

    private static int Do(Action action) { action(); return 1; }

    // ===== Lifecycle =====
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_scrollColumnsToEnd)
        {
            _scrollColumnsToEnd = false;
            try { await JS.InvokeVoidAsync("FinderColumns.scrollToEnd", "finder-columns"); } catch { }
        }

        if (firstRender)
        {
            _dotnetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("MonacoInterop.init", "monaco-editor", _dotnetRef);
            _editorInitialized = true;

            await JS.InvokeVoidAsync("ResizeHandle.init", "resize-left", "panel-tree", "left");
            await JS.InvokeVoidAsync("DropZone.init", _dotnetRef);

            CircuitTracker.Connect(_circuitId);
            CircuitTracker.OnChanged += OnCircuitChanged;
            UpdateClientCountIndicator();
            await JS.InvokeVoidAsync("ClientCount.initUnload", _dotnetRef);

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
        CircuitTracker.OnChanged -= OnCircuitChanged;
        CircuitTracker.Disconnect(_circuitId);

        if (_editorInitialized)
        {
            try { await JS.InvokeVoidAsync("ClientCount.dispose"); } catch { }
            try { await JS.InvokeVoidAsync("MonacoInterop.dispose"); } catch { }
        }
        _dotnetRef?.Dispose();
    }
}
