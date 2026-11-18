<#
    影之刃零修改器 · 一键打包

    产出一个「双击即用」的单文件 exe：
      · 自带 .NET 8 运行时（self-contained），对方机器不用装 .NET
      · 内嵌运行模块 PBZBridge.dll，不用额外放文件

    任何一项自检不过，脚本以退出码 1 结束，不留半成品包。

    用法（在仓库根目录）：
      powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1 -NoBridge      # 只出界面预览包（CI 用）
      powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1 -SidecarBridge # 额外把运行模块放到 exe 旁边
      powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1 -Version 1.11.17  # 版本号取自 tag（CI 用）
      powershell -NoProfile -ExecutionPolicy Bypass -File pack.ps1 -DotnetPath "D:\dotnet\dotnet.exe"
#>
[CmdletBinding()]
param(
    [string]$OutRoot = 'out',
    [switch]$NoBridge,
    [switch]$SidecarBridge,
    [switch]$NoZip,
    [string]$Version = '',
    [string]$DotnetPath = '',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Repo = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$script:OutRootAbs = if ([IO.Path]::IsPathRooted($OutRoot)) { $OutRoot } else { Join-Path $script:Repo $OutRoot }
$script:CiHome = Join-Path $script:OutRootAbs '.build-cli-home'

function Say([string]$Text) { Write-Host $Text }
function Step([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Ok([string]$Text) { Write-Host "   [OK]   $Text" -ForegroundColor Green }
function Warn([string]$Text) { Write-Host "   [WARN] $Text" -ForegroundColor Yellow }
function Info([string]$Text) { Write-Host "          $Text" -ForegroundColor DarkGray }

function Get-Sha256Hex([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $fs = [IO.File]::OpenRead($Path)
        try { return (-join ($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') })) }
        finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} KB' -f ($Bytes / 1KB))
}

# ---------- .NET SDK ----------

function Resolve-Dotnet {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($DotnetPath) { $candidates.Add($DotnetPath) }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    $candidates.Add((Join-Path $script:OutRootAbs '.dotnet\dotnet.exe'))
    $candidates.Add((Join-Path $script:Repo 'out\.dotnet\dotnet.exe'))
    $candidates.Add((Join-Path $script:Repo '.dotnet\dotnet.exe'))
    $candidates.Add((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))
    $candidates.Add('C:\Program Files\dotnet\dotnet.exe')
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

function Assert-Sdk8([string]$Dotnet) {
    $sdks = & $Dotnet --list-sdks 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks 失败（退出码 $LASTEXITCODE）：$sdks" }
    $has8 = @($sdks | Where-Object { $_ -match '^8\.\d' }).Count -gt 0
    if (-not $has8) { throw "没找到 .NET 8 SDK，当前已装：`n$($sdks -join [Environment]::NewLine)" }
    return ($sdks | Where-Object { $_ -match '^8\.\d' } | Select-Object -Last 1)
}

# ---------- 单文件 bundle 解析 ----------

function Read-SevenBitInt([byte[]]$Bytes, [ref]$Pos) {
    $result = 0; $shift = 0
    while ($true) {
        if ($Pos.Value -ge $Bytes.Length) { throw '7bit eof' }
        $b = $Bytes[$Pos.Value]; $Pos.Value++
        $result = $result -bor (($b -band 0x7F) -shl $shift)
        if (($b -band 0x80) -eq 0) { break }
        $shift += 7
        if ($shift -gt 28) { throw '7bit overflow' }
    }
    return $result
}

function Read-BundleAt([byte[]]$Bytes, [int]$Start) {
    $len = $Bytes.Length
    $p = $Start
    if ($p + 12 -gt $len) { throw 'short header' }
    $major = [BitConverter]::ToInt32($Bytes, $p); $p += 4
    $minor = [BitConverter]::ToInt32($Bytes, $p); $p += 4
    $count = [BitConverter]::ToInt32($Bytes, $p); $p += 4
    if ($count -lt 1 -or $count -gt 5000) { throw 'bad count' }
    $idLen = Read-SevenBitInt $Bytes ([ref]$p)
    if ($idLen -le 0 -or $idLen -gt 200 -or ($p + $idLen) -gt $len) { throw 'bad id' }
    $bundleId = [Text.Encoding]::UTF8.GetString($Bytes, $p, $idLen); $p += $idLen
    if ($p + 40 -gt $len) { throw 'short header' }
    $depsOffset = [BitConverter]::ToInt64($Bytes, $p); $p += 8
    $depsSize = [BitConverter]::ToInt64($Bytes, $p); $p += 8
    $rcOffset = [BitConverter]::ToInt64($Bytes, $p); $p += 8
    $rcSize = [BitConverter]::ToInt64($Bytes, $p); $p += 8
    $flags = [BitConverter]::ToUInt64($Bytes, $p); $p += 8

    $files = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $count; $i++) {
        if ($p + 25 -gt $len) { throw 'short entry' }
        $off = [BitConverter]::ToInt64($Bytes, $p); $p += 8
        $size = [BitConverter]::ToInt64($Bytes, $p); $p += 8
        $csize = [BitConverter]::ToInt64($Bytes, $p); $p += 8
        $type = [int]$Bytes[$p]; $p += 1
        $plen = Read-SevenBitInt $Bytes ([ref]$p)
        if ($plen -le 0 -or $plen -gt 512 -or ($p + $plen) -gt $len) { throw 'bad path' }
        $path = [Text.Encoding]::UTF8.GetString($Bytes, $p, $plen); $p += $plen
        if ($off -lt 0 -or $size -lt 0 -or $csize -lt 0 -or ($off + $csize) -gt $Start) { throw 'entry out of range' }
        $files.Add([pscustomobject]@{ Offset = $off; Size = $size; CompressedSize = $csize; Type = $type; Path = $path })
    }
    if ($p -ne $len) { throw 'header does not end at EOF' }

    return [pscustomobject]@{
        HeaderStart = $Start; Major = $major; Minor = $minor; Count = $count
        BundleId = $bundleId; Flags = $flags
        DepsOffset = $depsOffset; DepsSize = $depsSize
        RuntimeConfigOffset = $rcOffset; RuntimeConfigSize = $rcSize
        Files = $files
    }
}

function Find-Bundle([byte[]]$Bytes) {
    $len = $Bytes.Length
    $lower = [Math]::Max(0, $len - 200000)
    for ($i = $lower; $i -lt ($len - 32); $i++) {
        $major = [BitConverter]::ToInt32($Bytes, $i)
        if ($major -lt 1 -or $major -gt 10) { continue }
        if ([BitConverter]::ToInt32($Bytes, $i + 4) -ne 0) { continue }
        try { return (Read-BundleAt $Bytes $i) } catch { }
    }
    return $null
}

function Get-BundleEntryBytes([byte[]]$Bytes, $Entry) {
    $cs = [int]$Entry.CompressedSize
    $raw = New-Object byte[] $cs
    [Array]::Copy($Bytes, [int]$Entry.Offset, $raw, 0, $cs)
    if ($Entry.CompressedSize -eq $Entry.Size) { return , $raw }
    $in = New-Object IO.MemoryStream(, $raw)
    try {
        $ds = New-Object IO.Compression.DeflateStream($in, [IO.Compression.CompressionMode]::Decompress)
        try {
            $ms = New-Object IO.MemoryStream
            try { $ds.CopyTo($ms); return , $ms.ToArray() }
            finally { $ms.Dispose() }
        }
        finally { $ds.Dispose() }
    }
    finally { $in.Dispose() }
}

# 在宿主程序集里按字节找内嵌的运行模块：命中且整段 SHA-256 一致才算内嵌成功。
function Find-EmbeddedPiece([byte[]]$HostBytes, [string]$PiecePath) {
    $pieceSha = Get-Sha256Hex $PiecePath
    $piece = [IO.File]::ReadAllBytes($PiecePath)
    $enc = [Text.Encoding]::GetEncoding(28591)
    $haystack = $enc.GetString($HostBytes)
    $needleLen = [Math]::Min(64, $piece.Length)
    $needle = $enc.GetString($piece, 0, $needleLen)

    $pos = 0
    while ($pos -lt $haystack.Length) {
        $idx = $haystack.IndexOf($needle, $pos, [StringComparison]::Ordinal)
        if ($idx -lt 0) { break }
        if (($idx + $piece.Length) -le $HostBytes.Length) {
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $hex = -join ($sha.ComputeHash($HostBytes, $idx, $piece.Length) | ForEach-Object { $_.ToString('x2') })
            }
            finally { $sha.Dispose() }
            if ($hex -eq $pieceSha) { return [pscustomobject]@{ Offset = $idx; Sha256 = $hex; Length = $piece.Length } }
        }
        $pos = $idx + 1
    }
    return $null
}

# ---------- 主流程 ----------

function Invoke-Pack {
    Step '1/6 环境'
    Say "   仓库      $script:Repo"
    if (-not (Test-Path -LiteralPath $script:OutRootAbs)) { New-Item -ItemType Directory -Force -Path $script:OutRootAbs | Out-Null }

    $csproj = Join-Path $script:Repo 'src\PBZTrainer\PBZTrainer.csproj'
    if (-not (Test-Path -LiteralPath $csproj)) { throw "找不到工程文件：$csproj" }

    # PowerShell 变量名不区分大小写：先把 -Version 参数读走，再用 effectiveVersion 干活
    $versionFromArg = -not [string]::IsNullOrWhiteSpace($Version)
    $csprojVersion = (Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1).Matches[0].Groups[1].Value
    if (-not $csprojVersion) { throw '读不到工程里的 <Version>' }
    $effectiveVersion = if ($versionFromArg) { $Version.Trim().TrimStart('v', 'V') } else { $csprojVersion }
    if ($effectiveVersion -notmatch '^\d+\.\d+\.\d+') { throw "版本号格式不对：'$effectiveVersion'（要 1.11.17 这种）" }
    Say "   版本      $effectiveVersion$(if ($versionFromArg) { "（来自 -Version；工程里是 $csprojVersion）" } else { '（来自工程）' })"

    $head = ''
    try { $head = (& git -C $script:Repo rev-parse --short HEAD 2>$null) } catch { }
    if ($head) {
        $dirty = ''
        try { $dirty = (& git -C $script:Repo status --porcelain 2>$null | Measure-Object).Count } catch { }
        Say "   提交      $head$(if ($dirty -and [int]$dirty -gt 0) { "（有 $dirty 个未提交改动）" })"
    }

    $dotnet = Resolve-Dotnet
    if (-not $dotnet) {
        throw @'
没找到 dotnet。装一个 .NET 8 SDK 后重跑，或用 -DotnetPath 指定：
  winget install Microsoft.DotNet.SDK.8
  # 或免安装/免管理员：把 SDK 解到 out\.dotnet，脚本会自动识别
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile out\dotnet-install.ps1
  powershell -NoProfile -File out\dotnet-install.ps1 -Channel 8.0 -InstallDir out\.dotnet -NoPath
'@
    }
    $sdk = Assert-Sdk8 $dotnet
    Say "   dotnet    $dotnet"
    Say "   SDK       $sdk"

    Step '2/6 运行模块'
    $bridge = Join-Path $script:Repo 'src\PBZTrainer\Data\PBZBridge.dll'
    $embedBridge = -not $NoBridge
    $bridgeSha = ''
    if ($embedBridge) {
        if (-not (Test-Path -LiteralPath $bridge)) {
            throw "要内嵌运行模块，但没找到：$bridge`n把 PBZBridge.dll 放到 src\PBZTrainer\Data\ 再打包；只出界面预览包用 -NoBridge。"
        }
        $bridgeSha = Get-Sha256Hex $bridge
        $bridgeLen = (Get-Item -LiteralPath $bridge).Length
        Say "   文件      $bridge"
        Say "   大小      $bridgeLen 字节   $([char]0x00B7)   SHA-256 $bridgeSha"
        Ok '将内嵌进 exe'
    }
    else {
        Warn '-NoBridge：产物不含运行模块，只能浏览界面，连不上游戏'
    }

    Step '3/6 发布（self-contained 单文件）'
    $publishDir = Join-Path $script:OutRootAbs 'publish'
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    # 让 dotnet 的首次运行标记 / 遥测落在 out\ 里，不污染用户目录
    $env:DOTNET_CLI_HOME = $script:CiHome
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    $publishArgs = @(
        'publish', $csproj
        '-c', $Configuration
        '-r', $Runtime
        '--self-contained', 'true'
        '-p:PublishSingleFile=true'
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:EnableCompressionInSingleFile=true'
        '-p:DebugType=none'
        '-p:DebugSymbols=false'
        "-p:EmbedBridge=$($embedBridge.ToString().ToLowerInvariant())"
        "-p:Version=$effectiveVersion"
        '--nologo'
        '-o', $publishDir
    )
    Say "   dotnet $($publishArgs -join ' ')"
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    Step '4/6 产物自检'
    $exePath = Join-Path $publishDir 'PBZTrainer.exe'
    if (-not (Test-Path -LiteralPath $exePath)) { throw "发布目录里没有 PBZTrainer.exe：$publishDir" }

    # 4.1 必须只有 exe 一个文件：多出 dll/json 说明是免安装但不自带运行时的包
    $loose = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse | Where-Object { $_.FullName -ne $exePath })
    $runtimeLeak = @($loose | Where-Object { $_.Extension -in '.dll', '.json', '.pdb', '.exe' })
    if ($runtimeLeak.Count -gt 0) {
        throw "发布目录里多出 $(($runtimeLeak | ForEach-Object { $_.Name }) -join ', ') —— 这不是自带运行时的单文件包"
    }
    if ($loose.Count -gt 0) { Warn "发布目录里另有：$(($loose | ForEach-Object { $_.Name }) -join ', ')" }

    $exeInfo = Get-Item -LiteralPath $exePath
    $exeSha = Get-Sha256Hex $exePath
    $exeBytes = [IO.File]::ReadAllBytes($exePath)
    Ok "exe $($exeInfo.Length) 字节（$(Format-Size $exeInfo.Length)）"
    Info "SHA-256 $exeSha"

    # 4.2 bundle 头必须能解析
    $bundle = Find-Bundle $exeBytes
    if (-not $bundle) { throw 'exe 里找不到单文件 bundle 头：这不是 PublishSingleFile 产物' }
    Ok "bundle v$($bundle.Major).$($bundle.Minor)，$($bundle.Count) 个条目"

    # 4.3 运行时必须真的在包里（.NET 运行时缺失＝对方机器会提示缺少运行依赖）
    $needEntries = @('System.Private.CoreLib.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll', 'vcruntime140_cor3.dll')
    foreach ($name in $needEntries) {
        $hit = $bundle.Files | Where-Object { $_.Path -eq $name } | Select-Object -First 1
        if (-not $hit) { throw "包里缺 $name：这个 exe 不是自带运行时版本" }
    }
    if (-not ($bundle.Files | Where-Object { $_.Type -eq 3 })) { throw '包里缺 deps 条目：不是 self-contained' }
    if (-not ($bundle.Files | Where-Object { $_.Type -eq 4 })) { throw '包里缺 runtimeconfig 条目：不是 self-contained' }
    Ok '自带 .NET 8 运行时（含 WPF 原生库与 VC 运行库变体）'

    # 4.4 运行模块必须真的内嵌（缺它就是「这个程序里没有内置运行模块」那个弹窗）
    $appEntry = $bundle.Files | Where-Object { $_.Path -match '(^|/)PBZTrainer\.dll$' } | Select-Object -First 1
    if (-not $appEntry) { throw '包里找不到 PBZTrainer.dll' }
    $appBytes = Get-BundleEntryBytes $exeBytes $appEntry
    if ($appBytes.Length -ne $appEntry.Size) { throw "解压 PBZTrainer.dll 长度不符：$($appBytes.Length) / $($appEntry.Size)" }

    if ($embedBridge) {
        $found = Find-EmbeddedPiece $appBytes $bridge
        if (-not $found) { throw '运行模块没有真正内嵌进 PBZTrainer.dll（打包参数或工程配置被改过）' }
        Ok "内嵌运行模块：$($found.Length) 字节，偏移 0x$($found.Offset.ToString('X')), SHA-256 一致"
    }
    else {
        if (Test-Path -LiteralPath $bridge) {
            $leak = Find-EmbeddedPiece $appBytes $bridge
            if ($leak) { Warn '这个包意外带了运行模块' } else { Ok '确认不含运行模块（预览包）' }
        }
        else {
            Ok '确认不含运行模块（仓库里没有 PBZBridge.dll，预览包）'
        }
    }

    Step '5/6 组装发布目录'
    $releaseDir = Join-Path $script:OutRootAbs 'release'
    if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

    Copy-Item -LiteralPath $exePath -Destination (Join-Path $releaseDir 'PBZTrainer.exe') -Force
    $docs = @(
        @{ From = 'docs\USAGE.txt'; To = '使用说明.txt' },
        @{ From = 'docs\FIRST-RUN.txt'; To = '首次使用.txt' },
        @{ From = 'LICENSE'; To = 'LICENSE' },
        @{ From = 'NOTICE.md'; To = 'NOTICE.md' }
    )
    foreach ($d in $docs) {
        $src = Join-Path $script:Repo $d.From
        if (-not (Test-Path -LiteralPath $src)) { throw "发布目录缺少来源文件：$($d.From)" }
        Copy-Item -LiteralPath $src -Destination (Join-Path $releaseDir $d.To) -Force
    }
    if ($embedBridge -and $SidecarBridge) {
        Copy-Item -LiteralPath $bridge -Destination (Join-Path $releaseDir 'PBZBridge.dll') -Force
        Ok '另放了 PBZBridge.dll 到 exe 旁边（备用路径）'
    }

    $sumLines = foreach ($f in (Get-ChildItem -LiteralPath $releaseDir -File | Sort-Object Name)) {
        '{0}  {1}' -f (Get-Sha256Hex $f.FullName), $f.Name
    }
    $sumPath = Join-Path $releaseDir 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($sumPath, (($sumLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    Ok "发布目录 $releaseDir（$((Get-ChildItem -LiteralPath $releaseDir -File).Count) 个文件）"

    # 兼容 README 里写的 out\PBZTrainer.exe
    Copy-Item -LiteralPath $exePath -Destination (Join-Path $script:OutRootAbs 'PBZTrainer.exe') -Force

    Step '6/6 打 zip'
    # 文件名不带版本号：README 用 /releases/latest/download/ 指向它，发版不用改文档。
    # 版本号在 tag、Release 标题和正文里；exe 里的 FileVersion 也带着。
    $zipName = if ($embedBridge) { 'PBZTrainer-win-x64.zip' } else { 'PBZTrainer-UI-preview-win-x64.zip' }
    $zipPath = Join-Path $script:OutRootAbs $zipName
    if ($NoZip) {
        Warn '-NoZip：跳过压缩'
    }
    else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($releaseDir, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

        $zipSha = Get-Sha256Hex $zipPath
        $zipLen = (Get-Item -LiteralPath $zipPath).Length
        [IO.File]::WriteAllText((Join-Path $script:OutRootAbs 'SHA256SUMS.txt'), (('{0}  {1}' -f $zipSha, $zipName) + "`n"),
            (New-Object Text.UTF8Encoding($false)))
        Ok "$zipName  $(Format-Size $zipLen)"
        Info "SHA-256 $zipSha"
    }

    Say ''
    Say '------------------------------ 完成 ------------------------------'
    Say "  exe      $(Join-Path $script:OutRootAbs 'PBZTrainer.exe')"
    Say "  发布目录 $releaseDir"
    if (-not $NoZip) { Say "  zip      $zipPath" }
    if ($embedBridge) {
        Say ''
        Say '  自检：自带 .NET 运行时 = 是   内嵌运行模块 = 是（SHA-256 一致）'
        Say '  对方机器双击 exe 即可用，不需要装 .NET、不需要额外文件。'
    }
    else {
        Say ''
        Say '  自检：自带 .NET 运行时 = 是   内嵌运行模块 = 无（预览包）'
        Say '  这个包只能浏览界面；要能连游戏请去掉 -NoBridge 重新打包。'
    }
    Say '------------------------------------------------------------------'
}

try {
    Invoke-Pack
    exit 0
}
catch {
    Write-Host ''
    Write-Host '打包失败' -ForegroundColor Red
    Write-Host ("  {0}" -f $_.Exception.Message) -ForegroundColor Red
    if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) {
        Write-Host ("  位置 pack.ps1:{0}" -f $_.InvocationInfo.ScriptLineNumber) -ForegroundColor DarkGray
    }
    exit 1
}
