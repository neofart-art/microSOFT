namespace RdpShadowManager;
internal sealed class SystemInfoData
{
    public string CapturedAt { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Windows { get; set; } = "";
    public string LastBoot { get; set; } = "";
    public double? UptimeSeconds { get; set; }
    public long? InstalledRamBytes { get; set; }
    public long? VisibleRamBytes { get; set; }
    public long? FreeRamBytes { get; set; }
    public CpuInfo[] Cpus { get; set; } = [];
    public VolumeInfo[] Volumes { get; set; } = [];
    public PhysicalDiskInfo[] PhysicalDisks { get; set; } = [];
    public BitLockerInfo[] BitLocker { get; set; } = [];
    public UpdateInfo[] Updates { get; set; } = [];
    public bool? RebootPending { get; set; }
    public string[] RebootReasons { get; set; } = [];
    public Dictionary<string, string> Sections { get; set; } = [];
    public Dictionary<string, string[]> SectionErrors { get; set; } = [];
}
internal sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public int? Cores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? LoadPercent { get; set; }
}
internal sealed class VolumeInfo
{
    public string Drive { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long? SizeBytes { get; set; }
    public long? FreeBytes { get; set; }
}
internal sealed class PhysicalDiskInfo
{
    public string Name { get; set; } = "";
    public string Health { get; set; } = "";
    public string OperationalStatus { get; set; } = "";
    public long? SizeBytes { get; set; }
}
internal sealed class BitLockerInfo
{
    public string Drive { get; set; } = "";
    public int? ProtectionStatus { get; set; }
    public int? ConversionStatus { get; set; }
    public int? EncryptionPercent { get; set; }
    public int? LockStatus { get; set; }
}
internal sealed class UpdateInfo
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string InstalledOn { get; set; } = "";
}
