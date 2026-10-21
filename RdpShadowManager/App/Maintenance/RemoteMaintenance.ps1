param([Parameter(Mandatory=$true)][string]$RequestJson)
# This script is sent ONLY through Invoke-Command. Never dot-source or execute it locally.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$result = @{ Code=1; Computer=$env:COMPUTERNAME; User=''; Sid=''; SessionId=0; MachineId=''; Versions=@(); Backups=@(); Conflicts=@(); Changed=@(); Errors=@(); Success=0; Skipped=0; Failed=0; BackupPath=''; OperationId=''; Message='' }
$lock = $null; $held = $false
$watch = [Diagnostics.Stopwatch]::StartNew()
function Check-Deadline { if ($watch.Elapsed.TotalSeconds -gt 100) { throw 'Превышено время выполнения помощника. См. резервную копию и отчёт.' } }
try {
    if (-not (Get-Variable PSSenderInfo -ErrorAction SilentlyContinue) -or $null -eq $PSSenderInfo) { throw 'Помощник разрешён только в удалённой WinRM-сессии. Локальный запуск запрещён.' }
    $r = $RequestJson | ConvertFrom-Json
    $allowed = @('Action','Computer','LocalMachineId','SessionId','User','Domain','Sid','MachineId','Versions','CloseOutlook','BackupId','OperationId')
    foreach ($p in $r.PSObject.Properties.Name) { if ($p -notin $allowed) { throw "Неизвестный параметр: $p" } }
    if ($r.Action -notin @('Inspect','Apply','RestorePreview','Restore')) { throw 'Неизвестная операция.' }
    if ([int]$r.SessionId -le 0) { throw 'Некорректный ID сеанса.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Нужны административные права на удалённом компьютере.' }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try { $k=$base.OpenSubKey('SOFTWARE\Microsoft\Cryptography'); try { $machine=[string]$k.GetValue('MachineGuid') } finally { if ($k) { $k.Dispose() } } } finally { $base.Dispose() }
    if (-not $machine -or -not $r.LocalMachineId -or $machine -eq $r.LocalMachineId) { throw 'Локальный компьютер администратора исключён из обслуживания (MachineGuid совпадает или недоступен).' }
    $result.MachineId=$machine
    if ($r.Action -ne 'Inspect' -and $machine -ne $r.MachineId) { throw 'Изменилась идентичность целевого компьютера. Откройте диалог заново.' }
    if (-not ('RsmNative' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class RsmNative {
 [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)] static extern bool Info(IntPtr server,int id,int type,out IntPtr data,out int size);
 [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr data);
 public static string Read(int id,int type) { IntPtr data; int size; if(!Info(IntPtr.Zero,id,type,out data,out size)) throw new Win32Exception(Marshal.GetLastWin32Error()); try { return Marshal.PtrToStringUni(data) ?? ""; } finally { WTSFreeMemory(data); } }
 public static int State(int id) { IntPtr data; int size; if(!Info(IntPtr.Zero,id,8,out data,out size)) throw new Win32Exception(Marshal.GetLastWin32Error()); try { return Marshal.ReadInt32(data); } finally { WTSFreeMemory(data); } }
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int type,out int data,int size,out int returned);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,int flags,System.Text.StringBuilder name,ref int size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
 [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint ms);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static string[] CloseOutlook(int session,string sid) {
  var errors=new List<string>();
  foreach(var candidate in Process.GetProcessesByName("OUTLOOK")) {
   using(candidate) {
    IntPtr process=IntPtr.Zero, token=IntPtr.Zero;
    try {
     if(candidate.SessionId != session) continue;
     process=OpenProcess(0x101001,false,candidate.Id); if(process==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
     int length=32768; var image=new System.Text.StringBuilder(length);
     if(!QueryFullProcessImageName(process,0,image,ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
     if(!string.Equals(Path.GetFileName(image.ToString()),"OUTLOOK.EXE",StringComparison.OrdinalIgnoreCase)) continue;
     if(!OpenProcessToken(process,8,out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
     int tokenSession, returned;
     if(!GetTokenInformation(token,12,out tokenSession,4,out returned)) throw new Win32Exception(Marshal.GetLastWin32Error());
     using(var owner=new WindowsIdentity(token)) { if(tokenSession != session || owner.User == null || owner.User.Value != sid) continue; }
     if(!TerminateProcess(process,1)) throw new Win32Exception(Marshal.GetLastWin32Error());
     if(WaitForSingleObject(process,5000)!=0) throw new Exception("Не подтверждено завершение Outlook (5 секунд)");
    } catch(Exception ex) { errors.Add("PID="+candidate.Id+": "+ex.Message); }
    finally { if(token!=IntPtr.Zero) CloseHandle(token); if(process!=IntPtr.Zero) CloseHandle(process); }
   }
  }
  return errors.ToArray();
 }
}
'@
    }
    function Assert-Target {
        Check-Deadline
        $user=[RsmNative]::Read([int]$r.SessionId,5); $domain=[RsmNative]::Read([int]$r.SessionId,7)
        if (-not $user -or -not $domain -or [RsmNative]::State([int]$r.SessionId) -ne 0) { throw 'Сеанс завершён, не активен или сведения о пользователе недоступны.' }
        if ($user -ine $r.User -or $domain -ine $r.Domain) { throw 'Пользователь выбранного сеанса изменился. Обновите список.' }
        $sid=([Security.Principal.NTAccount]::new($domain,$user)).Translate([Security.Principal.SecurityIdentifier]).Value
        if (-not $sid -or ($r.Action -ne 'Inspect' -and $sid -ne $r.Sid)) { throw 'SID пользователя изменился или не определён.' }
        $hku=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users,[Microsoft.Win32.RegistryView]::Registry64)
        try { $profile=$hku.OpenSubKey($sid); if (-not $profile) { throw "Профиль HKEY_USERS\$sid не загружен или недоступен." }; $profile.Dispose() } finally { $hku.Dispose() }
        $result.User="$domain\$user"; $result.Sid=$sid; $result.SessionId=[int]$r.SessionId
    }
    Assert-Target
    $lock=[Threading.Mutex]::new($false,'Global\RdpShadowManager-Maintenance')
    try { $held=$lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held=$true }
    if (-not $held) { throw 'На этом компьютере уже выполняется обслуживание. Дождитесь его завершения.' }
    function Get-Versions {
        $versions=@()
        foreach ($view in @('Registry64','Registry32')) {
            $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::$view)
            try {
                foreach ($v in @('16.0','15.0','14.0')) {
                    $k=$h.OpenSubKey("SOFTWARE\Microsoft\Office\$v\Outlook\InstallRoot")
                    if ($k) { try { $path=[string]$k.GetValue('Path'); if ($path -and (Test-Path -LiteralPath (Join-Path $path 'OUTLOOK.EXE') -PathType Leaf)) { $versions+=$v } } finally { $k.Dispose() } }
                }
                $k=$h.OpenSubKey('SOFTWARE\Microsoft\Office\ClickToRun\Configuration')
                if ($k) {
                    try {
                        $products=[string]$k.GetValue('ProductReleaseIds'); $version=[string]$k.GetValue('VersionToReport'); $install=[string]$k.GetValue('InstallationPath')
                        if ($version -match '^16\.' -and $products -match '(?i)(Outlook|O365ProPlus|O365Business|ProPlus|Professional|Standard|HomeBusiness|Mondo)' -and $install) {
                            foreach ($suffix in @('root\Office16\OUTLOOK.EXE','Office16\OUTLOOK.EXE')) { if (Test-Path -LiteralPath (Join-Path $install $suffix) -PathType Leaf) { $versions+='16.0' } }
                        }
                    } finally { $k.Dispose() }
                }
            } finally { $h.Dispose() }
        }
        return @($versions | Select-Object -Unique)
    }
    $result.Versions=@(Get-Versions)
    $root=Join-Path $env:ProgramData 'RdpShadowManager\Backups'
    function Assert-SafeDirectory([string]$path) {
        if (Test-Path -LiteralPath $path) {
            $item=Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Небезопасный каталог резервных копий: $path" }
        }
    }
    Assert-SafeDirectory (Split-Path $root); Assert-SafeDirectory $root
    if (Test-Path -LiteralPath $root) {
        $existingAcl=Get-Acl -LiteralPath $root
        foreach ($rule in $existingAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Каталог резервных копий доступен посторонним. Проверьте ACL вручную; автоматическое восстановление заблокировано.' }
        }
    }
    function Get-Backups {
        if (-not (Test-Path -LiteralPath $root)) { return @() }
        $list=@()
        foreach ($f in @(Get-ChildItem -LiteralPath $root -Filter '*.json' -File)) {
            if ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Резервная копия является ссылкой.' }
            try { $b=Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json } catch { continue }
            if ($b.MachineId -eq $machine -and $b.Sid -eq $result.Sid -and $b.OperationId -match '^[a-f0-9-]{36}$') {
                $list+=@{ Id=$b.OperationId; Time=$b.Time; Versions=@($b.Versions); Path=$f.FullName }
            }
        }
        return @($list | Sort-Object { $_.Time } -Descending)
    }
    if ($r.Action -eq 'Inspect') {
        $result.Backups=@(Get-Backups); $result.Code=0; $result.Message='Цель проверена. Профиль пользователя загружен.'
    } else {
        function New-Plan([string[]]$versions) {
            $params=@('disableautodiscoverv2service','excludeexplicito365endpoint','excludehttpredirect','excludehttpsautodiscoverdomain','excludehttpsrootdomain','excludelastknowngoodurl','excludescplookup','excludesrvrecord')
            $items=@()
            foreach ($v in $versions) {
                if ($v -notin @('14.0','15.0','16.0')) { throw "Недопустимая версия Office: $v" }
                foreach ($view in @('Registry64','Registry32')) {
                    foreach ($hive in @('Users','LocalMachine')) {
                        foreach ($policy in @($false,$true)) {
                            $prefix=if ($hive -eq 'Users') { "$($result.Sid)\Software" } else { 'SOFTWARE' }
                            $path=if ($policy) { "$prefix\Policies\Microsoft\Office\$v\Outlook\AutoDiscover" } else { "$prefix\Microsoft\Office\$v\Outlook\AutoDiscover" }
                            $names=if ($policy) { $params } else { @('excludeexplicito365endpoint') }
                            foreach ($name in $names) { $items+=@{ Hive=$hive; View=$view; Path=$path; Name=$name } }
                        }
                    }
                }
            }
            return $items
        }
        function Read-Value($entry) {
            $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::$($entry.Hive),[Microsoft.Win32.RegistryView]::$($entry.View))
            try {
                $k=$h.OpenSubKey($entry.Path)
                if (-not $k) { return @{ Exists=$false; Kind=''; Data=$null } }
                try {
                    if ($entry.Name -notin $k.GetValueNames()) { return @{ Exists=$false; Kind=''; Data=$null } }
                    $kind=$k.GetValueKind($entry.Name).ToString()
                    $data=$k.GetValue($entry.Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    switch ($kind) {
                        {$_ -in @('Binary','None')} { $data=[Convert]::ToBase64String([byte[]]$data) }
                        'DWord' { $data=([int]$data).ToString([Globalization.CultureInfo]::InvariantCulture) }
                        'QWord' { $data=([long]$data).ToString([Globalization.CultureInfo]::InvariantCulture) }
                        'MultiString' { $data=@([string[]]$data) }
                        {$_ -in @('String','ExpandString')} { $data=[string]$data }
                        default { throw "Неподдерживаемый тип реестра: $kind" }
                    }
                    return @{ Exists=$true; Kind=$kind; Data=$data }
                } finally { $k.Dispose() }
            } finally { $h.Dispose() }
        }
        function Same-Value($a,$b) {
            if ([bool]$a.Exists -ne [bool]$b.Exists) { return $false }
            if (-not $a.Exists) { return $true }
            return ($a.Kind -eq $b.Kind -and (ConvertTo-Json -InputObject $a.Data -Compress -Depth 5) -ceq (ConvertTo-Json -InputObject $b.Data -Compress -Depth 5))
        }
        function Restore-Conflict($entry,$current) {
            if ($entry.State -in @('Pending','Skipped')) { return $false }
            return (-not (Same-Value $current $entry.Before) -and ($entry.State -eq 'Restored' -or -not (Same-Value $current $entry.Expected)))
        }
        function Write-Value($entry,$value) {
            $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::$($entry.Hive),[Microsoft.Win32.RegistryView]::$($entry.View))
            try {
                if (-not $value.Exists) {
                    $k=$h.OpenSubKey($entry.Path,$true)
                    if ($k) { try { $k.DeleteValue($entry.Name,$false); $k.Flush() } finally { $k.Dispose() } }; return
                }
                $k=$h.CreateSubKey($entry.Path,$true)
                try {
                    $data=switch ($value.Kind) {
                        'DWord' { [int]::Parse([string]$value.Data,[Globalization.CultureInfo]::InvariantCulture) }
                        'QWord' { [long]::Parse([string]$value.Data,[Globalization.CultureInfo]::InvariantCulture) }
                        'Binary' { ,([Convert]::FromBase64String($value.Data)) }
                        'None' { ,([Convert]::FromBase64String($value.Data)) }
                        'MultiString' { ,([string[]]$value.Data) }
                        'String' { [string]$value.Data }
                        'ExpandString' { [string]$value.Data }
                        default { throw 'Недопустимый тип в резервной копии.' }
                    }
                    $k.SetValue($entry.Name,$data,[Microsoft.Win32.RegistryValueKind]::$($value.Kind)); $k.Flush()
                } finally { $k.Dispose() }
            } finally { $h.Dispose() }
        }
        function Save-Backup($backup,[string]$path) {
            # Atomic replacement + disk flush: the full original snapshot exists before any registry writes.
            $json=ConvertTo-Json -InputObject $backup -Depth 15
            $tmp="$path.tmp"
            $stream=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $bytes=[Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
            if (Test-Path -LiteralPath $path) { [IO.File]::Replace($tmp,$path,$null) } else { [IO.File]::Move($tmp,$path) }
        }
        $desired=@{ Exists=$true; Kind='DWord'; Data='1' }
        if ($r.Action -eq 'Apply') {
            $versions=@($r.Versions | Select-Object -Unique)
            if ($versions.Count -eq 0) { throw 'Outlook не найден или версия не выбрана. Укажите версию явно в диалоге.' }
            $plan=@(New-Plan $versions)
            $entries=@()
            foreach ($entry in $plan) { Check-Deadline; $entries+=@{ Target=$entry; Before=(Read-Value $entry); Expected=$desired; State='Pending' } }
            [IO.Directory]::CreateDirectory($root) | Out-Null
            Assert-SafeDirectory (Split-Path $root); Assert-SafeDirectory $root
            $acl=[Security.AccessControl.DirectorySecurity]::new()
            $acl.SetAccessRuleProtection($true,$false)
            foreach ($s in @('S-1-5-18','S-1-5-32-544')) {
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($s),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
            }
            Set-Acl -LiteralPath $root -AclObject $acl
            $operationId=([guid]$r.OperationId).ToString()
            if ($operationId -eq [guid]::Empty.ToString()) { throw 'Не указан идентификатор операции.' }
            $path=Join-Path $root "$operationId.json"
            if (Test-Path -LiteralPath $path) { throw 'Идентификатор операции уже использован.' }
            $backup=@{ Schema=1; OperationId=$operationId; MachineId=$machine; Computer=$env:COMPUTERNAME; Sid=$result.Sid; User=$result.User; SessionId=$result.SessionId; Time=[DateTimeOffset]::Now.ToString('O'); Versions=$versions; Entries=$entries; Complete=$false }
            Save-Backup $backup $path
            $result.BackupPath=$path; $result.OperationId=$operationId; $result.Versions=$versions
        } else {
            $id=([guid]$r.BackupId).ToString(); $path=Join-Path $root "$id.json"
            if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Резервная копия является ссылкой.' }
            $backup=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($backup.Schema -ne 1 -or $backup.OperationId -ne $id -or $backup.MachineId -ne $machine -or $backup.Sid -ne $result.Sid) { throw 'Резервная копия не соответствует компьютеру или SID.' }
            $plan=@(New-Plan @($backup.Versions))
            if (@($backup.Entries).Count -ne $plan.Count) { throw 'Повреждён план резервной копии.' }
            for ($i=0;$i -lt $plan.Count;$i++) {
                foreach ($field in @('Hive','View','Path','Name')) { if ($backup.Entries[$i].Target.$field -cne $plan[$i].$field) { throw 'Недопустимый путь в резервной копии.' } }
            }
            $entries=@($backup.Entries)
            $result.BackupPath=$path; $result.OperationId=$id; $result.Versions=@($backup.Versions)
            foreach ($entry in $entries) {
                $current=Read-Value $entry.Target
                if ($entry.State -eq 'Pending' -or $entry.State -eq 'Skipped') { continue }
                if (Restore-Conflict $entry $current) {
                    $result.Conflicts+=@{ Path="$($entry.Target.Hive)\$($entry.Target.Path)"; View=$entry.Target.View; Name=$entry.Target.Name; Current=$current; Expected=$entry.Expected; Before=$entry.Before }
                }
            }
            if ($result.Conflicts.Count -gt 0) { $result.Code=3; throw "Конфликт восстановления: $($result.Conflicts.Count) значений изменены после применения. Восстановление не начато." }
        }
        if ($r.Action -eq 'RestorePreview') {
            $result.Code=0; $result.Message='Конфликтов нет. Перед восстановлением проверка будет выполнена повторно.'
        } else {
            Assert-Target
            if ($r.CloseOutlook) {
                $closeErrors=@([RsmNative]::CloseOutlook([int]$r.SessionId,$result.Sid))
                if ($closeErrors.Count -gt 0) { $result.Errors+=$closeErrors; throw 'Не удалось закрыть Outlook выбранного пользователя. Изменения реестра не начаты.' }
            }
            Assert-Target
            foreach ($entry in $entries) {
                Assert-Target
                if ($r.Action -eq 'Restore' -and $entry.State -in @('Pending','Skipped')) { $result.Skipped++; continue }
                $value=if ($r.Action -eq 'Apply') { $desired } else { $entry.Before }
                $current=Read-Value $entry.Target
                if ($r.Action -eq 'Restore' -and $entry.State -eq 'Restored' -and -not (Same-Value $current $value)) { throw 'Конфликт: ранее восстановленное значение изменено повторно.' }
                if (Same-Value $current $value) { $result.Skipped++; if ($r.Action -eq 'Apply') { $entry.State='Skipped' }; continue }
                # Recheck concurrent edits immediately before each write; stop rather than overwrite them.
                $expected=if ($r.Action -eq 'Apply') { $entry.Before } else { $entry.Expected }
                if (-not (Same-Value $current $expected)) { throw "Конфликт перед записью: $($entry.Target.Path) / $($entry.Target.Name). Остальные значения не обработаны." }
                if ($r.Action -eq 'Apply') { $entry.State='WriteIntent'; Save-Backup $backup $path }
                try {
                    Write-Value $entry.Target $value
                    if (-not (Same-Value (Read-Value $entry.Target) $value)) { throw 'Проверка записанного значения не пройдена.' }
                    $result.Success++
                    $result.Changed+=@{ Path="$($entry.Target.Hive)\$($entry.Target.Path)"; View=$entry.Target.View; Name=$entry.Target.Name; Value=$value }
                    if ($r.Action -eq 'Apply') { $entry.State='Verified' } else { $entry.State='Restored' }; Save-Backup $backup $path
                } catch { $result.Failed++; $result.Errors+="$($entry.Target.Hive)\$($entry.Target.Path) [$($entry.Target.View)] $($entry.Target.Name): $($_.Exception.Message)" }
            }
            if ($r.Action -eq 'Apply') { $backup.Complete=($result.Failed -eq 0); Save-Backup $backup $path }
            $result.Code=if ($result.Failed -eq 0) { 0 } else { 1 }
            $result.Message=if ($result.Code -eq 0) { 'Помощник завершён; записанные значения проверены.' } else { 'Операция завершена с ошибками. Сохранена резервная копия; см. отчёт.' }
        }
    }
} catch {
    $result.Errors+=$_.Exception.Message; $result.Failed++
    $result.Message=$_.Exception.Message
    if ($result.Code -ne 3) { $result.Code=1 }
} finally {
    if ($held) { $lock.ReleaseMutex() }
    if ($lock) { $lock.Dispose() }
}
ConvertTo-Json -InputObject $result -Depth 18 -Compress
