using System;
using System.Threading.Tasks;
using Ds2.UI.Core;

namespace Promaker.Services;

/// <summary>
/// 파일 입출력을 담당하는 서비스 인터페이스
/// </summary>
public interface IFileService
{
    /// <summary>
    /// 프로젝트를 JSON 파일로 저장
    /// </summary>
    /// <param name="filePath">저장할 파일 경로</param>
    /// <param name="store">저장할 DsStore</param>
    /// <returns>저장 성공 여부</returns>
    Task<bool> SaveProjectAsync(string filePath, DsStore store);

    /// <summary>
    /// JSON 파일에서 프로젝트 로드
    /// </summary>
    /// <param name="filePath">로드할 파일 경로</param>
    /// <returns>로드된 DsStore, 실패 시 null</returns>
    Task<DsStore?> LoadProjectAsync(string filePath);

    /// <summary>
    /// AASX 파일로 내보내기
    /// </summary>
    /// <param name="filePath">내보낼 AASX 파일 경로</param>
    /// <param name="store">내보낼 DsStore</param>
    /// <returns>내보내기 성공 여부</returns>
    Task<bool> ExportAasxAsync(string filePath, DsStore store);

    /// <summary>
    /// AASX 파일에서 가져오기
    /// </summary>
    /// <param name="filePath">가져올 AASX 파일 경로</param>
    /// <returns>가져온 DsStore, 실패 시 null</returns>
    Task<DsStore?> ImportAasxAsync(string filePath);

    /// <summary>
    /// Mermaid 파일에서 가져오기
    /// </summary>
    /// <param name="filePath">가져올 Mermaid 파일 경로</param>
    /// <returns>가져온 DsStore, 실패 시 null</returns>
    Task<DsStore?> ImportMermaidAsync(string filePath);

    /// <summary>
    /// 파일 경로가 특정 확장자인지 확인
    /// </summary>
    bool HasExtension(string path, string extension);
}
