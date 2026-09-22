<#
.SYNOPSIS
  Windows 端到端验证：确认这台机器能不能把游戏的技术脊椎跑起来，并留下完整日志。

.DESCRIPTION
  依次检查并运行：
    1. 环境信息（系统、CPU、内存）
    2. .NET SDK + Core 测试（dotnet test，含真实起 QEMU 的集成测试）
    3. QEMU：能否启动、是否具备伪装所需的 IvyBridge / e1000e / ich9-ahci
    4. 客户机镜像是否就位
    5. godot-xterm 插件（缺则自动下载，-Offline 时跳过）
    6. Godot 无头自检（SelfTest：键盘 -> 客户机 -> 渲染，外加伪装断言）
    7. 退出后有没有残留的 qemu 进程
  所有输出落到 m0\run\verify-<时间戳>\，最后打成同名 zip，把 zip 发回来即可。

.PARAMETER Godot
  Godot mono 版可执行文件路径。不给则依次找 $env:GODOT、PATH、常见安装位置。
  优先用 *_console.exe —— 非 console 版在 Windows 上不往 stdout 写日志。

.PARAMETER Qemu
  qemu-system-x86_64.exe 路径。不给则依次找 $env:GAMEHACKER_QEMU、
  runtime\windows-x86_64\bin\、C:\Program Files\qemu\、PATH。

.PARAMETER Windowed
  额外跑一轮带窗口的，并截图（无头模式的渲染器是空的，截不出东西）。

.PARAMETER Offline
  不联网：插件缺失时直接报错，不去 GitHub 下载。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-windows.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-windows.ps1 -Godot D:\godot\Godot_v4.7.2-stable_mono_win64_console.exe -Windowed
#>
[CmdletBinding()]
param(
    [string]$Godot,
    [string]$Qemu,
    [switch]$Windowed,
    [switch]$Offline,
    [int]$BootTimeoutSeconds = 180
)

# 注意：本脚本刻意只用 Windows PowerShell 5.1 就有的语法（不用 && ?? 三元运算符），
# 因为那是 Windows 自带的版本；文件必须存成带 BOM 的 UTF-8，否则 5.1 会按
# 本地代码页读、中文全乱。

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 2

$Repo = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..'))
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Out = [System.IO.Path]::Combine($Repo, 'm0', 'run', "verify-$Stamp")
New-Item -ItemType Directory -Force -Path $Out | Out-Null
Start-Transcript -Path (Join-Path $Out 'transcript.txt') | Out-Null

$Results = New-Object System.Collections.ArrayList

function Step([string]$Name, [bool]$Ok, [string]$Detail = '') {
    [void]$Results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    if ($Ok) { $tag = 'PASS'; $color = 'Green' } else { $tag = 'FAIL'; $color = 'Red' }
    Write-Host ("[{0}] {1}  {2}" -f $tag, $Name, $Detail) -ForegroundColor $color
}

