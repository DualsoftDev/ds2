using System;
using System.Collections.Generic;
using Ds2.Core;
using Ds2.UI.Core;

namespace Promaker.Services;

/// <summary>
/// 프로젝트 엔티티 관리를 담당하는 서비스 인터페이스
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// 새 프로젝트 추가
    /// </summary>
    /// <param name="name">프로젝트 이름</param>
    /// <param name="store">DsStore</param>
    /// <returns>추가된 프로젝트 ID</returns>
    Guid AddProject(string name, DsStore store);

    /// <summary>
    /// System 추가
    /// </summary>
    /// <param name="name">System 이름</param>
    /// <param name="isControl">Control 타입 여부</param>
    /// <param name="store">DsStore</param>
    /// <param name="selectedEntityKind">선택된 엔티티 종류</param>
    /// <param name="selectedEntityId">선택된 엔티티 ID</param>
    /// <param name="activeTabKind">활성 탭 종류</param>
    /// <param name="activeTabRootId">활성 탭 루트 ID</param>
    /// <returns>추가된 System ID</returns>
    Guid AddSystem(
        string name,
        bool isControl,
        DsStore store,
        EntityKind? selectedEntityKind = null,
        Guid? selectedEntityId = null,
        TabKind? activeTabKind = null,
        Guid? activeTabRootId = null);

    /// <summary>
    /// Flow 추가
    /// </summary>
    /// <param name="name">Flow 이름</param>
    /// <param name="store">DsStore</param>
    /// <param name="selectedEntityKind">선택된 엔티티 종류</param>
    /// <param name="selectedEntityId">선택된 엔티티 ID</param>
    /// <param name="activeTabKind">활성 탭 종류</param>
    /// <param name="activeTabRootId">활성 탭 루트 ID</param>
    /// <returns>추가된 Flow ID</returns>
    Guid AddFlow(
        string name,
        DsStore store,
        EntityKind? selectedEntityKind = null,
        Guid? selectedEntityId = null,
        TabKind? activeTabKind = null,
        Guid? activeTabRootId = null);

    /// <summary>
    /// Work 추가
    /// </summary>
    /// <param name="name">Work 이름</param>
    /// <param name="flowId">부모 Flow ID</param>
    /// <param name="store">DsStore</param>
    /// <returns>추가된 Work ID</returns>
    Guid AddWork(string name, Guid flowId, DsStore store);

    /// <summary>
    /// Call 추가
    /// </summary>
    /// <param name="name">Call 이름</param>
    /// <param name="workId">부모 Work ID</param>
    /// <param name="store">DsStore</param>
    /// <returns>추가된 Call ID</returns>
    Guid AddCall(string name, Guid workId, DsStore store);

    /// <summary>
    /// 엔티티 삭제
    /// </summary>
    /// <param name="entityIds">삭제할 엔티티 ID 목록</param>
    /// <param name="store">DsStore</param>
    void DeleteEntities(IEnumerable<Guid> entityIds, DsStore store);

    /// <summary>
    /// 엔티티 이름 변경
    /// </summary>
    /// <param name="entityId">엔티티 ID</param>
    /// <param name="newName">새 이름</param>
    /// <param name="store">DsStore</param>
    void RenameEntity(Guid entityId, string newName, DsStore store);

    /// <summary>
    /// 엔티티 이동
    /// </summary>
    /// <param name="requests">이동 요청 목록</param>
    /// <param name="store">DsStore</param>
    void MoveEntities(IEnumerable<MoveEntityRequest> requests, DsStore store);

    /// <summary>
    /// 프로젝트 속성 업데이트
    /// </summary>
    /// <param name="projectId">프로젝트 ID</param>
    /// <param name="properties">업데이트할 속성</param>
    /// <param name="store">DsStore</param>
    void UpdateProjectProperties(Guid projectId, ProjectProperties properties, DsStore store);
}

/// <summary>
/// 프로젝트 속성
/// </summary>
public record ProjectProperties(
    string? Name = null,
    string? Description = null,
    string? Version = null);
