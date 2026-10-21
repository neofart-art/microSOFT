$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$sourcePath=Join-Path $PSScriptRoot '../App/Maintenance/RemoteMaintenance.ps1'
$source=[IO.File]::ReadAllText((Resolve-Path $sourcePath),[Text.Encoding]::UTF8)
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
if ($errors.Count -gt 0) { throw ($errors | Out-String) }
$count=0
function Assert($value,[string]$message) { if (-not $value) { throw $message }; $script:count++ }
# A direct invocation must stop before any registry or process access.
$guard=& ([ScriptBlock]::Create($source)) '{"Action":"Inspect"}' | ConvertFrom-Json
Assert ($guard.Code -ne 0 -and $guard.Message -match 'WinRM') 'Local invocation must fail closed'
foreach ($name in @('New-Plan','Read-Value','Write-Value','Same-Value','Save-Backup','Restore-Conflict')) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if (-not $node) { throw "Missing function: $name" }
    . ([ScriptBlock]::Create($node.Extent.Text))
}
$native=$ast.Find({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Add-Type'},$true)
. ([ScriptBlock]::Create($native.Extent.Text))
Assert ($null -ne ('RsmNative' -as [type])) 'Native WTS and token-check code compiles'
$result=@{ Sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
$plan=@(New-Plan @('16.0'))
Assert ($plan.Count -eq 36) 'Expected 36 logical values per Office version (both views, both hives)'
foreach ($entry in $plan) {
    Assert ($entry.Hive -in @('Users','LocalMachine')) 'Never target HKCU'
    Assert ($entry.View -in @('Registry32','Registry64')) 'Explicit registry view'
    if ($entry.Hive -eq 'Users') { Assert ($entry.Path.StartsWith($result.Sid+'\')) 'HKU must be SID scoped' }
    if ($entry.Path -notmatch '\\Policies\\') { Assert ($entry.Name -eq 'excludeexplicito365endpoint') 'Only one non-policy value' }
}
$bat=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Reference/Outlook repeirNE.bat'),[Text.Encoding]::UTF8)
$params=([regex]::Match($bat,'set "PARAMS=([^"]+)"').Groups[1].Value -split ' ' | Sort-Object)
$policy=@($plan | Where-Object { $_.Path -match '\\Policies\\' } | ForEach-Object { $_.Name } | Select-Object -Unique | Sort-Object)
Assert (($params -join ',') -ceq ($policy -join ',')) 'Policy names must exactly match supplied BAT'
# Only this test uses a disposable HKCU scratch key. Production entry points reject local execution.
$scratch='Software\RdpShadowManager-Tests-'+[guid]::NewGuid().ToString()
$temp=Join-Path ([IO.Path]::GetTempPath()) ('RsmBackupTest-'+[guid]::NewGuid().ToString()+'.json')
try {
    $entry=@{ Hive='CurrentUser'; View='Registry64'; Path=$scratch; Name='value' }
    $absent=Read-Value $entry
    Assert (-not $absent.Exists) 'Absent snapshot'
    $fixtures=@(
        @{ Exists=$true; Kind='String'; Data='original text' },
        @{ Exists=$true; Kind='ExpandString'; Data='%USERPROFILE%\Outlook' },
        @{ Exists=$true; Kind='MultiString'; Data=@('one','','three') },
        @{ Exists=$true; Kind='DWord'; Data='-1' },
        @{ Exists=$true; Kind='QWord'; Data='9223372036854775807' },
        @{ Exists=$true; Kind='Binary'; Data=[Convert]::ToBase64String([byte[]]@(0,1,128,255)) },
        @{ Exists=$true; Kind='None'; Data=[Convert]::ToBase64String([byte[]]@(0,255)) }
    )
    foreach ($original in $fixtures) {
        Write-Value $entry $original
        $snapshot=Read-Value $entry
        Assert (Same-Value $snapshot $original) "Read original type $($original.Kind)"
        $backup=@{ Entries=@(@{Target=$entry; Before=$snapshot}); Time=[DateTimeOffset]::Now.ToString('O') }
        Save-Backup $backup $temp
        $persisted=Get-Content -LiteralPath $temp -Raw | ConvertFrom-Json
        Write-Value $entry @{ Exists=$true; Kind='DWord'; Data='1' }
        Assert (-not (Same-Value (Read-Value $entry) $snapshot)) 'Applied value differs from original'
        Write-Value $entry $persisted.Entries[0].Before
        Assert (Same-Value (Read-Value $entry) $original) "Exact restore $($original.Kind)"
    }
    $before=@{Exists=$true;Kind='String';Data='before'}
    $expected=@{Exists=$true;Kind='DWord';Data='1'}
    $restoreEntry=@{State='Verified';Before=$before;Expected=$expected}
    Assert (-not (Restore-Conflict $restoreEntry $expected)) 'Program value can be restored'
    Assert (-not (Restore-Conflict $restoreEntry $before)) 'Already original value has no conflict'
    Assert (Restore-Conflict $restoreEntry @{Exists=$true;Kind='DWord';Data='2'}) 'Third-party change blocks restore'
    $restoreEntry.State='Restored'
    Assert (Restore-Conflict $restoreEntry $expected) 'Repeated edits after restore are conflicts'
    $restoreEntry.State='Pending'
    Assert (-not (Restore-Conflict $restoreEntry $expected)) 'Unattempted values are left alone'
    Write-Value $entry $absent
    Assert (-not (Read-Value $entry).Exists) 'Originally absent value is deleted'
    $k=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($scratch)
    try { Assert ($null -ne $k) 'Restore must not delete registry keys' } finally { if ($k) { $k.Dispose() } }
    Assert (-not (Same-Value @{Exists=$true;Kind='DWord';Data='2'} @{Exists=$true;Kind='DWord';Data='1'})) 'Changed value is a conflict'
} finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($scratch,$false)
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$temp.tmp" -Force -ErrorAction SilentlyContinue
}
Write-Host "PASS: $count maintenance checks"