function Section([string]$Title) {
    Write-Host ''
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

<# 带超时地跑一个外部程序，stdout/stderr 各落一个文件。
   返回退出码；超时 -1；启动失败 -2；stderr 命中 FailPattern 提前终止 -3。 #>
function Invoke-Logged([string]$Exe, [string[]]$Arguments, [string]$LogName,
                       [int]$TimeoutSeconds = 600, [string]$WorkDir = $Repo,
                       [string]$FailPattern = '') {
    $stdout = Join-Path $Out "$LogName.out.txt"
    $stderr = Join-Path $Out "$LogName.err.txt"
    # Start-Process 的 -ArgumentList 在 5.1 下不会替你加引号，带空格的参数要自己包
    $quoted = $Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList $quoted -WorkingDirectory $WorkDir `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
                -NoNewWindow -PassThru
    } catch {
        Set-Content -Path $stderr -Value $_.Exception.Message
        return -2
    }
    # Windows PowerShell 5.1 的坑：Start-Process -PassThru 拿到的 Process 对象
    # 如果没在进程退出前碰过 Handle，事后 ExitCode 永远是空的 ——
    # 第一轮 Windows 报告里所有"退出码"后面都是空白，就是这个原因。
    $null = $p.Handle
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.HasExited) {
        if ((Get-Date) -gt $deadline) {
            try { $p.Kill() } catch { }
            return -1
        }
        # 已知的致命错误出现在日志里就不必等到超时：比如 C# 没编译出来时，
        # Godot 找不到 Main 类，场景里没有任何代码会调 Quit，只会空转到超时。
        if ($FailPattern -and (Test-Path $stderr) -and
            (Select-String -Path $stderr -Pattern $FailPattern -Quiet)) {
            try { $p.Kill() } catch { }
            return -3
        }
        Start-Sleep -Milliseconds 500
    }
    $p.WaitForExit()
    return $p.ExitCode
}

function Tail([string]$Path, [int]$Lines = 15) {
    # dotnet / Godot 往重定向文件里写的是 UTF-8，5.1 默认按本地代码页读会乱码
    if (Test-Path $Path) { (Get-Content $Path -Tail $Lines -Encoding UTF8) -join "`n" } else { '' }
}

# ---------------------------------------------------------------------------
Section '1. 环境'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$envInfo = @(
    "系统:     $($os.Caption) $($os.Version) ($env:PROCESSOR_ARCHITECTURE)"
    "CPU:      $($cpu.Name)  逻辑核 $($cpu.NumberOfLogicalProcessors)"
    "内存:     {0:N1} GB" -f ($os.TotalVisibleMemorySize / 1MB)
    "PS:       $($PSVersionTable.PSVersion)"
    "仓库:     $Repo"
) -join "`n"
Write-Host $envInfo
Set-Content -Path (Join-Path $Out 'environment.txt') -Value $envInfo -Encoding UTF8
Step '环境信息已记录' $true
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    Write-Host '  注意：非 x86_64 主机，客户机是 x86_64，只能纯 TCG 且会明显变慢' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
Section '2. .NET 与 Core 测试'
# 常见误会：装了".NET 10"但装的是运行时（Runtime / Desktop Runtime）而不是 SDK；
# .NET Framework 4.8.x 是另一套东西，和这里无关。
# 另一个常见坑：32 位和 64 位 dotnet 是两套独立安装，SDK 可能只装在其中一套里。
# 32 位 SDK 能编译、能跑测试，但 64 位 Godot 编辑器打开工程时要加载 SDK 里的
# Microsoft.Build.dll（ReadyToRun 编译，带位数），会报 BadImageFormatException /
# Could not load 'Microsoft.Build(.Framework), Version=15.1.0.0'。
# 所以把每一份 dotnet.exe 都列出来，并且优先选 64 位的。
$dotnet = $null
$dotnetReport = New-Object System.Collections.ArrayList
$x86Dir = "${env:ProgramFiles(x86)}\dotnet"
$candidates = @("$env:ProgramFiles\dotnet\dotnet.exe")
$where = & where.exe dotnet 2>$null
if ($where) { $candidates += $where }
$candidates += @("$x86Dir\dotnet.exe")
$dotnetX86 = $null
foreach ($c in ($candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
    $sdkList = @(& $c --list-sdks 2>$null | Where-Object { $_ })
    $rtList = @(& $c --list-runtimes 2>$null | Where-Object { $_ })
    if ($sdkList.Count -gt 0) { $sdkText = $sdkList -join '; ' } else { $sdkText = '(无)' }
    if ($rtList.Count -gt 0) { $rtText = $rtList -join '; ' } else { $rtText = '(无)' }
    [void]$dotnetReport.Add("$c")
    [void]$dotnetReport.Add("  SDK:    $sdkText")
    [void]$dotnetReport.Add("  运行时: $rtText")
    if ($sdkList -match '^\s*([89]|\d\d)\.') {
        $is32 = ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') -and
                $c.StartsWith($x86Dir, [System.StringComparison]::OrdinalIgnoreCase)
        if ($is32) { if (-not $dotnetX86) { $dotnetX86 = $c } }
        elseif (-not $dotnet) { $dotnet = $c }
    }
}
# 只有 32 位 SDK 时仍然用它把后面的步骤跑完（编译、测试、自检都不受影响），
# 但单列一项失败：编辑器打不开工程
$sdkIs32 = $false
if (-not $dotnet -and $dotnetX86) { $dotnet = $dotnetX86; $sdkIs32 = $true }
$dotnetText = $dotnetReport -join "`n"
Set-Content -Path (Join-Path $Out 'dotnet-info.txt') -Value $dotnetText -Encoding UTF8
Write-Host $dotnetText
if ($dotnet) {
    Step '.NET SDK' $true $dotnet
    if ($sdkIs32) {
        Step '.NET SDK 是 64 位（Godot 编辑器需要）' $false ('只有 32 位 SDK。命令行编译和自检能过，' +
            '但在 Godot 编辑器里打开工程会报 Microsoft.Build 加载失败。装 x64 版 .NET 10 SDK：' +
            'https://dotnet.microsoft.com/download/dotnet/10.0 —— SDK 栏 Windows 的 "x64"（不是 x86）；' +
            '32 位那份可以在"应用和功能"里卸掉')
    } else {
        Step '.NET SDK 是 64 位（Godot 编辑器需要）' $true
    }
    # 后面所有步骤都用这一份，避免 PATH 先命中那份没有 SDK 的
    $env:PATH = (Split-Path $dotnet) + ';' + $env:PATH
} elseif ($dotnetReport.Count -gt 0) {
    Step '.NET SDK' $false ('找到了 dotnet 但没有 8 或更新版本的 SDK（只有运行时不够，需要 SDK）。' +
        '装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0 —— 选 "SDK" 那一栏')
} else {
    Step '.NET SDK' $false '找不到 dotnet。装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0'
}

# ---------------------------------------------------------------------------
Section '3. QEMU'
function Resolve-Qemu {
    if ($Qemu) { return $Qemu }
    if ($env:GAMEHACKER_QEMU) { return $env:GAMEHACKER_QEMU }
    $candidates = @(
        [System.IO.Path]::Combine($Repo, 'runtime', 'windows-x86_64', 'bin', 'qemu-system-x86_64.exe'),
        'C:\Program Files\qemu\qemu-system-x86_64.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command qemu-system-x86_64 -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}
$qemuExe = Resolve-Qemu
if (-not $qemuExe -or -not (Test-Path $qemuExe)) {
    Step 'QEMU 可执行文件' $false '找不到。装 https://qemu.weilnetz.de/w64/ 的官方构建，或用 -Qemu 指定'
} else {
    # 游戏侧（GamePaths）只认 runtime\ 下的那份或 PATH 里的名字；
    # 找到别处的就显式交给它，保证测的就是这里检查过的这一份
    $env:GAMEHACKER_QEMU = $qemuExe
    # 先收全输出再取第一行：管道里直接 Select-Object -First 1 会提前掐断，
    # 5.1 下 $LASTEXITCODE 随之不可信 —— 第一轮报告里版本号都打出来了却判 FAIL
    $verOut = @(& $qemuExe -version 2>&1)
    $qemuOk = ($LASTEXITCODE -eq 0)
    $ver = "$($verOut | Select-Object -First 1)"
    Step 'QEMU 可执行文件' $qemuOk "$qemuExe  ($ver)"

    # 伪装需要的三样：Windows 上可能用的是别人的构建，不能假定和 Linux 裁剪版一致
    $cpus = (& $qemuExe -cpu help 2>&1) -join "`n"
    Step 'QEMU 支持 -cpu IvyBridge / Westmere' (($cpus -match 'IvyBridge') -and ($cpus -match 'Westmere'))
    $devs = (& $qemuExe -device help 2>&1) -join "`n"
    Step 'QEMU 支持 e1000e 网卡' ($devs -match '"e1000e"')
    Step 'QEMU 支持 ich9-ahci / ide-hd' (($devs -match '"ich9-ahci"') -and ($devs -match '"ide-hd"'))
    Set-Content -Path (Join-Path $Out 'qemu-capabilities.txt') -Value "$ver`n`n$cpus`n`n$devs" -Encoding UTF8
}

# ---------------------------------------------------------------------------
Section '4. 客户机镜像'
$images = [System.IO.Path]::Combine($Repo, 'm0', 'images')
$missing = @()
foreach ($f in 'vmlinuz-virt', 'm0-guest.cpio.gz', 'alpine-main.qcow2') {
    if (-not (Test-Path (Join-Path $images $f))) { $missing += $f }
}
if ($missing.Count -gt 0) {
    Step '客户机镜像' $false ("缺少 {0}。镜像只能在 Linux 上构建，在 Linux 上跑 scripts/pack-for-windows.sh 后把 zip 解到仓库根" -f ($missing -join ', '))
} else {
    $sizes = Get-ChildItem $images | Where-Object { $_.Name -ne '.gitkeep' } | ForEach-Object { '{0}={1:N1}MB' -f $_.Name, ($_.Length / 1MB) }
    Step '客户机镜像' $true ($sizes -join ' ')
}

# ---------------------------------------------------------------------------
Section '5. Core 测试（含真实 QEMU 集成测试）'
if ($dotnet) {
    $code = Invoke-Logged 'dotnet' @('test', 'GameHacker.slnx', '--nologo', '-v', 'q') 'dotnet-test' 900
    $summary = (Select-String -Path (Join-Path $Out 'dotnet-test.out.txt') -Pattern '通过|Passed|失败|Failed' |
                Select-Object -Last 1)
    if ($summary) { $detail = $summary.Line.Trim() } else { $detail = "退出码 $code" }
    Step 'dotnet test' ($code -eq 0) $detail
} else {
    Step 'dotnet test' $false '跳过：没有 .NET SDK（见第 2 步）'
}

# ---------------------------------------------------------------------------
Section '6. Godot'
function Resolve-Godot {
    if ($Godot) { return $Godot }
    if ($env:GODOT) { return $env:GODOT }
    foreach ($name in 'godot-mono', 'godot') {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    $roots = @("$env:LOCALAPPDATA", "$env:ProgramFiles", "$env:USERPROFILE\Downloads", 'C:\Godot', 'D:\Godot')
    foreach ($r in $roots) {
        if (-not $r -or -not (Test-Path $r)) { continue }
        $hit = Get-ChildItem -Path $r -Recurse -Depth 3 -Filter 'Godot_v4*mono*_console.exe' -ErrorAction SilentlyContinue |
               Sort-Object Name -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}
$godotExe = Resolve-Godot
$project = [System.IO.Path]::Combine($Repo, 'src', 'GameHacker.Godot')

if (-not $godotExe -or -not (Test-Path $godotExe)) {
    Step 'Godot mono' $false '找不到。下载 Godot 4.7 .NET 版，用 -Godot 指向 *_console.exe'
} else {
    $gv = (& $godotExe --version 2>&1 | Select-Object -Last 1)
    # 非 mono 版能打开工程但加载不了任何 .cs 脚本，错误信息很迷惑，这里先拦住
    Step 'Godot mono' ($gv -match 'mono') "$godotExe  ($gv)"
    if ($godotExe -notmatch '_console\.exe$') {
        Write-Host '  注意：不是 *_console.exe，Windows 上可能拿不到 stdout 日志' -ForegroundColor Yellow
    }

    # --- 插件 ---
    $addon = [System.IO.Path]::Combine($project, 'addons', 'godot_xterm')
    if (-not (Test-Path $addon)) {
        if ($Offline) {
            Step 'godot-xterm 插件' $false '缺失且处于 -Offline；把 Linux 上的 addons\godot_xterm 拷过来'
        } else {
            Write-Host '  下载 godot-xterm v4.0.3 …'
            $zip = Join-Path $env:TEMP 'godot-xterm-v4.0.3.zip'
            $extract = Join-Path $env:TEMP "godot-xterm-$Stamp"
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -UseBasicParsing -OutFile $zip `
                    'https://github.com/lihop/godot-xterm/releases/download/v4.0.3/godot-xterm-v4.0.3.zip'
                Expand-Archive -Path $zip -DestinationPath $extract -Force
                $src = Get-ChildItem $extract -Recurse -Directory -Filter 'godot_xterm' | Select-Object -First 1
                New-Item -ItemType Directory -Force -Path (Join-Path $project 'addons') | Out-Null
                Copy-Item -Recurse -Path $src.FullName -Destination (Join-Path $project 'addons')
            } catch {
                Write-Host "  下载失败: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
    $dll = [System.IO.Path]::Combine($addon, 'lib', 'libgodot-xterm.windows.template_debug.x86_64.dll')
    Step 'godot-xterm 插件（Windows x86_64 GDExtension）' (Test-Path $dll) $dll

    # --- 编译 C# 并导入资源 ---
    # 第一次打开工程必须先 --import，否则 GDExtension 还没被扫描，Terminal 节点类型不存在
    if ($dotnet) {
        $code = Invoke-Logged 'dotnet' @('build', (Join-Path $project 'GameHacker.Godot.csproj'), '--nologo', '-v', 'q') 'godot-build' 300
        Step 'Godot 工程 C# 编译' ($code -eq 0) (Tail (Join-Path $Out 'godot-build.out.txt') 2)
    } else {
        Step 'Godot 工程 C# 编译' $false '跳过：没有 .NET SDK（见第 2 步）'
    }
    # 没有 .NET 时 Godot mono 连 --import 都会卡住（报 hostfxr 找不到然后不退出），
    # 在 Mac 上实测白等满 300 秒；有 SDK 时也挂上同样的失败模式兜底
    if (-not $dotnet) {
        Step 'Godot 资源导入' $false '跳过：没有 .NET SDK，Godot mono 找不到 .NET 运行时会卡住'
    } else {
        $code = Invoke-Logged $godotExe @('--headless', '--path', $project, '--import') 'godot-import' 300 `
                    -FailPattern 'Failed to load .NET runtime|hostfxr'
        if ($code -eq -3) {
            Step 'Godot 资源导入' $false 'Godot 找不到 .NET 运行时（hostfxr）'
        } else {
            Step 'Godot 资源导入' ($code -eq 0) "退出码 $code"
        }
        # --import 会走一遍编辑器插件初始化，GodotTools 在这里用 MSBuild 打开 csproj。
        # 这一步出错不影响退出码，但玩家双击打开编辑器时就是这个报错
        $importErr = Join-Path $Out 'godot-import.err.txt'
        $msb = $null
        if (Test-Path $importErr) {
            $msb = Select-String -Path $importErr -Pattern 'Microsoft\.Build|MSBuildLocator' -Encoding UTF8 |
                   Select-Object -First 1
        }
        if ($msb) {
            Step 'Godot 编辑器能加载 MSBuild' $false ("$($msb.Line.Trim())" +
                ' —— 多半是 SDK 位数不对，见 .NET SDK 那一项')
        } else {
            Step 'Godot 编辑器能加载 MSBuild' $true
        }
    }

    # --- 自检 ---
    $report = Join-Path $Out 'selftest.txt'
    $env:GAMEHACKER_BOOT_TIMEOUT = "$BootTimeoutSeconds"
    $env:GAMEHACKER_SELFTEST = $report
    $env:GAMEHACKER_PCAP = Join-Path $Out 'capture.pcap'
    $t0 = Get-Date
    if ($dotnet) {
        # C# 没编译出来时 Godot 找不到 Main 类、没有代码会调 Quit，只会空转到超时；
        # 看见这类错误就提前收掉
        $code = Invoke-Logged $godotExe @('--headless', '--path', $project) 'godot-selftest' ($BootTimeoutSeconds + 180) `
                    -FailPattern 'Cannot instantiate C# script|Failed to load .NET runtime|hostfxr'
    } else {
        $code = -4
    }
    $elapsed = [int]((Get-Date) - $t0).TotalSeconds
    if ($code -eq -4) {
        Step 'Godot 自检' $false '跳过：没有 .NET SDK，C# 脚本编译不出来（见第 2 步）'
    } elseif (Test-Path $report) {
        $lines = @(Get-Content $report)
        $pass = @($lines | Where-Object { $_ -like 'PASS*' }).Count
        Step "Godot 自检 (${elapsed}s)" (($code -eq 0) -and ($pass -eq $lines.Count)) "$pass/$($lines.Count) 项通过，退出码 $code"
        foreach ($l in $lines) { Write-Host "    $l" }
    } else {
        if ($code -eq -3) {
            $why = 'Godot 加载不了 C# 脚本（C# 没编译出来或 .NET 运行时加载失败）'
        } elseif ($code -eq -1) {
            $why = '超时'
        } else {
            $why = "退出码 $code"
        }
        Step "Godot 自检 (${elapsed}s)" $false "没有生成报告：$why。见 godot-selftest.*.txt"
        Write-Host (Tail (Join-Path $Out 'godot-selftest.out.txt') 25)
    }

    # --- 可选：带窗口截图 ---
    if ($Windowed) {
        Remove-Item Env:GAMEHACKER_SELFTEST -ErrorAction SilentlyContinue
        $env:GAMEHACKER_PCAP = Join-Path $Out 'capture-windowed.pcap'
        $env:GAMEHACKER_SCREENSHOT = Join-Path $Out 'screenshot.png'
        $code = Invoke-Logged $godotExe @('--path', $project) 'godot-windowed' ($BootTimeoutSeconds + 120)
        Step '带窗口运行并截图' (Test-Path $env:GAMEHACKER_SCREENSHOT) "退出码 $code"
        Remove-Item Env:GAMEHACKER_SCREENSHOT -ErrorAction SilentlyContinue
    }
    Remove-Item Env:GAMEHACKER_SELFTEST, Env:GAMEHACKER_PCAP -ErrorAction SilentlyContinue

    # godot 自己的日志（user://logs）也一并带走
    $userLogs = Join-Path $env:APPDATA 'Godot\app_userdata\GameHacker\logs'
    if (Test-Path $userLogs) { Copy-Item -Recurse $userLogs (Join-Path $Out 'godot-user-logs') -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------
Section '7. 残留进程'
Start-Sleep -Seconds 2
$orphans = @(Get-Process -Name 'qemu-system*' -ErrorAction SilentlyContinue)
# TCG 每台占满一核，孤儿会让下一次启动越跑越慢直至超时 —— M2 里在 Linux 上实测到过
Step '退出后没有残留 qemu' ($orphans.Count -eq 0) ("{0} 个" -f $orphans.Count)
if ($orphans.Count -gt 0) {
    $orphans | Format-Table Id, ProcessName, StartTime -AutoSize | Out-String | Write-Host
    $orphans | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
Section '汇总'
$failed = @($Results | Where-Object { -not $_.Ok })
$text = ($Results | ForEach-Object {
    if ($_.Ok) { $t = 'PASS' } else { $t = 'FAIL' }
    "$t`t$($_.Name)`t$($_.Detail)"
}) -join "`r`n"
Set-Content -Path (Join-Path $Out 'report.txt') -Value $text -Encoding UTF8

if ($failed.Count -eq 0) {
    Write-Host ("全部 {0} 项通过" -f $Results.Count) -ForegroundColor Green
} else {
    Write-Host ("{0}/{1} 项未通过：" -f $failed.Count, $Results.Count) -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $($_.Name): $($_.Detail)" -ForegroundColor Red }
}

Stop-Transcript | Out-Null
$zipPath = "$Out.zip"
# 不用 Compress-Archive：5.1 的实现用反斜杠做 zip 内路径分隔符，
# 在 Linux/macOS 上解出来会变成 "godot-user-logs\godot.log" 这样的文件名
# （第一轮回传的包就是这样）。
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $base = (Resolve-Path $Out).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -Path $Out -Recurse -File) {
        $entry = $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry)
    }
} finally {
    $archive.Dispose()
}
Write-Host ''
Write-Host "日志目录: $Out"
Write-Host "打包:     $zipPath   <- 把这个发回来" -ForegroundColor Cyan

if ($failed.Count -eq 0) { exit 0 } else { exit 1 }
