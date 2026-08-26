// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Plc;

/// <summary>
/// PLC 태그 로그 엔티티 - plcTagLog 테이블
/// </summary>
public class PlcTagLogEntity
{
    /// <summary>
    /// 로그 ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 태그 ID (외래키)
    /// </summary>
    public int PlcTagId { get; set; }

    /// <summary>
    /// 로그 기록 시간
    /// </summary>
    public DateTime DateTime { get; set; }

    /// <summary>
    /// 태그 값 (TEXT로 저장)
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// 부모 태그
    /// </summary>
    public PlcTagEntity? PlcTag { get; set; }

    /// <summary>
    /// 태그 이름 (조인 시 사용)
    /// </summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// 이 로그를 남긴 PLC 의 소유 System(Guid 문자열). 멀티 PLC 에서 같은 주소를 다른 PLC 도
    /// 쓸 수 있어 주소만으로는 귀속이 안 된다. "" = 귀속 미상(레거시 행·기본 plc 행).
    /// </summary>
    public string SystemId { get; set; } = string.Empty;

    /// <summary>
    /// 태그 주소 (조인 시 사용)
    /// </summary>
    public string Address { get; set; } = string.Empty;
}
