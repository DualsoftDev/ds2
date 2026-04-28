using System;
using System.Collections.Generic;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// 신호 템플릿 원천 데이터 제공자.
///
/// 설계 전환 후:
///   • 영구 디스크 파일 생성/편집은 수행하지 않는다.
///   • <see cref="DefaultTemplatesRO"/> 는 신규 프로젝트/SystemType 에 대한 임베디드 fallback 이며,
///     실제 편집 결과는 모두 AASX 내 FBTagMapPresets 에만 저장된다.
///   • <see cref="XgiTemplatePath"/> 만 예외적으로 디스크 파일 (배포 시 동봉된 XGI_Template.xml) 경로를 노출한다.
/// </summary>
public static class TemplateManager
{
    /// <summary>
    /// XGI 프로젝트 템플릿 파일 경로 (실행 파일과 함께 배포된 XGI_Template.xml)
    /// </summary>
    public static string XgiTemplatePath =>
        Path.Combine(AppContext.BaseDirectory, "Template", "XGI_Template.xml");

    /// <summary>기본 템플릿 문자열 테이블 — 신규 SystemType Preset seed 용 fallback</summary>
    public static IReadOnlyDictionary<string, string> DefaultTemplatesRO => DefaultTemplates;

    private static readonly Dictionary<string, string> DefaultTemplates = new()
    {
        ["system_base.txt"] = @"# System Base Address Configuration
# 시스템 타입별 글로벌 주소 설정 (신규 Preset seed 용)

@SYSTEM RBT
@IW_BASE 3000
@QW_BASE 3000
@MW_BASE 30000

@SYSTEM PIN
@IW_BASE 2000
@QW_BASE 2000
@MW_BASE 20000
",
        ["flow_base.txt"] = @"# Flow Base Address Configuration
# Flow별 로컬 주소 설정 (신규 Preset seed 용)
#
# 예시:
# @FLOW Flow1
# @IW_BASE 4000
# @QW_BASE 4000
# @MW_BASE 10000
#
# @FLOW Flow2
# @IW_BASE 4100
# @QW_BASE 4100
# @MW_BASE 10100
",
        ["RBT.txt"] = @"# RBT (Robot) 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_WRS_$(D)_$(A)
RET: W_$(F)_WRS_$(D)_$(A)

[QW]
ADV: W_$(F)_SOL_$(D)_$(A)
RET: W_$(F)_SOL_$(D)_$(A)

[MW]
ADV: W_$(F)_M_$(D)_$(A)
RET: W_$(F)_M_$(D)_$(A)
",
        ["PIN.txt"] = @"# PIN 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
UP: W_$(F)_WRS_$(D)_$(A)
DOWN: W_$(F)_WRS_$(D)_$(A)

[QW]
UP: W_$(F)_SOL_$(D)_$(A)
DOWN: W_$(F)_SOL_$(D)_$(A)

[MW]
UP: W_$(F)_M_$(D)_$(A)
DOWN: W_$(F)_M_$(D)_$(A)
",
        ["CLAMP.txt"] = @"# CLAMP 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
CLOSE: W_$(F)_WRS_$(D)_$(A)
OPEN: W_$(F)_WRS_$(D)_$(A)

[QW]
CLOSE: W_$(F)_SOL_$(D)_$(A)
OPEN: W_$(F)_SOL_$(D)_$(A)

[MW]
CLOSE: W_$(F)_M_$(D)_$(A)
OPEN: W_$(F)_M_$(D)_$(A)
",
        ["LATCH.txt"] = @"# LATCH 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
LOCK: W_$(F)_WRS_$(D)_$(A)
UNLOCK: W_$(F)_WRS_$(D)_$(A)

[QW]
LOCK: W_$(F)_SOL_$(D)_$(A)
UNLOCK: W_$(F)_SOL_$(D)_$(A)

[MW]
LOCK: W_$(F)_M_$(D)_$(A)
UNLOCK: W_$(F)_M_$(D)_$(A)
",
        ["Unit.txt"] = @"# Unit 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_WRS_$(D)_$(A)
RET: W_$(F)_WRS_$(D)_$(A)

[QW]
ADV: W_$(F)_SOL_$(D)_$(A)
RET: W_$(F)_SOL_$(D)_$(A)

[MW]
ADV: W_$(F)_M_$(D)_$(A)
RET: W_$(F)_M_$(D)_$(A)
",
        ["UpDn.txt"] = @"# UpDn 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
UP: W_$(F)_WRS_$(D)_$(A)
DOWN: W_$(F)_WRS_$(D)_$(A)

[QW]
UP: W_$(F)_SOL_$(D)_$(A)
DOWN: W_$(F)_SOL_$(D)_$(A)

[MW]
UP: W_$(F)_M_$(D)_$(A)
DOWN: W_$(F)_M_$(D)_$(A)
",
        ["Motor.txt"] = @"# Motor 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
FWD: W_$(F)_WRS_$(D)_$(A)
BWD: W_$(F)_WRS_$(D)_$(A)

[QW]
FWD: W_$(F)_SOL_$(D)_$(A)
BWD: W_$(F)_SOL_$(D)_$(A)

[MW]
FWD: W_$(F)_M_$(D)_$(A)
BWD: W_$(F)_M_$(D)_$(A)
",
        ["Multi.txt"] = @"# Multi 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명

[IW]
ADV: W_$(F)_WRS_$(D)_$(A)
RET: W_$(F)_WRS_$(D)_$(A)
UP: W_$(F)_WRS_$(D)_$(A)
DOWN: W_$(F)_WRS_$(D)_$(A)
FWD: W_$(F)_WRS_$(D)_$(A)
BWD: W_$(F)_WRS_$(D)_$(A)

[QW]
ADV: W_$(F)_SOL_$(D)_$(A)
RET: W_$(F)_SOL_$(D)_$(A)
UP: W_$(F)_SOL_$(D)_$(A)
DOWN: W_$(F)_SOL_$(D)_$(A)
FWD: W_$(F)_SOL_$(D)_$(A)
BWD: W_$(F)_SOL_$(D)_$(A)

[MW]
ADV: W_$(F)_M_$(D)_$(A)
RET: W_$(F)_M_$(D)_$(A)
UP: W_$(F)_M_$(D)_$(A)
DOWN: W_$(F)_M_$(D)_$(A)
FWD: W_$(F)_M_$(D)_$(A)
BWD: W_$(F)_M_$(D)_$(A)
",
        // ─── Robot 디폴트 템플릿 ─────────────────────────────────────────────
        // weldgrip / weldgrippallet 공통 — 동일 IW/QW 레이아웃.
        // '-' 단독 라인 = 빈 슬롯 (주소 1 비트 예약, 신호 없음).
        ["RobotWeldGrip.txt"] = RobotDefaultTemplate,
        ["RobotWeldGripPallet.txt"] = RobotDefaultTemplate,
    };

