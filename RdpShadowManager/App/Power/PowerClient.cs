using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed record PowerWorkerRequest(string Computer, string Address, PowerAction Action, int DelaySeconds, string Comment, bool Force);
internal sealed record PowerResult(int Code, string Message);
internal static class PowerClient
{
    private static IEnumerable<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Concat([IPAddress.Loopback, IPAddress.IPv6Loopback]);
    private static string[] LocalNames() => [Environment.MachineName, Dns.GetHostName(), "localhost"];
    internal static void ValidateNativeEndpoint(string machineName)
    {
        if (string.IsNullOrEmpty(machineName) || !machineName.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("Не указана удалённая цель.");
        var addressText = machineName[2..];
        const string suffix = ".ipv6-literal.net";
        if (addressText.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) addressText = addressText[..^suffix.Length].Replace('-', ':');
        if (!IPAddress.TryParse(addressText, out var address)) throw new ArgumentException("Нативный запрос разрешён только на проверенный IP-адрес.");
        var target = PowerTarget.Create(address.ToString(), [address], LocalAddresses(), LocalNames());
        if (!string.Equals(target.MachineName, machineName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Неканоническая цель нативного запроса.");
    }
    public static async Task<PowerTarget> Resolve(string computer)
    {
        computer = Connection.ValidateHost(computer);
        IPAddress[] addresses = IPAddress.TryParse(computer, out var literal) ? [literal] :
            await Dns.GetHostAddressesAsync(computer).WaitAsync(TimeSpan.FromSeconds(10));
        return PowerTarget.Create(computer, addresses, LocalAddresses(), LocalNames());
    }
    public static PowerResult Worker(PowerWorkerRequest request, IPowerNative? native = null)
    {
        // Revalidate against this computer's current interfaces immediately before native dispatch.
        var target = PowerTarget.Create(request.Computer, [IPAddress.Parse(request.Address)], LocalAddresses(), LocalNames());
        var command = new PowerRequest(target, request.Action, request.DelaySeconds, request.Comment, request.Force);
        int code = PowerOperations.Execute(command, native ?? new WindowsPowerNative());
        return new(code, code == 0 ? PowerOperations.Accepted(command) : PowerOperations.ErrorMessage(code) + $" Системный код {code}: {new Win32Exception(code).Message}");
    }
    public static async Task<PowerResult> Send(PowerRequest request)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--power-worker");
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить процесс запроса питания.");
        var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var data = new PowerWorkerRequest(request.Target.Computer, request.Target.Address, request.Action, request.DelaySeconds, request.Comment, request.Force);
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), deadline.Token);
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(deadline.Token);
            var result = JsonSerializer.Deserialize<PowerResult>(await output) ?? throw new InvalidOperationException("Пустой ответ процесса питания.");
            if (worker.ExitCode != (result.Code == 0 ? 0 : 1)) throw new InvalidOperationException($"Неверный результат процесса питания: {await error}");
            return result;
        }
        catch (OperationCanceledException)
        { throw new TimeoutException("Ответ на запрос питания не получен за 20 секунд. Действие могло быть запланировано; проверьте цель или явно запросите отмену. Автоматического повтора нет."); }
        finally
        {
            if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
    }
}
internal sealed class WindowsPowerNative : IPowerNative
{
    [DllImport("advapi32.dll", EntryPoint = "InitiateSystemShutdownExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownEx(string machineName, string message, uint timeout,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed, [MarshalAs(UnmanagedType.Bool)] bool reboot, uint reason);
    [DllImport("advapi32.dll", EntryPoint = "AbortSystemShutdownW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortSystemShutdown(string machineName);
    private static void RequireRemote(string machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName) || !machineName.StartsWith("\\\\", StringComparison.Ordinal) || machineName.Length <= 2)
            throw new ArgumentException("Пустая или локальная цель запрещена.");
        PowerClient.ValidateNativeEndpoint(machineName);
    }
    public int Initiate(NativeShutdownRequest request)
    {
        RequireRemote(request.MachineName);
        return InitiateSystemShutdownEx(request.MachineName, request.Comment, request.DelaySeconds, request.ForceAppsClosed, request.Reboot, request.Reason) ? 0 : LastError();
    }
    public int Abort(string machineName)
    {
        RequireRemote(machineName);
        return AbortSystemShutdown(machineName) ? 0 : LastError();
    }
    private static int LastError() { var code = Marshal.GetLastWin32Error(); return code == 0 ? 31 : code; }
}
