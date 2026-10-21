using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // The same portable executable doubles as a killable WTS worker.
        if (args.Length == 2 && args[0] == "--query")
        {
            QueryResult result;
            try { result = Wts.Query(Connection.ValidateHost(args[1])); }
            catch (Exception ex) { result = new([], [], ex is Win32Exception w ? w.NativeErrorCode : ex.HResult, ex.Message); }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
            using var output = Console.OpenStandardOutput();
            output.Write(bytes);
            return 0;
        }
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--ui-smoke")
        {
            try { UiSmoke.Run(args[1]); return 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); return 1; }
        }
        StartupState? restored = null;
        if (args.Length != 0)
        {
            try
            {
                if (args.Length != 2 || args[0] != "--resume-state")
                    throw new ArgumentException("Неизвестные аргументы запуска.");
                restored = StartupState.Decode(args[1]);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "RDP Shadow Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
        Application.Run(new MainForm(restored));
        return 0;
    }
}
internal static class Wts
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry { public int Id; public IntPtr Name; public int State; }
    [DllImport("wtsapi32.dll", EntryPoint="WTSOpenServerW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr Open(string server);
    [DllImport("wtsapi32.dll", EntryPoint="WTSEnumerateSessionsW", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Enumerate(IntPtr server, int reserved, int version, out IntPtr data, out int count);
    [DllImport("wtsapi32.dll", EntryPoint="WTSQuerySessionInformationW", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Info(IntPtr server, int id, int type, out IntPtr data, out int bytes);
    [DllImport("wtsapi32.dll", EntryPoint="WTSFreeMemory")] private static extern void Free(IntPtr data);
    [DllImport("wtsapi32.dll", EntryPoint="WTSCloseServer")] private static extern void Close(IntPtr server);
    public static QueryResult Query(string host)
    {
        var server = Open(host);
        if (server == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!Enumerate(server, 0, 1, out var data, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                List<Session> sessions = []; List<string> diagnostics = [];
                string Read(int id, int type)
                {
                    if (!Info(server, id, type, out var buffer, out _))
                    {
                        var code = Marshal.GetLastWin32Error();
                        diagnostics.Add($"WTSQuerySessionInformation: ID={id}, поле={type}, код={code}: {new Win32Exception(code).Message}");
                        return "";
                    }
                    try { return Marshal.PtrToStringUni(buffer) ?? ""; }
                    finally { if (buffer != IntPtr.Zero) Free(buffer); }
                }
                for (int i=0; i<count; i++)
                {
                    var entry = Marshal.PtrToStructure<Entry>(IntPtr.Add(data, i * Marshal.SizeOf<Entry>()));
                    if (entry.Id <= 0) continue;
                    var user = Read(entry.Id, 5);
                    if (user.Length == 0) continue;
                    sessions.Add(new(entry.Id, user, Read(entry.Id, 7), Marshal.PtrToStringUni(entry.Name) ?? "", entry.State));
                }
                return new(sessions, diagnostics);
            }
            finally { if (data != IntPtr.Zero) Free(data); }
        }
        finally { Close(server); }
    }
}
