# ------------------------------------------------------------------
# 규약: echo/printf 출력은 ASCII 전용 — 한글은 주석에만 둘 것.
# ------------------------------------------------------------------
# dist-common.mk — 공용 dist/scp/bump 타겟 정의
#
# Promaker 의 Makefile 이 include 해서 사용한다. Ev2.Backend 와 동일한
# 인터페이스: VERSION_FILE / INSTALLER_EXE(S) 만 정의하면 dist-force /
# dist alias 가 자동 생성된다.
#
# include 하기 전에 반드시 정의해야 하는 변수:
#   VERSION_FILE   — BuildVersion.txt 경로
#   INSTALLER_EXE  — 산출물 .exe 경로 (단일)              [둘 중 하나 정의]
#   INSTALLER_EXES — 산출물 .exe 경로들 공백 구분 (다중)  [둘 중 하나 정의]
#
# include 후 호출 측 Makefile 이 정의해야 하는 타겟:
#   dist-installer — installer .exe 만 만드는 타겟
#
# 자동 정의되는 타겟:
#   dist-force     — dist-installer 후 모든 INSTALLER_EXES 를 scp + patch bump 1회
#   dist           — dist-force alias (DIST_COMMON_SKIP_DIST_RULE 미설정 시)

ifndef VERSION_FILE
$(error VERSION_FILE is not defined — set it before including dist-common.mk)
endif

INSTALLER_EXES += $(INSTALLER_EXE)

ifeq ($(strip $(INSTALLER_EXES)),)
$(error Neither INSTALLER_EXE nor INSTALLER_EXES is defined — set one before including dist-common.mk)
endif

DIST_COMMON_DIR := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))

# scp 대상 — 필요 시 호출 측 Makefile 에서 override 가능.
SCP_DEST ?= download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64

.PHONY: dist-force
dist-force: dist-installer
	@for exe in $(INSTALLER_EXES); do \
		bash "$(DIST_COMMON_DIR)/scp-installer.sh" "$$exe" "$(SCP_DEST)" || exit 1; \
	done
	@bash "$(DIST_COMMON_DIR)/bump-buildversion.sh" "$(VERSION_FILE)"

ifndef DIST_COMMON_SKIP_DIST_RULE
.PHONY: dist
dist: dist-force
endif
