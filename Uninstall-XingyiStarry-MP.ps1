param(
    [ValidateSet('Plugin', 'All')]
    [string]$Mode = '',
    [string]$GameRoot = $PSScriptRoot,
    [switch]$Force,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Wait-BeforeExit {
    if (-not $NoPause -and -not [Console]::IsInputRedirected) {
        Write-Host
        Write-Host '按任意键退出...'
        [void][Console]::ReadKey($true)
    }
}

function Stop-Uninstall([string]$Message, [int]$ExitCode) {
    if (-not [string]::IsNullOrWhiteSpace($Message)) { Write-Host $Message }
    Wait-BeforeExit
    exit $ExitCode
}

$root = [IO.Path]::GetFullPath($GameRoot)
$rootPath = [IO.Path]::GetPathRoot($root)
if ($root.TrimEnd('\') -eq $rootPath.TrimEnd('\')) {
    Stop-Uninstall '[错误] 拒绝在磁盘根目录运行卸载。' 1
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'AnnW.exe') -PathType Leaf)) {
    Stop-Uninstall '[错误] 当前目录不是游戏目录：没有找到 AnnW.exe。请把卸载脚本放到游戏根目录后再运行。' 1
}

$runningGame = Get-Process -Name 'AnnW' -ErrorAction SilentlyContinue
if ($runningGame) {
    Stop-Uninstall '[错误] 检测到游戏仍在运行。请完全退出所有 AnnW 进程后重试。' 1
}

$options = @(
    [pscustomobject]@{ Mode = 'Plugin'; Label = '只卸载 XingyiStarry MP（默认）'; Detail = '保留 BepInEx 和其他 BepInEx 模组。' },
    [pscustomobject]@{ Mode = 'All'; Label = '卸载 XingyiStarry MP + BepInEx'; Detail = '删除整个 BepInEx 框架、全部 BepInEx 模组和配置。' }
)

if ([string]::IsNullOrWhiteSpace($Mode)) {
    $selected = 0
    :menu while ($true) {
        Clear-Host
        Write-Host 'XingyiStarry MP 卸载程序'
        Write-Host '=========================='
        Write-Host
        Write-Host '请使用 ↑/↓ 方向键选择，按 Enter 确认；也可以直接按数字键 1 或 2。'
        Write-Host '按 Esc 取消。'
        Write-Host
        for ($index = 0; $index -lt $options.Count; $index++) {
            $prefix = if ($index -eq $selected) { '>' } else { ' ' }
            Write-Host ("{0} {1}. {2}" -f $prefix, ($index + 1), $options[$index].Label)
            Write-Host ("     {0}" -f $options[$index].Detail) -ForegroundColor DarkGray
        }

        $key = [Console]::ReadKey($true).Key
        switch ($key) {
            'UpArrow' { $selected = ($selected - 1 + $options.Count) % $options.Count }
            'DownArrow' { $selected = ($selected + 1) % $options.Count }
            'D1' { $selected = 0; break menu }
            'NumPad1' { $selected = 0; break menu }
            'D2' { $selected = 1; break menu }
            'NumPad2' { $selected = 1; break menu }
            'Enter' { break menu }
            'Escape' { Stop-Uninstall '已取消卸载。' 2 }
        }
    }
    $Mode = $options[$selected].Mode
}

$selectedOption = $options | Where-Object Mode -eq $Mode | Select-Object -First 1
Write-Host
Write-Host ("已选择：{0}" -f $selectedOption.Label)
if ($Mode -eq 'All') {
    Write-Host '[警告] 此操作会删除整个 BepInEx 目录，包括其他模组、配置、缓存和日志。' -ForegroundColor Yellow
}

if (-not $Force) {
    Write-Host '确认继续？按 Y 确认，按 N 或 Esc 取消。'
    while ($true) {
        $confirmKey = [Console]::ReadKey($true).Key
        if ($confirmKey -eq 'Y') { break }
        if ($confirmKey -in @('N', 'Escape')) { Stop-Uninstall '已取消卸载。' 2 }
    }
}

$removedCount = 0
function Remove-OwnedPath([string]$RelativePath) {
    $target = Join-Path $root $RelativePath
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
        $script:removedCount++
        Write-Host ("  已删除：{0}" -f $RelativePath)
    }
}

try {
    Write-Host
    Write-Host '正在卸载...'

    if ($Mode -eq 'All') {
        Remove-OwnedPath 'BepInEx'
        foreach ($relativePath in @(
            '.doorstop_version',
            'doorstop_config.ini',
            'winhttp.dll',
            'changelog.txt'
        )) {
            Remove-OwnedPath $relativePath
        }
    } else {
        foreach ($relativePath in @(
            'BepInEx\plugins\XingyiStarry.Mp',
            'BepInEx\plugins\XingyiStarry.Mp.DebugTools',
            'BepInEx\patchers\XingyiStarry.Mp.EarlyPatcher.dll',
            'BepInEx\config\xingyistarry.mp.cfg'
        )) {
            Remove-OwnedPath $relativePath
        }
    }

    foreach ($relativePath in @(
        'XingyiStarry.Mp.NoSteam',
        'XingyiStarry.Mp-README.txt',
        'XingyiStarry.Mp-LICENSE.txt',
        'XingyiStarry.Mp-THIRD-PARTY-NOTICES.md',
        'XingyiStarry.Mp-Licenses',
        'Uninstall-XingyiStarry-MP.bat',
        'Uninstall-XingyiStarry-MP.ps1'
    )) {
        Remove-OwnedPath $relativePath
    }
} catch {
    Write-Host
    Write-Host ("[错误] 卸载未完成：{0}" -f $_.Exception.Message) -ForegroundColor Red
    Wait-BeforeExit
    exit 1
}

Write-Host
if ($removedCount -eq 0) {
    Write-Host '[完成] 没有找到需要删除的已安装文件。'
} elseif ($Mode -eq 'All') {
    Write-Host '[完成] XingyiStarry MP 和 BepInEx 已卸载。'
} else {
    Write-Host '[完成] XingyiStarry MP 已卸载，BepInEx 已保留。'
}
Wait-BeforeExit
exit 0