    private const string RobotDefaultTemplate = @"# Robot (WeldGrip / WeldGripPallet) 신호 템플릿
# $(F) = Flow명, $(D) = Device명, $(A) = Api명
# '-' 단독 라인 = 빈 슬롯 (주소만 예약, 신호 미생성)

[IW]
# Word 0: 상태
Api_None: W_$(F)_I_$(D)_HOME_POS
Api_None: W_$(F)_I_$(D)_TOTAL_ERR
Api_None: W_$(F)_I_$(D)_READY_ON
Api_None: W_$(F)_I_$(D)_AUTO
Api_None: W_$(F)_I_$(D)_RUNING
Api_None: W_$(F)_I_$(D)_ABNORMAL_SEL
Api_None: W_$(F)_I_$(D)_TIP_DRESSING
Api_None: W_$(F)_I_$(D)_EM_STOP
Api_None: W_$(F)_I_$(D)_LAST_WORK_COMP
Api_None: W_$(F)_I_$(D)_1ST_WORK_COMP
Api_None: W_$(F)_I_$(D)_2ND_WORK_COMP
Api_None: W_$(F)_I_$(D)_3RD_WORK_COMP
Api_None: W_$(F)_I_$(D)_4TH_WORK_COMP
Api_None: W_$(F)_I_$(D)_5TH_WORK_COMP
Api_None: W_$(F)_I_$(D)_6TH_WORK_COMP
Api_None: W_$(F)_I_$(D)_7TH_WORK_COMP
# Word 1: 간섭/통신
Api_None: W_$(F)_I_$(D)_NON_INTF1
Api_None: W_$(F)_I_$(D)_NON_INTF2
Api_None: W_$(F)_I_$(D)_NON_INTF3
Api_None: W_$(F)_I_$(D)_NON_INTF4
Api_None: W_$(F)_I_$(D)_NON_INTF5
Api_None: W_$(F)_I_$(D)_NON_INTF6
Api_None: W_$(F)_I_$(D)_NON_INTF7
Api_None: W_$(F)_I_$(D)_NON_INTF8
Api_None: W_$(F)_I_$(D)_NON_INTF9
Api_None: W_$(F)_I_$(D)_NON_INTF10
Api_None: W_$(F)_I_$(D)_NON_INTF11
Api_None: W_$(F)_I_$(D)_NON_INTF12
-
Api_None: W_$(F)_I_$(D)_EACH_WELD_COMP
Api_None: W_$(F)_I_$(D)_MOTOR_MC_ON
Api_None: W_$(F)_I_$(D)_COMM_CHK
# Word 2: 에러 상세 1
Api_None: W_$(F)_I_$(D)_CONTROLLER_ERR
Api_None: W_$(F)_I_$(D)_COMM_ERR
Api_None: W_$(F)_I_$(D)_AIR_ERR
Api_None: W_$(F)_I_$(D)_WATER_ERR
Api_None: W_$(F)_I_$(D)_GRIPPER_ERR
Api_None: W_$(F)_I_$(D)_SEALER_ERR
Api_None: W_$(F)_I_$(D)_VISION_ERR
Api_None: W_$(F)_I_$(D)_BOLTING_ERR
Api_None: W_$(F)_I_$(D)_LASER_ERR
Api_None: W_$(F)_I_$(D)_CLEANNER_ERR
Api_None: W_$(F)_I_$(D)_MARKING_ERR
Api_None: W_$(F)_I_$(D)_TC_ERR
Api_None: W_$(F)_I_$(D)_TR_ERR
Api_None: W_$(F)_I_$(D)_ATD_ERR
Api_None: W_$(F)_I_$(D)_TIP_CHK_ERR
Api_None: W_$(F)_I_$(D)_WELD_COUNT_ERR
# Word 3: 에러 상세 2 + 바이패스
Api_None: W_$(F)_I_$(D)_WSENSOR_ERR
Api_None: W_$(F)_I_$(D)_PLC_ERR
Api_None: W_$(F)_I_$(D)_FEEDER_ERR
Api_None: W_$(F)_I_$(D)_PLT_PICK_UP_ERR
Api_None: W_$(F)_I_$(D)_BOLTING_ERR1
Api_None: W_$(F)_I_$(D)_BOLTING_ERR2
Api_None: W_$(F)_I_$(D)_BOLTING_ERR3
Api_None: W_$(F)_I_$(D)_BOLTING_ERR4
Api_None: W_$(F)_I_$(D)_BOLTING_ERR5
Api_None: W_$(F)_I_$(D)_BOLTING_ERR6
-
-
Api_None: W_$(F)_I_$(D)_SEALER_CHECK_BYPASS
Api_None: W_$(F)_I_$(D)_TIP_CHK_BYPASS
Api_None: W_$(F)_I_$(D)_VISION_BYPASS
Api_None: W_$(F)_I_$(D)_CLEANNER_BYPASS
# Word 4: PLT (예약 13비트)
Api_None: W_$(F)_I_$(D)_PLT_COUNT_RST_COMP
Api_None: W_$(F)_I_$(D)_PLT_UNLOAD_COMP
Api_None: W_$(F)_I_$(D)_PLT_LAST_UNLOAD
-
-
-
-
-
-
-
-
-
-
-
-
# Word 5: PLT 간섭
Api_None: W_$(F)_I_$(D)_PLT1_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT2_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT3_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT4_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT5_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT6_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT7_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT8_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT9_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT10_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT11_NON_INTF
Api_None: W_$(F)_I_$(D)_PLT12_NON_INTF
-
-
-
-
# Word 6: PROG ECHO (BCD)
Api_None: W_$(F)_I_$(D)_PROG_ECHO_1
Api_None: W_$(F)_I_$(D)_PROG_ECHO_2
Api_None: W_$(F)_I_$(D)_PROG_ECHO_4
Api_None: W_$(F)_I_$(D)_PROG_ECHO_8
Api_None: W_$(F)_I_$(D)_PROG_ECHO_10
Api_None: W_$(F)_I_$(D)_PROG_ECHO_20
Api_None: W_$(F)_I_$(D)_PROG_ECHO_40
Api_None: W_$(F)_I_$(D)_PROG_ECHO_80
Api_None: W_$(F)_I_$(D)_PROG_ECHO_100
Api_None: W_$(F)_I_$(D)_PROG_ECHO_200
Api_None: W_$(F)_I_$(D)_PROG_ECHO_400
Api_None: W_$(F)_I_$(D)_PROG_ECHO_800
Api_None: W_$(F)_I_$(D)_PROG_ECHO_1000
Api_None: W_$(F)_I_$(D)_PROG_ECHO_2000
Api_None: W_$(F)_I_$(D)_PROG_ECHO_4000
Api_None: W_$(F)_I_$(D)_PROG_ECHO_8000
# Word 7: 상호간섭
Api_None: W_$(F)_I_$(D)_MUTUAL_INT1
Api_None: W_$(F)_I_$(D)_MUTUAL_INT2
Api_None: W_$(F)_I_$(D)_MUTUAL_INT3
Api_None: W_$(F)_I_$(D)_MUTUAL_INT4
Api_None: W_$(F)_I_$(D)_MUTUAL_INT5
Api_None: W_$(F)_I_$(D)_MUTUAL_INT6
Api_None: W_$(F)_I_$(D)_MUTUAL_INT7
Api_None: W_$(F)_I_$(D)_MUTUAL_INT8
Api_None: W_$(F)_I_$(D)_MUTUAL_INT9
Api_None: W_$(F)_I_$(D)_MUTUAL_INT10
-
-
-
-
-
-
# Word 8: 용접 PLT 카운트 (BCD)
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_1
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_2
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_4
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_8
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_16
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_32
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_64
Api_None: W_$(F)_I_$(D)_WELD_PLT_COUNT_128
-
-
-
-
-
-
-
-
# Word 9: 예비
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-

[QW]
# Word 0: 제어 + 작업 완료 ECHO
WORK_COMP_RST: W_$(F)_Q_$(D)_WORK_COMP_RST
START: W_$(F)_Q_$(D)_START
Api_None: W_$(F)_Q_$(D)_ERR_RST
Api_None: W_$(F)_Q_$(D)_EXT_READY
Api_None: W_$(F)_Q_$(D)_TIP_DRESS_START
Api_None: W_$(F)_Q_$(D)_PAUSE_NC
Api_None: W_$(F)_Q_$(D)_EACH_WORK_COMP_RST
Api_None: W_$(F)_Q_$(D)_WATER_CUT_OFF
Api_None: W_$(F)_Q_$(D)_LAST_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_1ST_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_2ND_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_3RD_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_4TH_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_5TH_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_6TH_WORK_COMP_ECHO
Api_None: W_$(F)_Q_$(D)_7TH_WORK_COMP_ECHO
# Word 1: 차종 IN_OK
A_1ST_IN_OK: W_$(F)_Q_$(D)_A_1ST_IN_OK
B_1ST_IN_OK: W_$(F)_Q_$(D)_B_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_C_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_D_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_E_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_F_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_G_1ST_IN_OK
Api_None: W_$(F)_Q_$(D)_H_1ST_IN_OK
2ND_IN_OK: W_$(F)_Q_$(D)_2ND_IN_OK
3RD_IN_OK: W_$(F)_Q_$(D)_3RD_IN_OK
4TH_IN_OK: W_$(F)_Q_$(D)_4TH_IN_OK
5TH_IN_OK: W_$(F)_Q_$(D)_5TH_IN_OK
6TH_IN_OK: W_$(F)_Q_$(D)_6TH_IN_OK
7TH_IN_OK: W_$(F)_Q_$(D)_7TH_IN_OK
-
Api_None: W_$(F)_Q_$(D)_COMM_CHK
# Word 2: 프로그램 선택 (BCD)
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_1
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_2
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_4
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_8
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_10
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_20
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_40
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_80
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_100
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_200
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_400
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_800
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_1000
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_2000
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_4000
Api_None: W_$(F)_Q_$(D)_PROG_SELECT_8000
# Word 3: 상호간섭 + 기타제어
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT1
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT2
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT3
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT4
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT5
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT6
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT7
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT8
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT9
Api_None: W_$(F)_Q_$(D)_MUTUAL_INT10
Api_None: W_$(F)_Q_$(D)_TIP_CHANGE_START
Api_None: W_$(F)_Q_$(D)_TIP_CHANGE_END
Api_None: W_$(F)_Q_$(D)_GATE_OPEN_NC
Api_None: W_$(F)_Q_$(D)_EM_STOP_NC
Api_None: W_$(F)_Q_$(D)_BZ_STOP
Api_None: W_$(F)_Q_$(D)_TEST
# Word 4: PLT IN_OK
PLT1_IN_OK: W_$(F)_Q_$(D)_PLT1_IN_OK
PLT2_IN_OK: W_$(F)_Q_$(D)_PLT2_IN_OK
PLT3_IN_OK: W_$(F)_Q_$(D)_PLT3_IN_OK
PLT4_IN_OK: W_$(F)_Q_$(D)_PLT4_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT5_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT6_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT7_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT8_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT9_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT10_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT11_IN_OK
Api_None: W_$(F)_Q_$(D)_PLT12_IN_OK
-
-
-
-
# Word 5: PLT COUNT RST
PLT1_COUNT_RST: W_$(F)_Q_$(D)_PLT1_COUNT_RST
PLT2_COUNT_RST: W_$(F)_Q_$(D)_PLT2_COUNT_RST
PLT3_COUNT_RST: W_$(F)_Q_$(D)_PLT3_COUNT_RST
PLT4_COUNT_RST: W_$(F)_Q_$(D)_PLT4_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT5_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT6_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT7_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT8_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT9_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT10_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT11_COUNT_RST
Api_None: W_$(F)_Q_$(D)_PLT12_COUNT_RST
-
-
-
-
# Word 6: CN (BCD)
Api_None: W_$(F)_Q_$(D)_CN_1
Api_None: W_$(F)_Q_$(D)_CN_2
Api_None: W_$(F)_Q_$(D)_CN_4
Api_None: W_$(F)_Q_$(D)_CN_8
Api_None: W_$(F)_Q_$(D)_CN_10
Api_None: W_$(F)_Q_$(D)_CN_20
Api_None: W_$(F)_Q_$(D)_CN_40
Api_None: W_$(F)_Q_$(D)_CN_80
Api_None: W_$(F)_Q_$(D)_CN_100
Api_None: W_$(F)_Q_$(D)_CN_200
Api_None: W_$(F)_Q_$(D)_CN_400
Api_None: W_$(F)_Q_$(D)_CN_800
Api_None: W_$(F)_Q_$(D)_CN_1000
Api_None: W_$(F)_Q_$(D)_CN_2000
Api_None: W_$(F)_Q_$(D)_CN_4000
Api_None: W_$(F)_Q_$(D)_CN_8000
# Word 7: 예비
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-
# Word 8: CTYPE (BCD)
Api_None: W_$(F)_Q_$(D)_CTYPE_1
Api_None: W_$(F)_Q_$(D)_CTYPE_2
Api_None: W_$(F)_Q_$(D)_CTYPE_4
Api_None: W_$(F)_Q_$(D)_CTYPE_8
Api_None: W_$(F)_Q_$(D)_CTYPE_10
Api_None: W_$(F)_Q_$(D)_CTYPE_20
Api_None: W_$(F)_Q_$(D)_CTYPE_40
Api_None: W_$(F)_Q_$(D)_CTYPE_80
Api_None: W_$(F)_Q_$(D)_CTYPE_100
Api_None: W_$(F)_Q_$(D)_CTYPE_200
Api_None: W_$(F)_Q_$(D)_CTYPE_400
Api_None: W_$(F)_Q_$(D)_CTYPE_800
Api_None: W_$(F)_Q_$(D)_CTYPE_1000
Api_None: W_$(F)_Q_$(D)_CTYPE_2000
Api_None: W_$(F)_Q_$(D)_CTYPE_4000
Api_None: W_$(F)_Q_$(D)_CTYPE_8000
# Word 9: 예비
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-
-
";
}
