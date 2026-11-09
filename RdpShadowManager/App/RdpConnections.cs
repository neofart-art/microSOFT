using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed record RdpClientProcess(int Id, DateTime Started);
internal static class RdpConnections
{
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
                if (Marshal.ReadInt32(row) != 5) continue; // MIB_TCP_STATE_ESTAB only
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
            _ = WatchRdpAddress(computer);
        }
        catch (Exception ex) { Trace("Адрес подключения mstsc", computer, ex.Message, ex.HResult); }
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
            string[] dns = [];
            try { dns = (await InfoDns(computer, cancellation.Token)).Select(ip => ip.ToString()).ToArray(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Trace("IPv4 / DNS", computer, ex.Message, ex.HResult); }
            while (!cancellation.IsCancellationRequested && !IsDisposed && !closing
                && host.Text.Trim().Equals(computer, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = await InfoConnectedIPv4(computer, ComputerIPv4.Addresses(currentRemoteAddresses.Concat(dns)), cancellation.Token);
                if (!string.IsNullOrEmpty(ComputerIPv4.Normalize(candidate)))
                {
                    if (!IsDisposed && !closing && host.Text.Trim().Equals(computer, StringComparison.OrdinalIgnoreCase))
                        ShowComputerIPv4(new(candidate!, "Установленное TCP-подключение mstsc"));
                    return;
                }
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) Trace("IPv4 подключения", computer, ex.Message, ex.HResult); }
        finally { if (ReferenceEquals(rdpPolling, cancellation)) rdpPolling = null; cancellation.Dispose(); }
    }
}
