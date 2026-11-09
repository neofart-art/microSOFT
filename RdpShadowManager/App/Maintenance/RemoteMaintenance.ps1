param([Parameter(Mandatory=$true)][string]$RequestJson)
# This script is sent ONLY through Invoke-Command. Never dot-source or execute it locally.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$result = @{ Completed=$false; Verified=$false; OutlookDetectionError=''; Windows=''; WindowsVersion=''; Addresses=@(); PrimaryIPv4=''; RdpIPv4=''; SystemInfo=$null; Office=@(); OfficeKnown=$false; Code=1; Computer=$env:COMPUTERNAME; User=''; Sid=''; SessionId=0; MachineId=''; Versions=@(); Backups=@(); Conflicts=@(); Changed=@(); Errors=@(); Success=0; Skipped=0; Failed=0; BackupPath=''; OperationId=''; Message='' }
$lock = $null; $held = $false
$watch = [Diagnostics.Stopwatch]::StartNew()
function Check-Deadline { if ($watch.Elapsed.TotalSeconds -gt 100) { throw 'Превышено время выполнения помощника. См. резервную копию и отчёт.' } }
function Normalize-IPv4([string]$value) {
    $ip=$null
    if (-not [Net.IPAddress]::TryParse($value,[ref]$ip)) { return '' }
    if ($ip.IsIPv4MappedToIPv6) { $ip=$ip.MapToIPv4() }
    if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip) -or $ip.Equals([Net.IPAddress]::Any)) { return '' }
    return $ip.ToString()
}
function Find-RdpIPv4([object[]]$connections,[string[]]$clients) {
    $known=@($clients | ForEach-Object { Normalize-IPv4 $_ } | Where-Object {$_} | Select-Object -Unique)
    return @(@(foreach ($connection in $connections) {
        $local=Normalize-IPv4 ([string]$connection.LocalAddress)
        $peer=Normalize-IPv4 ([string]$connection.RemoteAddress)
        if ($connection.LocalPort -eq 3389 -and [string]$connection.State -eq 'Established' -and $local -and $peer -in $known) { $local }
    }) | Select-Object -Unique)
}
function Read-RdpConnection {
    try {
        $connections=@(Get-NetTCPConnection -State Established -ErrorAction Stop | Where-Object {$_.LocalPort -eq 3389})
        $matching=@(Find-RdpIPv4 $connections @($r.ClientIPv4))
        if ($matching.Count -eq 1) { $result.RdpIPv4=[string]$matching[0] }
        elseif ($matching.Count -gt 1) { $result.Errors+='Несколько RDP-адресов текущего клиента; адрес соединения неоднозначен.' }
        return @($matching)
    } catch { $result.Errors+="Проверка адреса RDP: $($_.Exception.Message)"; return @() }
}
function Read-SystemCim([string]$class,[string[]]$properties,[string]$namespace='root/cimv2') {
    return @(Get-CimInstance -Namespace $namespace -ClassName $class -Property $properties -OperationTimeoutSec 6 -ErrorAction Stop)
}
function Read-RestartIndicators {
    $reasons=@()
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try {
        foreach ($marker in @(
            @{Path='SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending';Text='Component Based Servicing ожидает перезагрузку'},
            @{Path='SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired';Text='Windows Update ожидает перезагрузку'}
        )) {
            $key=$base.OpenSubKey($marker.Path)
            if ($key) { try { $reasons+=$marker.Text } finally { $key.Dispose() } }
        }
        $key=$base.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager')
        if ($key) {
            try {
                if (@($key.GetValue('PendingFileRenameOperations',$null) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
                    $reasons+='Есть отложенные операции с файлами (PendingFileRenameOperations)'
                }
            } finally { $key.Dispose() }
        }
        return @{Pending=($reasons.Count -gt 0);Reasons=$reasons}
    } finally { $base.Dispose() }
}
function Get-SystemInformation([string[]]$sections=@('computer','performance','volumes','physical','updates','restart')) {
    $details=@{
        CapturedAt=[DateTimeOffset]::UtcNow.ToString('O');Manufacturer='';Model='';SerialNumber='';Windows='';LastBoot='';UptimeSeconds=$null;
        InstalledRamBytes=$null;VisibleRamBytes=$null;FreeRamBytes=$null;Cpus=@();Volumes=@();PhysicalDisks=@();Updates=@();
        RebootPending=$null;RebootReasons=@();Sections=@{};SectionErrors=@{}
    }
    $cache=@{OS=$null}
    function Read-Section([string]$section,[scriptblock]$read) {
        if ($section -notin $sections) { return }
        $details.Sections[$section]='loading'
        try {
            if ($watch.Elapsed.TotalSeconds -gt 50) { throw 'Достигнут лимит времени чтения сведений.' }
            & $read
            if (-not $details.SectionErrors.ContainsKey($section)) { $details.Sections[$section]='ok' }
            else { $details.Sections[$section]='partial' }
        } catch {
            $details.Sections[$section]='error'
            $details.SectionErrors[$section]=@($details.SectionErrors[$section])+@($_.Exception.Message)
        }
    }
    Read-Section 'computer' {
        $cs=@(Read-SystemCim 'Win32_ComputerSystem' @('Manufacturer','Model','TotalPhysicalMemory'))
        if ($cs.Count -eq 0) { throw 'Win32_ComputerSystem не вернул сведения.' }
        $details.Manufacturer=[string]$cs[0].Manufacturer; $details.Model=[string]$cs[0].Model
        if ($null -ne $cs[0].TotalPhysicalMemory) { $details.InstalledRamBytes=[long]$cs[0].TotalPhysicalMemory }
        $os=@(Read-SystemCim 'Win32_OperatingSystem' @('Caption','LastBootUpTime','TotalVisibleMemorySize','FreePhysicalMemory'))
        if ($os.Count -eq 0) { throw 'Win32_OperatingSystem не вернул сведения.' }
        $cache.OS=$os[0]; $details.Windows=[string]$os[0].Caption
        if ($null -ne $os[0].LastBootUpTime) {
            $boot=([datetime]$os[0].LastBootUpTime).ToUniversalTime()
            $details.LastBoot=$boot.ToString('O')
            $details.UptimeSeconds=[Math]::Max(0,([datetime]::UtcNow-$boot).TotalSeconds)
        }
        $bios=@(Read-SystemCim 'Win32_BIOS' @('SerialNumber'))
        if ($bios.Count -eq 0) { throw 'Серийный номер BIOS недоступен.' }
        $details.SerialNumber=[string]$bios[0].SerialNumber
    }
    Read-Section 'performance' {
        $cpus=@(Read-SystemCim 'Win32_Processor' @('Name','NumberOfCores','NumberOfLogicalProcessors','LoadPercentage'))
        if ($cpus.Count -eq 0) { throw 'Сведения о процессорах недоступны.' }
        $details.Cpus=@(foreach ($cpu in $cpus) {
            @{Name=[string]$cpu.Name;Cores=$cpu.NumberOfCores;LogicalProcessors=$cpu.NumberOfLogicalProcessors;LoadPercent=$cpu.LoadPercentage}
        })
        if (-not $cache.OS) {
            $os=@(Read-SystemCim 'Win32_OperatingSystem' @('Caption','LastBootUpTime','TotalVisibleMemorySize','FreePhysicalMemory'))
            if ($os.Count -eq 0) { throw 'Сведения об ОЗУ недоступны.' }
            $cache.OS=$os[0]
        }
        if ($null -ne $cache.OS.TotalVisibleMemorySize) { $details.VisibleRamBytes=[long]$cache.OS.TotalVisibleMemorySize*1024 }
        if ($null -ne $cache.OS.FreePhysicalMemory) { $details.FreeRamBytes=[long]$cache.OS.FreePhysicalMemory*1024 }
    }
    Read-Section 'volumes' {
        $details.Volumes=@(Read-SystemCim 'Win32_LogicalDisk' @('DeviceID','DriveType','FileSystem','Size','FreeSpace') | Where-Object {$_.DriveType -eq 3} | ForEach-Object {
            @{Drive=[string]$_.DeviceID;FileSystem=[string]$_.FileSystem;SizeBytes=$_.Size;FreeBytes=$_.FreeSpace}
        })
    }
    Read-Section 'physical' {
        $details.PhysicalDisks=@(Get-PhysicalDisk -ErrorAction Stop | ForEach-Object {
            @{Name=[string]$_.FriendlyName;Health=[string]$_.HealthStatus;OperationalStatus=(@($_.OperationalStatus) -join ', ');SizeBytes=$_.Size}
        })
    }
    Read-Section 'updates' {
        $rows=@(Read-SystemCim 'Win32_QuickFixEngineering' @('HotFixID','Description','InstalledOn') | ForEach-Object {
            $date=[datetime]::MinValue
            $raw=[string]$_.InstalledOn
            $known=[datetime]::TryParse($raw,[ref]$date)
            @{Id=[string]$_.HotFixID;Description=[string]$_.Description;InstalledOn=$(if ($known) {$date.ToString('yyyy-MM-dd')} else {$raw});SortDate=$date}
        })
        $details.Updates=@($rows | Sort-Object SortDate -Descending | Select-Object -First 20 | ForEach-Object {
            @{Id=$_.Id;Description=$_.Description;InstalledOn=$_.InstalledOn}
        })
    }
    Read-Section 'restart' {
        $state=Read-RestartIndicators
        $details.RebootPending=[bool]$state.Pending; $details.RebootReasons=@($state.Reasons)
    }
    return $details
}
function Get-RequestPolicy([string]$action) {
    switch ($action) {
        'Inspect' { return @{RequiresSession=$true;RequiresPinnedMachine=$false} }
        {$_ -in @('Apply','RestorePreview','Restore')} { return @{RequiresSession=$true;RequiresPinnedMachine=$true} }
        {$_ -in @('ComputerInfo','ConnectionInfo','SystemInfo')} { return @{RequiresSession=$false;RequiresPinnedMachine=$false} }
        default { throw 'Неизвестная операция.' }
    }
}
function Assert-RemotingContext {
    if (-not (Get-Variable PSSenderInfo -ErrorAction SilentlyContinue) -or $null -eq $PSSenderInfo) {
        throw 'Помощник разрешён только в удалённой WinRM-сессии. Локальный запуск запрещён.'
    }
}
function Get-RemoteMachineIdentity([string]$localMachineId) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Нужны административные права на удалённом компьютере.' }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try { $k=$base.OpenSubKey('SOFTWARE\Microsoft\Cryptography'); try { $machine=[string]$k.GetValue('MachineGuid') } finally { if ($k) { $k.Dispose() } } } finally { $base.Dispose() }
    if (-not $machine -or -not $localMachineId -or $machine -eq $r.LocalMachineId) { throw 'Локальный компьютер администратора исключён из обслуживания (MachineGuid совпадает или недоступен).' }
    return $machine
}
function Get-AdapterValue($item,[string]$name) {
    if ($null -eq $item) { return $null }
    if ($item -is [Collections.IDictionary]) { return $item[$name] }
    $property=$item.PSObject.Properties[$name]
    if ($property) { return $property.Value }
    return $null
}
function Select-LanIPv4([object[]]$adapters,[object[]]$metadata) {
    $choices=@()
    foreach ($adapter in $adapters) {
        $index=Get-AdapterValue $adapter 'InterfaceIndex'
        $meta=@($metadata | Where-Object {(Get-AdapterValue $_ 'InterfaceIndex') -eq $index} | Select-Object -First 1)
        if ($meta.Count -eq 0) { continue }
        $item=$meta[0]
        $description=@('Name','Description','NetConnectionID','ServiceName' | ForEach-Object {[string](Get-AdapterValue $item $_)}) -join ' '
        $pnp=[string](Get-AdapterValue $item 'PNPDeviceID')
        # Some VPN drivers report PhysicalAdapter=true; use driver identity as well.
        if ($description -match '(?i)Radmin|VPN|WireGuard|OpenVPN|ZeroTier|Hamachi|VirtualBox|VMware|Hyper-V|Virtual|(^|\s)(TAP|TUN)(\s|$)' -or $pnp -match '(?i)^(ROOT|SWD|SW)\\') { continue }
        if (-not ((Get-AdapterValue $item 'PhysicalAdapter') -eq $true -or $pnp -match '(?i)^(PCI|USB|ACPI)\\')) { continue }
        $addresses=@(Get-AdapterValue $adapter 'IPAddress' | ForEach-Object {Normalize-IPv4 ([string]$_)} | Where-Object {$_ -and $_ -notmatch '^169\.254\.'} | Select-Object -Unique)
        if ($addresses.Count -eq 0) { continue }
        $gateway=@(Get-AdapterValue $adapter 'DefaultIPGateway' | ForEach-Object {Normalize-IPv4 ([string]$_)} | Where-Object {$_}).Count -gt 0
        $metric=Get-AdapterValue $adapter 'IPConnectionMetric'
        if ($null -eq $metric) { $metric=[int]::MaxValue }
        $choices+=@{Addresses=$addresses;GatewayScore=$(if ($gateway) {0} else {1});Metric=[long]$metric}
    }
    if ($choices.Count -eq 0) { return @{Address='';Reason='Нет однозначного аппаратного LAN/Wi-Fi IPv4; VPN-адрес не подставлен.'} }
    $ordered=@($choices | Sort-Object GatewayScore,Metric)
    $best=@($ordered | Where-Object {$_.GatewayScore -eq $ordered[0].GatewayScore -and $_.Metric -eq $ordered[0].Metric})
    $addresses=@($best | ForEach-Object {$_.Addresses} | Select-Object -Unique)
    if ($addresses.Count -ne 1) { return @{Address='';Reason='Несколько равноправных LAN IPv4; адрес без подтверждённого соединения не выбран.'} }
    return @{Address=[string]$addresses[0];Reason='Основной аппаратный LAN/Wi-Fi IPv4'}
}
function Get-ComputerInformation {
        $result.SystemInfo=Get-SystemInformation @('computer','performance')
        try {
            $os=Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
            $result.Windows=[string]$os.Caption
        } catch { $result.Errors+="Windows: $($_.Exception.Message)" }
        # Registry values are optional on older Windows; do not discard a known caption.
        try {
            $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion')
            try {
                $release=[string]$key.GetValue('DisplayVersion', $key.GetValue('ReleaseId',''))
                $build=[string]$key.GetValue('CurrentBuildNumber','')
                $ubr=[string]$key.GetValue('UBR','')
                $result.WindowsVersion="$release, сборка $build.$ubr".Trim(', ')
            } finally { if ($key) { $key.Dispose() } }
        } catch { $result.Errors+="Версия Windows: $($_.Exception.Message)" }
        try {
            $activeAdapters=@(Get-CimInstance Win32_NetworkAdapter -Filter 'NetConnectionStatus=2' -ErrorAction Stop)
            $active=@($activeAdapters | ForEach-Object { $_.InterfaceIndex })
            $physical=@($activeAdapters | Where-Object {$_.PhysicalAdapter -eq $true} | ForEach-Object {$_.InterfaceIndex})
            $adapters=@(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -ErrorAction Stop | Where-Object { $_.InterfaceIndex -in $active })
            $ordered=@($adapters | Sort-Object @{Expression={if ($_.InterfaceIndex -in $physical) {0} else {1}}}, @{Expression={if ($_.DefaultIPGateway) {0} else {1}}}, InterfaceIndex)
            $result.Addresses=@(@(foreach ($a in $ordered) { foreach ($ip in @($a.IPAddress)) {
                $parsed=$null
                if ([Net.IPAddress]::TryParse($ip,[ref]$parsed) -and -not [Net.IPAddress]::IsLoopback($parsed) -and $ip -notmatch '^(169\.254\.|fe80:|0\.|::($|%))') { $ip }
            } }) | Select-Object -Unique)
            $selection=Select-LanIPv4 @($adapters) @($activeAdapters)
            $result.PrimaryIPv4=[string]$selection.Address
            if (-not $result.PrimaryIPv4) { $result.Errors+="IPv4: $($selection.Reason)" }
        } catch { $result.Errors+="IP: $($_.Exception.Message)" }
        $rdpAddresses=@(Read-RdpConnection)
        $result.Addresses=@(@($result.Addresses) + @($rdpAddresses) | Select-Object -Unique)
        $officeErrors=@(); $products=@()
        foreach ($view in @('Registry64','Registry32')) {
            $h=$null
            try {
                $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::$view)
                $c=$h.OpenSubKey('SOFTWARE\Microsoft\Office\ClickToRun\Configuration')
                if ($c) { try {
                    $ids=[string]$c.GetValue('ProductReleaseIds',''); $ver=[string]$c.GetValue('VersionToReport',''); $platform=[string]$c.GetValue('Platform','')
                    $bits=if ($platform -eq 'x64') {'64 бит'} elseif ($platform -eq 'x86') {'32 бит'} else {'разрядность не определена'}
                    foreach ($id in @($ids -split ',' | Where-Object {$_})) { $products+="Microsoft Office $id, версия $ver, $bits (Click-to-Run)" }
                    if (-not $ids) { $officeErrors+='Click-to-Run: отсутствует ProductReleaseIds' }
                } finally { $c.Dispose() } }
                $u=$h.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                if (-not $u) { throw 'Раздел установленных программ недоступен' }
                try { foreach ($name in $u.GetSubKeyNames()) {
                    $k=$u.OpenSubKey($name)
                    if (-not $k) { throw "Не прочитан продукт $name" }
                    try {
                        $title=[string]$k.GetValue('DisplayName','')
                        if ($title -match '(?i)Microsoft.*(Office|365|Visio|Project|Outlook|Access)' -and [int]$k.GetValue('WindowsInstaller',0) -eq 1) {
                            $version=[string]$k.GetValue('DisplayVersion','')
                            $bits=if ($view -eq 'Registry64' -and [Environment]::Is64BitOperatingSystem) {'64 бит'} else {'32 бит'}
                            $products+="$title, версия $version, $bits (MSI)"
                        }
                    } finally { $k.Dispose() }
                } } finally { $u.Dispose() }
            } catch { $officeErrors+="$view : $($_.Exception.Message)" } finally { if ($h) { $h.Dispose() } }
        }
        $result.Office=@($products | Select-Object -Unique); $result.OfficeKnown=($officeErrors.Count -eq 0)
        $result.Errors+=@($officeErrors); $result.Completed=$true; $result.Code=0
        ConvertTo-Json -InputObject $result -Depth 18 -Compress
        return

}
try {
    Assert-RemotingContext
    $r = $RequestJson | ConvertFrom-Json
    $allowed = @('Action','Computer','LocalMachineId','SessionId','User','Domain','Sid','MachineId','Versions','CloseOutlook','BackupId','OperationId','ClientIPv4')
    foreach ($p in $r.PSObject.Properties.Name) { if ($p -notin $allowed) { throw "Неизвестный параметр: $p" } }
    $policy=Get-RequestPolicy ([string]$r.Action)
    if ($policy.RequiresSession -and [int]$r.SessionId -le 0) { throw 'Некорректный ID сеанса.' }
    $machine=Get-RemoteMachineIdentity ([string]$r.LocalMachineId)
    $result.OperationId=if ($r.Action -eq 'Restore') { [string]$r.BackupId } else { [string]$r.OperationId }
    $result.MachineId=$machine
    if ($policy.RequiresPinnedMachine -and $machine -ne $r.MachineId) { throw 'Изменилась идентичность целевого компьютера. Откройте диалог заново.' }
    if ($r.Action -eq 'SystemInfo') {
        $result.SystemInfo=Get-SystemInformation
        foreach ($messages in $result.SystemInfo.SectionErrors.Values) { $result.Errors+=@($messages | Where-Object {$_}) }
        $result.Code=0; $result.Completed=$true
        $result.Message=if ($result.Errors.Count -eq 0) {'Сведения прочитаны.'} else {'Часть сведений недоступна. См. ошибки разделов.'}
        ConvertTo-Json -InputObject $result -Depth 18 -Compress
        return
    }
    if ($r.Action -eq 'ConnectionInfo') {
        $result.Addresses=@(Read-RdpConnection)
        $result.Code=0; $result.Completed=$true
        ConvertTo-Json -InputObject $result -Depth 18 -Compress
        return
    }
    if ($r.Action -eq 'ComputerInfo') { Get-ComputerInformation; return }
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
    function Resolve-OutlookVersions([object[]]$evidence,[string[]]$problems) {
        $issues=@($problems)
        $versions=@()
        foreach ($item in $evidence) {
            $major=0
            if (-not [int]::TryParse(([string]$item.FileVersion -split '\.')[0],[ref]$major) -or $major -notin @(15,16)) {
                $issues+="Не поддерживается или неизвестна версия OUTLOOK.EXE: $($item.FileVersion) ($($item.Path))"; continue
            }
            $branch="$major.0"
            if ($branch -ne $item.RegistryBranch) { $issues+="Противоречие версии файла $($item.FileVersion) и установленной ветки $($item.RegistryBranch): $($item.Path)"; continue }
            $versions+=$branch
        }
        $versions=@($versions | Sort-Object -Unique)
        if ($issues.Count -eq 0 -and $versions.Count -eq 0) { $issues+='Классический Outlook 15.0/16.0 не найден. Реестр не изменён.' }
        return @{ Versions=$versions; Error=($issues -join '; ') }
    }
    function Assert-AutomaticVersions($detected,[string[]]$confirmed) {
        if ($detected.Error -or @($detected.Versions).Count -eq 0) { throw "Нельзя однозначно определить установленный классический Outlook: $($detected.Error)" }
        if ((@($confirmed | Sort-Object -Unique) -join ',') -ne (@($detected.Versions | Sort-Object -Unique) -join ',')) {
            throw 'Установленные версии Outlook изменились после подтверждения. Откройте обслуживание заново.'
        }
        return @($detected.Versions)
    }
    function Get-OutlookDetection {
        $evidence=@(); $problems=@()
        foreach ($view in @('Registry64','Registry32')) {
            $h=$null
            try {
                $h=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::$view)
                foreach ($v in @('16.0','15.0','14.0')) {
                    $k=$h.OpenSubKey("SOFTWARE\Microsoft\Office\$v\Outlook\InstallRoot")
                    if ($k) {
                        try {
                            $path=[string]$k.GetValue('Path')
                            if (-not $path) { $problems+="Outlook $v ($view): InstallRoot не содержит Path"; continue }
                            $exe=Join-Path $path 'OUTLOOK.EXE'
                            if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { $problems+="Outlook $v ($view): не найден $exe"; continue }
                            $file=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
                            $evidence+=@{ Path=$exe; RegistryBranch=$v; FileVersion="$($file.FileMajorPart).$($file.FileMinorPart).$($file.FileBuildPart).$($file.FilePrivatePart)" }
                        } finally { $k.Dispose() }
                    }
                }
                foreach ($config in @('SOFTWARE\Microsoft\Office\ClickToRun\Configuration','SOFTWARE\Microsoft\Office\15.0\ClickToRun\Configuration','SOFTWARE\Microsoft\Office\16.0\ClickToRun\Configuration')) {
                    $k=$h.OpenSubKey($config)
                    if (-not $k) { continue }
                    try {
                        $products=[string]$k.GetValue('ProductReleaseIds','')
                        $excluded=[string]$k.GetValue('ExcludedApps','')
                        if ($products -notmatch '(?i)(Outlook|O365ProPlus|O365Business|ProPlus|Professional|Standard|HomeBusiness|Mondo)' -or $excluded -match '(?i)(^|[,;\s])Outlook($|[,;\s])') { continue }
                        $version=[string]$k.GetValue('VersionToReport',$k.GetValue('ClientVersionToReport',''))
                        $install=[string]$k.GetValue('InstallationPath','')
                        $major=0
                        if (-not [int]::TryParse(($version -split '\.')[0],[ref]$major) -or $major -notin @(15,16) -or -not $install) {
                            $problems+="Click-to-Run ($view): отсутствует или не поддерживается версия/путь Outlook ($version)"; continue
                        }
                        $found=$false
                        foreach ($suffix in @("root\Office$major\OUTLOOK.EXE","Office$major\OUTLOOK.EXE",'OUTLOOK.EXE')) {
                            $exe=Join-Path $install $suffix
                            if (Test-Path -LiteralPath $exe -PathType Leaf) {
                                $file=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
                                $evidence+=@{ Path=$exe; RegistryBranch="$major.0"; FileVersion="$($file.FileMajorPart).$($file.FileMinorPart).$($file.FileBuildPart).$($file.FilePrivatePart)" }
                                $found=$true
                            }
                        }
                        if (-not $found) { $problems+="Click-to-Run ($view): установленный продукт $products сообщает $version, но классический OUTLOOK.EXE не найден" }
                    } finally { $k.Dispose() }
                }
            } catch { $problems+="Определение Outlook ($view): $($_.Exception.Message)" }
            finally { if ($h) { $h.Dispose() } }
        }
        return Resolve-OutlookVersions @($evidence) @($problems)
    }
    $detected=Get-OutlookDetection
    $result.Versions=@($detected.Versions); $result.OutlookDetectionError=[string]$detected.Error
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
            if (Test-Path -LiteralPath $path) { [IO.File]::Replace($tmp,$path,"$path.previous"); [IO.File]::Delete("$path.previous") } else { [IO.File]::Move($tmp,$path) }
        }
        $desired=@{ Exists=$true; Kind='DWord'; Data='1' }
        if ($r.Action -eq 'Apply') {
            $versions=@(Assert-AutomaticVersions $detected @($r.Versions))
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
            $result.Verified=($result.Failed -eq 0 -and $result.Errors.Count -eq 0)
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
$result.Completed=$true
ConvertTo-Json -InputObject $result -Depth 18 -Compress
