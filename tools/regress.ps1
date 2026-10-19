# 一键回归：Debug/Release 构建 + 核心冒烟 + UI 渲染冒烟，结果汇总到仓库根 .regress.log
#
#   powershell -ExecutionPolicy Bypass -File tools\regress.ps1
#
# 为什么用 PowerShell 脚本而不是一串 && ：
#   1) 构建失败后 --no-build 会**悄悄跑到旧二进制**，得到"绿"的假回归结果——
#      脚本在第 17 行 fail-fast 挡住这个坑（2026-09-16 实际踩中过）。
#   2) PowerShell 5.1 重定向原生命令 stdout 时按 ANSI 码页解码，dotnet 的 UTF-8 中文
#      会双重编码成乱码（`实` → `瀹炰綋`），且 [Console]::OutputEncoding 在此场景下不生效。
#      所以含中文的断言输出一律由被测程序自己写 UTF-8 日志文件（DrawingView_SMOKE_LOG /
#      DrawingView_UISMOKE_LOG），脚本再用 Get-Content -Encoding utf8 读回来。
#
# 本脚本里**不要写中文字面量**：PowerShell 5.1 按 ANSI 读脚本，中文会 mojibake。
# 需要中文路径时用 [char]0x6D4B 这类写法拼，或直接用内联命令调用。

$ErrorActionPreference = "Continue"
# dotnet CLI / MSBuild 默认跟随系统语言输出中文，重定向时会变成乱码（见下方注释）。
# 强制英文即可拿到纯 ASCII 的构建日志。
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:VSLANG = "1033"
$repo = Split-Path $PSScriptRoot -Parent          # 脚本放在 tools/ 下，仓库根是上一级
Set-Location $repo
$log = Join-Path $repo ".regress.log"
$smokeLog = Join-Path $repo ".smoke.log"
$uiLog = Join-Path $repo ".uismoke.log"
foreach ($f in @($log, $smokeLog, $uiLog)) { if (Test-Path $f) { [System.IO.File]::Delete($f) } }

function W([string]$s) { $s | Out-File -FilePath $log -Encoding utf8 -Append }

W "=== BUILD DEBUG ==="
(dotnet build AvaDrawingView.slnx -c Debug --nologo) 2>&1 | Out-File -FilePath $log -Encoding utf8 -Append
$debugExit = $LASTEXITCODE
W "DEBUG EXIT $debugExit"
if ($debugExit -ne 0) { W "!! Debug build failed: --no-build would run STALE binaries. Aborting."; exit 1 }

W ""
W "=== BUILD RELEASE ==="
(dotnet build AvaDrawingView.slnx -c Release --nologo) 2>&1 | Out-File -FilePath $log -Encoding utf8 -Append
W "RELEASE EXIT $LASTEXITCODE"

# 冒烟测试的 stdout 在 PowerShell 5.1 里会被按 ANSI 码页解码（中文乱码），
# 所以丢掉它，只保留程序自己写的 UTF-8 日志文件内容。
W ""
W "=== CORE SMOKE ==="
$env:DrawingView_SMOKE_LOG = $smokeLog
(dotnet run --project tools\SmokeTest -c Debug --no-build) 2>&1 | Out-Null
W "CORE EXIT $LASTEXITCODE"
if (Test-Path $smokeLog) {
  Get-Content $smokeLog -Encoding utf8 | Out-File -FilePath $log -Encoding utf8 -Append
} else { W "!! DrawingView_SMOKE_LOG not produced ($smokeLog)" }

W ""
W "=== UI SMOKE ==="
$env:DrawingView_UISMOKE_LOG = $uiLog
(dotnet run --project tools\UiSmokeTest -c Debug --no-build) 2>&1 | Out-Null
W "UI EXIT $LASTEXITCODE"
if (Test-Path $uiLog) {
  Get-Content $uiLog -Encoding utf8 | Out-File -FilePath $log -Encoding utf8 -Append
} else { W "!! DrawingView_UISMOKE_LOG not produced ($uiLog)" }

# 注意：$log 里同时有按源码页乱码的原生 stdout 与正确的日志文件内容，
# 所以只在日志文件那两段里计数，避免翻倍。
$correct = ""
foreach ($f in @($smokeLog, $uiLog)) {
  if (Test-Path $f) { $correct = $correct + [System.IO.File]::ReadAllText($f) }
}
$pass = ([regex]::Matches($correct, "PASS -")).Count
$fail = ([regex]::Matches($correct, "FAIL -")).Count

W ""
W "=== SUMMARY ==="
W "PASS=$pass FAIL=$fail"
W "(DebugExit=$debugExit; details in $log)"

foreach ($f in @($smokeLog, $uiLog)) { if (Test-Path $f) { [System.IO.File]::Delete($f) } }
exit $(if ($fail -gt 0) { 1 } else { 0 })
