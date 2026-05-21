<#
.SYNOPSIS
  Ds2.LightHouseService 제거 — service stop + sc.exe delete.

.DESCRIPTION
  done-lighthouse-kb-server.md Phase S1.
  config.json 과 storage (`%PROGRAMDATA%\Dualsoft\LightHouseService\`) 는 **보존** — 운영자가 수동 삭제.
  관리자 권한 필요.

.EXAMPLE
  .\uninstall-service.ps1
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
  [string]$ServiceName = "Ds2.LightHouseService"
)

$ErrorActionPreference = "Stop"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
  Write-Warning "Service 미등록: $ServiceName"
  exit 0
}

if ($svc.Status -eq 'Running') {
  Write-Host "Service 정지 중..."
  Stop-Service -Name $ServiceName -Force
}

& sc.exe delete $ServiceName | Out-Null
Write-Host "Service 제거 완료: $ServiceName"

# EventLog Source 제거 (install 의 짝, review m11). 다른 service 와 공유 안 한다는 가정.
if ([System.Diagnostics.EventLog]::SourceExists("Ds2.LightHouseService")) {
  Remove-EventLog -Source "Ds2.LightHouseService"
  Write-Host "EventLog Source 제거: Ds2.LightHouseService"
}

Write-Host "config.json / storage 는 보존됨 — %PROGRAMDATA%\Dualsoft\LightHouseService\"
