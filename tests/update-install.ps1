param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference='Stop'
$installer=(Resolve-Path -LiteralPath $InstallerPath).Path
$assembly=[Reflection.Assembly]::LoadFile($installer)
$apply=$assembly.GetType('Setup').GetMethod('ApplyUpdateFiles',[Reflection.BindingFlags]'Static,NonPublic')
$root=Join-Path ([IO.Path]::GetTempPath()) ('codex-usage-update-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    foreach($fail in @($false,$true)){
        $target=[IO.Path]::Combine($root,$fail.ToString())
        $null=New-Item -ItemType Directory -Path $target
        $worker=Join-Path $target 'quota-worker.cjs'
        $settings=Join-Path $target 'settings.json'
        $stats=Join-Path $target 'token-stats.json'
        [IO.File]::WriteAllText($worker,'old worker')
        [IO.File]::WriteAllText($settings,'{"enabled":true}')
        [IO.File]::WriteAllText($stats,'{"updatedAt":123}')
        if($fail){$null=New-Item -ItemType Directory -Path (Join-Path $target 'usage-icon.ico')}

        $failed=$false
        try{$null=$apply.Invoke($null,[object[]]@($target,$false))}
        catch{
            $errorCause=$_.Exception.GetBaseException()
            if(!$fail -or ($errorCause -isnot [IO.IOException] -and $errorCause -isnot [UnauthorizedAccessException])){throw}
            $failed=$true
        }

        if($fail){
            if(!$failed -or [IO.File]::ReadAllText($worker) -ne 'old worker' -or (Test-Path (Join-Path $target 'runtime/node.exe'))){
                throw 'Rollback did not restore the prior program files.'
            }
        }else{
            $executable=Join-Path $target 'Codex用量统计.exe'
            if([IO.File]::ReadAllText($worker) -eq 'old worker' -or
               [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion -ne $assembly.GetName().Version.ToString()){
                throw 'The new application and worker were not installed.'
            }
        }
        if([IO.File]::ReadAllText($settings) -ne '{"enabled":true}' -or [IO.File]::ReadAllText($stats) -ne '{"updatedAt":123}'){
            throw 'The update changed local settings or statistics.'
        }
    }
    Write-Output 'PASS: in-place update, preserved local data, rollback after partial file replacement.'
}finally{
    [IO.Directory]::Delete($root,$true)
}
