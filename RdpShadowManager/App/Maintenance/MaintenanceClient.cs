using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed record MaintenanceRequest(string Action, string Computer, string LocalMachineId, int SessionId,
    string User, string Domain, string Sid = "", string MachineId = "", string[]? Versions = null,
    bool CloseOutlook = false, string BackupId = "", string OperationId = "");
internal sealed class MaintenanceResult
{
    public int Code { get; set; } = 1;
    public string Computer { get; set; } = "";
    public string User { get; set; } = "";
    public string Sid { get; set; } = "";
    public int SessionId { get; set; }
    public string MachineId { get; set; } = "";
    public string[] Versions { get; set; } = [];
    public BackupChoice[] Backups { get; set; } = [];
    public JsonElement[] Conflicts { get; set; } = [];
    public JsonElement[] Changed { get; set; } = [];
    public string[] Errors { get; set; } = [];
    public int Success { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string BackupPath { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Report() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
internal sealed class BackupChoice
{
    public string Id { get; set; } = "";
    public string Time { get; set; } = "";
    public string[] Versions { get; set; } = [];
    public string Path { get; set; } = "";
    public override string ToString() => $"{Time} | Office {string.Join(", ", Versions)} | {Id}";
}
internal static class MaintenanceClient
{
    public static string LocalMachineId()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("Не удалось прочитать локальный MachineGuid. Обслуживание заблокировано.");
    }
    public static async Task<(MaintenanceResult Result, int ExitCode, string StandardError)> Run(MaintenanceRequest request)
    {
        Connection.ValidateHost(request.Computer);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("RdpShadowManager.Maintenance.RemoteMaintenance.ps1")
            ?? throw new InvalidOperationException("Удалённый помощник отсутствует в сборке.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var helper = Convert.ToBase64String(Encoding.UTF8.GetBytes(await reader.ReadToEndAsync()));
        var input = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        // Both substituted strings are Base64 only. User strings never become PowerShell source.
        var wrapper = $$"""
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            try {
                $json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{input}}'))
                $r=$json | ConvertFrom-Json
                $code=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{helper}}'))
                $options=New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 120000 -CancelTimeout 5000
                $output=Invoke-Command -ComputerName $r.Computer -SessionOption $options -ScriptBlock ([ScriptBlock]::Create($code)) -ArgumentList $json -ErrorAction Stop
                $data=[string]$output
                $parsed=$data | ConvertFrom-Json -ErrorAction Stop
                [Console]::Out.Write($data)
                exit ([int]$parsed.Code)
            } catch {
                $failure=@{ Code=1; Message=$_.Exception.Message; Errors=@($_.ToString()); Failed=1 }
                [Console]::Out.Write(($failure | ConvertTo-Json -Compress -Depth 5))
                exit 1
            }
            """;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Windows PowerShell не найден. Нужен PowerShell 5.1 и настроенный WinRM.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // Only this short bootstrap is on the command line; helper source is streamed as data.
        var bootstrap = "& ([ScriptBlock]::Create([Console]::In.ReadToEnd()))";
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) }) start.ArgumentList.Add(a);
        start.RedirectStandardInput = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить транспорт WinRM.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try
        {
            await process.StandardInput.WriteLineAsync(wrapper.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var output = await outputTask;
            var errors = await errorTask;
            var result = JsonSerializer.Deserialize<MaintenanceResult>(output.TrimStart('\uFEFF'))
                ?? throw new InvalidOperationException("WinRM вернул пустой отчёт.");
            if (result.Code != process.ExitCode) throw new InvalidOperationException("Код завершения не совпадает с отчётом помощника.");
            return (result, process.ExitCode, errors);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Тайм-аут WinRM. Результат удалённой операции неизвестен; не считайте её успешной. ID: {request.OperationId}. Проверьте резервные копии на целевом ПК в %ProgramData%\\RdpShadowManager\\Backups. Удалённый помощник имеет собственный лимит времени и блокировку повторного запуска.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(outputTask, errorTask);
        }
    }
}
