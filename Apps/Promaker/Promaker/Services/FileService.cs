using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ds2.Aasx;
using Ds2.Mermaid;
using Ds2.Store;
using Ds2.Editor;
using log4net;

namespace Promaker.Services;

/// <summary>
/// 파일 입출력을 담당하는 서비스 구현
/// </summary>
public class FileService : IFileService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FileService));

    public const string FileFilter =
        "All Supported (*.json;*.aasx;*.md)|*.json;*.aasx;*.md|JSON Files (*.json)|*.json|AASX Files (*.aasx)|*.aasx|Mermaid Files (*.md)|*.md";

    public async Task<bool> SaveProjectAsync(string filePath, DsStore store)
    {
        return await Task.Run(() =>
        {
            try
            {
                store.SaveToFile(filePath);
                Log.Info($"Project saved: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Save file '{filePath}' failed", ex);
                throw new FileServiceException($"Failed to save file: {ex.Message}", ex);
            }
        });
    }

    public async Task<DsStore?> LoadProjectAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var store = new DsStore();
                store.LoadFromFile(filePath);
                Log.Info($"Project loaded: {filePath}");
                return store;
            }
            catch (Exception ex)
            {
                Log.Error($"Open file '{filePath}' failed", ex);
                throw new FileServiceException($"Failed to open file: {ex.Message}", ex);
            }
        });
    }

    public async Task<bool> ExportAasxAsync(string filePath, DsStore store)
    {
        return await Task.Run(() =>
        {
            try
            {
                var exported = AasxExporter.exportFromStore(store, filePath);
                if (!exported)
                {
                    Log.Warn($"AASX save failed: no project ({filePath})");
                    throw new FileServiceException("No project available for AASX save.");
                }

                Log.Info($"AASX exported: {filePath}");
                return true;
            }
            catch (FileServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Save AASX '{filePath}' failed", ex);
                throw new FileServiceException($"Failed to save AASX: {ex.Message}", ex);
            }
        });
    }

    public async Task<DsStore?> ImportAasxAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var store = new DsStore();
                if (!AasxImporter.importIntoStore(store, filePath))
                {
                    Log.Warn($"AASX open failed: empty result ({filePath})");
                    throw new FileServiceException("Failed to open AASX file.");
                }

                Log.Info($"AASX imported: {filePath}");
                return store;
            }
            catch (FileServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Open AASX '{filePath}' failed", ex);
                throw new FileServiceException($"Failed to open AASX: {ex.Message}", ex);
            }
        });
    }

    public async Task<DsStore?> ImportMermaidAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var result = MermaidImporter.loadProjectFromFile(filePath);

                if (result.IsError)
                {
                    var errors = string.Join("\n", result.ErrorValue);
                    Log.Warn($"Mermaid import failed: {errors}");
                    throw new FileServiceException($"Mermaid 불러오기 실패:\n{errors}");
                }

                Log.Info($"Mermaid imported: {filePath}");
                return result.ResultValue;
            }
            catch (FileServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Open Mermaid '{filePath}' failed", ex);
                throw new FileServiceException($"Mermaid 불러오기 실패: {ex.Message}", ex);
            }
        });
    }

    public bool HasExtension(string path, string extension)
    {
        return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mermaid 파일로 저장
    /// </summary>
    public async Task<bool> SaveMermaidAsync(string filePath, DsStore store)
    {
        return await Task.Run(() =>
        {
            try
            {
                var result = MermaidExporter.saveProjectToFile(store, filePath);

                if (result.IsError)
                {
                    var errors = string.Join("\n", result.ErrorValue);
                    Log.Warn($"Mermaid save failed: {errors}");
                    throw new FileServiceException($"Mermaid 저장 실패:\n{errors}");
                }

                Log.Info($"Mermaid saved: {filePath}");
                return true;
            }
            catch (FileServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Save Mermaid '{filePath}' failed", ex);
                throw new FileServiceException($"Mermaid 저장 실패: {ex.Message}", ex);
            }
        });
    }
}

/// <summary>
/// 파일 서비스 예외
/// </summary>
public class FileServiceException : Exception
{
    public FileServiceException(string message) : base(message) { }
    public FileServiceException(string message, Exception innerException) : base(message, innerException) { }
}
