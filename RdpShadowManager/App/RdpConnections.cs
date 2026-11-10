using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed record RdpClientProcess(int Id, DateTime Started);
internal static class RdpConnections
{
    public static string[] LocalIPv4Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => ComputerIPv4.Normalize(a.Address.ToString())).Where(a => a != null).Cast<string>().Distinct().ToArray();
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    public static string? EstablishedIPv4(IEnumerable<RdpClientProcess> clients, string[] candidates)
    {
        var allowed = ComputerIPv4.Addresses(candidates).ToHashSet();
        if (allowed.Count == 0) return null;
        var verified = new List<int>();
        foreach (var client in clients.Reverse())
        {
            try
            {
                using var process = Process.GetProcessById(client.Id);
                if (!process.HasExited && process.ProcessName.Equals("mstsc", StringComparison.OrdinalIgnoreCase)
                    && process.StartTime.ToUniversalTime() == client.Started) verified.Add(client.Id);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        if (verified.Count == 0) return null;
        int size = 0;
        uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0); // AF_INET, OWNER_PID_ALL
        if (code != 0 && code != 122) throw new Win32Exception((int)code);
        if (size < 4 || size > 16777216) throw new InvalidOperationException("Некорректный размер таблицы TCP.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            code = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
            if (code != 0) throw new Win32Exception((int)code);
            int count = Marshal.ReadInt32(buffer);
            if (count < 0 || 4L + count * 24L > size) throw new InvalidOperationException("Некорректная таблица TCP.");
            var endpoints = new List<(int Pid, string Address)>();
            for (int i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                if (!ComputerIPv4.IsEstablishedRdpEndpoint(Marshal.ReadInt32(row), Marshal.ReadInt32(row, 16))) continue; // Established target RDP, not a gateway HTTPS connection
                var address = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12))).ToString();
                if (allowed.Contains(address)) endpoints.Add((Marshal.ReadInt32(row, 20), address));
            }
            foreach (int pid in verified)
            {
                var found = endpoints.FirstOrDefault(e => e.Pid == pid);
                if (found.Address != null) return found.Address;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
internal sealed partial class MainForm
{
    private readonly Dictionary<string, List<RdpClientProcess>> rdpClients = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? rdpPolling;
    private string[] currentRemoteAddresses = [];
    internal Func<string, string[], CancellationToken, Task<string?>> InfoConnectedIPv4 = null!;
    private void RegisterRdpClient(string computer, Process process)
    {
        try
        {
            if (!rdpClients.TryGetValue(computer, out var clients)) rdpClients[computer] = clients = [];
            var item = new RdpClientProcess(process.Id, process.StartTime.ToUniversalTime());
            clients.RemoveAll(c => c.Id == item.Id); clients.Add(item);
            if (clients.Count > 16) clients.RemoveRange(0, clients.Count - 16);
            if (rdpClients.Count > 30)
                foreach (var key in rdpClients.Keys.Where(k => !k.Equals(computer, StringComparison.OrdinalIgnoreCase)).Take(rdpClients.Count - 30).ToArray()) rdpClients.Remove(key);
        }
        catch (Exception ex) { Trace("Адрес подключения mstsc", computer, ex.Message, ex.HResult); }
        _ = WatchRdpAddress(computer);
    }
    private Task<string?> ReadConnectedIPv4(string computer, string[] candidates, CancellationToken token)
    {
        RdpClientProcess[] clients = rdpClients.TryGetValue(computer, out var known) ? known.ToArray() : [];
        return Task.Run(() => RdpConnections.EstablishedIPv4(clients, candidates), token);
    }
    private async Task WatchRdpAddress(string computer)
    {
        rdpPolling?.Cancel();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); rdpPolling = cancellation;
        try
        {
            while (!cancellation.IsCancellationRequested && !IsDisposed && !closing
                && host.Text.Trim().Equals(computer, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = await InfoConnectedIPv4(computer, ComputerIPv4.Addresses(currentRemoteAddresses), cancellation.Token);
                if (!string.IsNullOrEmpty(ComputerIPv4.Normalize(candidate)) && currentRemoteAddresses.Contains(candidate))
                {
                    if (!IsDisposed && !closing && host.Text.Trim().Equals(computer, StringComparison.OrdinalIgnoreCase))
                        ShowComputerIPv4(ComputerIPv4.Select(candidate, null, currentRemoteAddresses));
                    return;
                }
                var proof = await MaintenanceClient.Run(new MaintenanceRequest("ConnectionInfo", computer,
                    MaintenanceClient.LocalMachineId(), 0, "", "", ClientIPv4: RdpConnections.LocalIPv4Addresses()), cancellation.Token);
                if (IsDisposed || closing || !host.Text.Trim().Equals(computer, StringComparison.OrdinalIgnoreCase)) return;
                Trace("IPv4 / удалённое RDP-соединение", computer, proof.Result.Report(), proof.ExitCode);
                TracePowerShellStreams("IPv4 / удалённое RDP-соединение", computer, proof.StandardError, proof.ExitCode);
                if (proof.ExitCode == 0 && proof.Result.Completed && proof.Result.RdpIPv4.Length > 0
                    && PowerShellDiagnostics.Analyze(proof.StandardError).Errors.Length == 0)
                {
                    var selected = ComputerIPv4.Select(null, null, proof.Result.Addresses, proof.Result.RdpIPv4);
                    if (selected.Address.Length > 0) { ShowComputerIPv4(selected); return; }
                }
                await Task.Delay(2000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) Trace("IPv4 подключения", computer, ex.Message, ex.HResult); }
        finally { if (ReferenceEquals(rdpPolling, cancellation)) rdpPolling = null; cancellation.Dispose(); }
    }
}
