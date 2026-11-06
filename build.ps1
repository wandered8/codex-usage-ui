param([string]$Version='1.2.0')
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$build=Join-Path $PSScriptRoot '.build'
$app=Join-Path $build 'app'
$dist=Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $build,$app,$dist -Force | Out-Null
$runtimeVersion='24.22.2'
$archive=Join-Path $build 'node.zip'
if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest "https://nodejs.org/dist/v$runtimeVersion/node-v$runtimeVersion-win-x64.zip" -OutFile $archive}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'CA1BFF5D8EAD13C9F2EA404A176DB46BD430704D5B5113734049FA365E7799E2'){throw 'Node.js 下载校验失败'}
Expand-Archive -LiteralPath $archive -DestinationPath $build -Force
$runtime=Join-Path $build "node-v$runtimeVersion-win-x64"
New-Item -ItemType Directory -Path (Join-Path $app 'runtime') -Force | Out-Null
foreach($file in @('node.exe','LICENSE')){Copy-Item -LiteralPath (Join-Path $runtime $file) -Destination (Join-Path $app 'runtime') -Force}
foreach($file in @('agent.cjs','quota-worker.cjs','usage-stats.cjs','usage-icon.ico','Codex用量统计.exe.config')){Copy-Item -LiteralPath (Join-Path 'src' $file) -Destination $app -Force}
foreach($file in @('LICENSE','THIRD_PARTY_NOTICES.md','README.md')){Copy-Item -LiteralPath $file -Destination $app -Force}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework=Split-Path -Parent $compiler
$references=@('System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll',"$framework\WPF\UIAutomationClient.dll","$framework\WPF\UIAutomationTypes.dll","$framework\WPF\WindowsBase.dll") | ForEach-Object {"/reference:$_"}
& $compiler /nologo /codepage:65001 /target:winexe "/out:$app\Codex用量统计.exe" "/win32icon:$PSScriptRoot\src\usage-icon.ico" "/win32manifest:$PSScriptRoot\src\app.manifest" @references "$PSScriptRoot\src\UsageCardManager.cs" "$PSScriptRoot\src\UsageStatsView.cs" "$PSScriptRoot\src\CodexRingOverlay.cs"
if($LASTEXITCODE -ne 0){throw '主程序编译失败'}
Copy-Item -LiteralPath 'src\Codex用量统计.exe.config' -Destination (Join-Path $app 'Uninstall.exe.config') -Force
$setupReferences=@('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:Microsoft.CSharp.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
& $compiler /nologo /codepage:65001 /target:winexe "/out:$app\Uninstall.exe" "/win32icon:$PSScriptRoot\src\usage-icon.ico" "/win32manifest:$PSScriptRoot\src\app.manifest" @setupReferences "$PSScriptRoot\installer\Setup.cs"
if($LASTEXITCODE -ne 0){throw '卸载程序编译失败'}
$payload=Join-Path $build 'payload.zip'
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $payload -Force
$setup=Join-Path $dist "CodexUsageUI-$Version-Setup-x64.exe"
& $compiler /nologo /codepage:65001 /target:winexe "/out:$setup" "/resource:$payload,payload.zip" "/win32icon:$PSScriptRoot\src\usage-icon.ico" "/win32manifest:$PSScriptRoot\src\app.manifest" @setupReferences "$PSScriptRoot\installer\Setup.cs"
if($LASTEXITCODE -ne 0){throw '安装包编译失败'}
Copy-Item -LiteralPath 'src\Codex用量统计.exe.config' -Destination ($setup+'.config') -Force
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | ForEach-Object {($_.Hash.ToLower()+'  '+[IO.Path]::GetFileName($_.Path))} | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Output "已生成：$setup"
