// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Models.Cctv;

/// <summary>
/// CCTV 영상 위에 그려지는 설비 라벨 오버레이 1개.
/// 좌표(x,y,w,h)는 모두 영상 프레임 기준 정규화 0~1 (해상도/스트림 변경에 독립, doc/20 §2).
/// 바인딩 단위는 <b>Flow</b>(대시보드 도면 레이아웃과 동일) 또는 <b>Call(설비)</b> 둘 다 지원한다(doc/20 §3.1).
/// <see cref="FlowId"/> / <see cref="CallId"/> 가 정본이고 *Name 은 표시/조회 캐시(rename 대비).
/// Call 이 지정되면 그 Call 이 더 구체적이므로 상태색은 Call 기준, 없으면 Flow 기준으로 구동한다.
/// </summary>
public record CctvOverlay
{
    /// <summary>오버레이 고유 ID (클라이언트 생성, 예: "ovl_&lt;guid&gt;").</summary>
    public string Id { get; init; } = "";

    /// <summary>대상 카메라 (<see cref="DSPilot.Models.CctvCamera.Name"/>, URL-safe FK).</summary>
    public string CameraName { get; init; } = "";

    /// <summary>바인딩된 Flow (DsProjectService 의 Flow.Id). Flow 단위 오버레이의 정본.</summary>
    public Guid? FlowId { get; init; }

    /// <summary>표시/조회 캐시. rename 대비 — 정본은 <see cref="FlowId"/>.</summary>
    public string? FlowName { get; init; }

    /// <summary>바인딩된 Call (PlcToCallMapperService.GetAllCallTagPairs 의 CallId). Call 단위 오버레이의 정본(옵션).</summary>
    public Guid? CallId { get; init; }

    /// <summary>표시/조회 캐시. rename 대비 — 정본은 <see cref="CallId"/>.</summary>
    public string CallName { get; init; } = "";

    /// <summary>정규화 X (0~1).</summary>
    public double X { get; init; }

    /// <summary>정규화 Y (0~1).</summary>
    public double Y { get; init; }

    /// <summary>정규화 너비 (0~1).</summary>
    public double W { get; init; }

    /// <summary>정규화 높이 (0~1).</summary>
    public double H { get; init; }

    /// <summary>표시 라벨 (없으면 <see cref="CallName"/> 으로 폴백).</summary>
    public string? Label { get; init; }

    /// <summary>연결선 끝점 X (옵션, 정규화 0~1).</summary>
    public double? AnchorX { get; init; }

    /// <summary>연결선 끝점 Y (옵션, 정규화 0~1).</summary>
    public double? AnchorY { get; init; }
}

/// <summary>
/// cctv-overlays.json 파일 모델 (BlueprintLayout 선례와 동일한 단일 JSON 영속 단위).
/// </summary>
public class CctvOverlayFile
{
    public int Version { get; set; } = 1;
    public List<CctvOverlay> Overlays { get; set; } = [];
}
