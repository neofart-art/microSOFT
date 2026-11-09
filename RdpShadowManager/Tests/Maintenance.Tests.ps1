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
foreach ($name in @('New-Plan','Read-Value','Write-Value','Same-Value','Save-Backup','Restore-Conflict','Resolve-OutlookVersions','Assert-AutomaticVersions','Normalize-IPv4','Find-RdpIPv4')) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if (-not $node) { throw "Missing function: $name" }
    . ([ScriptBlock]::Create($node.Extent.Text))
}
# Pure detection fixtures: no installed Office or real Outlook registry is needed.
$office2019=Resolve-OutlookVersions @(@{Path='C:\Office16\OUTLOOK.EXE';RegistryBranch='16.0';FileVersion='16.0.19127.20860'}) @()
Assert ($office2019.Error -eq '' -and ($office2019.Versions -join ',') -eq '16.0') 'Office 2019 build maps to branch 16.0'
$office2013=Resolve-OutlookVersions @(@{Path='C:\Office15\OUTLOOK.EXE';RegistryBranch='15.0';FileVersion='15.0.5579.1000'}) @()
Assert ($office2013.Error -eq '' -and ($office2013.Versions -join ',') -eq '15.0') 'Outlook 2013 maps to branch 15.0'
$both=Resolve-OutlookVersions @(@{Path='a';RegistryBranch='15.0';FileVersion='15.0.1.0'},@{Path='b';RegistryBranch='16.0';FileVersion='16.0.1.0'}) @()
Assert (($both.Versions -join ',') -eq '15.0,16.0') 'Two independently confirmed versions keep both branches'
$wrong=Resolve-OutlookVersions @(@{Path='a';RegistryBranch='15.0';FileVersion='16.0.1.0'}) @()
Assert ($wrong.Error.Length -gt 0) 'Conflicting file and registration blocks application'
$missing=Resolve-OutlookVersions @() @()
Assert ($missing.Error.Length -gt 0) 'No classic Outlook is not an invitation to write all versions'
$unknown=Resolve-OutlookVersions @(@{Path='a';RegistryBranch='16.0';FileVersion='unknown'}) @()
Assert ($unknown.Error.Length -gt 0) 'Unknown executable version is rejected'
$unreadable=Resolve-OutlookVersions @(@{Path='a';RegistryBranch='16.0';FileVersion='16.0.1.0'}) @('access denied')
Assert ($unreadable.Error -match 'access denied') 'Incomplete discovery blocks blind repair'
Assert ((@(Assert-AutomaticVersions $office2019 @('16.0')) -join ',') -eq '16.0') 'Apply uses detected branch'
try { Assert-AutomaticVersions $office2019 @('15.0'); throw 'Changed versions accepted' } catch { Assert ($_.Exception.Message -ne 'Changed versions accepted') 'Installation change after confirmation is rejected' }
try { Assert-AutomaticVersions $missing @(); throw 'Absent Outlook accepted' } catch { Assert ($_.Exception.Message -ne 'Absent Outlook accepted') 'No writes when automatic discovery fails' }
# Detection and branch validation must precede backup creation / mutation.
Assert ($source.IndexOf('$versions=@(Assert-AutomaticVersions') -lt $source.IndexOf('$entries=@()')) 'Automatic validation precedes registry mutation planning'
$sockets=@(
 @{LocalAddress='172.31.1.11';RemoteAddress='172.31.1.99';LocalPort=3389;State='Established'},
 @{LocalAddress='26.209.222.170';RemoteAddress='26.0.0.9';LocalPort=3389;State='Established'},
 @{LocalAddress='26.209.222.170';RemoteAddress='172.31.1.99';LocalPort=443;State='Established'}
)
Assert ((@(Find-RdpIPv4 $sockets @('172.31.1.99')) -join ',') -eq '172.31.1.11') 'Current client RDP connection selects 172.31.1.11, not unrelated 26 address'
Assert (@(Find-RdpIPv4 $sockets @('192.0.2.99')).Count -eq 0) 'Other users connections cannot determine current endpoint'
Assert (@(Find-RdpIPv4 @(@{LocalAddress='172.31.1.11';RemoteAddress='172.31.1.99';LocalPort=3389;State='Listen'}) @('172.31.1.99')).Count -eq 0) 'Listening socket is not connected'
Assert ((Normalize-IPv4 '::ffff:172.31.1.11') -eq '172.31.1.11') 'IPv4 mapped server socket is normalized to IPv4'
Assert ((Normalize-IPv4 '2001:db8::1') -eq '') 'Native IPv6 is not displayed'
Assert (@(Find-RdpIPv4 @($sockets[0],@{LocalAddress='172.31.1.12';RemoteAddress='172.31.1.99';LocalPort=3389;State='Established'}) @('172.31.1.99')).Count -eq 2) 'Multiple endpoints are kept ambiguous, not guessed'
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
    Remove-Item -LiteralPath "$temp.previous" -Force -ErrorAction SilentlyContinue
}
Write-Host "PASS: $count maintenance checks"
