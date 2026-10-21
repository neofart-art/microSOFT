using System.Net;
using System.Drawing.Imaging;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal static class PowerSmoke
{
    public static void Run(string output)
    {
        var fake = new NeverNative();
        foreach (var action in new[] { PowerAction.Restart, PowerAction.Shutdown, PowerAction.Abort })
        foreach (var address in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "0.0.0.0" })
        {
            try
            {
                PowerClient.Worker(new("remote-alias", address, action, 60, "test", false), fake);
                throw new InvalidOperationException("Worker accepted a local power target");
            }
            catch (ArgumentException) { }
        }
        foreach (var name in new[] { "", "localhost", ".", @"\\PC", "PC /f" })
        {
            try
            {
                PowerClient.Worker(new(name, "198.51.100.42", PowerAction.Shutdown, 60, "test", false), fake);
                throw new InvalidOperationException("Worker accepted an invalid power target");
            }
            catch (ArgumentException) { }
        }
        foreach (var endpoint in new[] { "", @"\\", @"\\127.0.0.1", @"\\--1.ipv6-literal.net", @"\\localhost" })
        {
            try { PowerClient.ValidateNativeEndpoint(endpoint); throw new InvalidOperationException("Native guard accepted an unsafe endpoint"); }
            catch (ArgumentException) { }
        }
        var target = PowerTarget.Create("TEST-PC", [IPAddress.Parse("198.51.100.42")], [IPAddress.Loopback], []);
        foreach (var action in new[] { PowerAction.Restart, PowerAction.Shutdown, PowerAction.Abort })
        {
            using var dialog = new PowerDialog(target, action);
            if (dialog.Force || dialog.DelaySeconds != 60 || dialog.AcceptButton != null || dialog.Request.Target != target)
                throw new InvalidOperationException("Unsafe power confirmation defaults");
            if (action != PowerAction.Abort && dialog.Request.Build().ForceAppsClosed)
                throw new InvalidOperationException("Default power request forces apps closed");
            if (action == PowerAction.Restart)
            {
                dialog.Show(); Application.DoEvents();
                using var image = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(image, dialog.ClientRectangle);
                image.Save(Path.Combine(output, "power-confirmation.png"), ImageFormat.Png);
                dialog.Close();
            }
        }
        File.WriteAllText(Path.Combine(output, "power-guards.txt"), "PASS: worker local/invalid targets rejected for restart/shutdown/abort; native endpoint guard; confirmation safe defaults. All tests inject a fail-on-call native adapter; no Windows power API was called.");
    }
    private sealed class NeverNative : IPowerNative
    {
        public int Initiate(NativeShutdownRequest _) => throw new InvalidOperationException("Unexpected power API dispatch");
        public int Abort(string _) => throw new InvalidOperationException("Unexpected abort API dispatch");
    }
}
