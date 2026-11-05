using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly AddressHistoryStore addressHistory;
    private bool updatingHistory;
    private void InitializeAddressHistory()
    {
        try { SetAddressChoices(addressHistory.Load()); }
        catch (Exception ex) { Trace("Чтение истории адресов", "", ex.Message, ex.HResult); }
    }
    private void SetAddressChoices(string[] choices)
    {
        var text = host.Text;
        int cursor = host.SelectionStart;
        updatingHistory = true;
        try
        {
            host.BeginUpdate();
            host.Items.Clear(); host.Items.AddRange(choices);
            host.Text = text;
            host.SelectionStart = Math.Min(cursor, host.Text.Length);
            host.SelectionLength = 0;
        }
        finally { host.EndUpdate(); updatingHistory = false; }
    }
    private void RememberAddress(string computer)
    {
        var entries = new List<string?> { computer };
        entries.AddRange(host.Items.Cast<string>());
        // Merge other instances' recent entries when possible; do not let storage errors block WTS.
        try { entries.AddRange(addressHistory.Load()); }
        catch (Exception ex) { Trace("Чтение истории адресов", computer, ex.Message, ex.HResult); }
        var choices = AddressHistoryStore.Normalize(entries);
        SetAddressChoices(choices);
        try { addressHistory.Save(choices); }
        catch (Exception ex) { Trace("Сохранение истории адресов", computer, ex.Message, ex.HResult); }
    }
    internal static void CheckAddressHistoryUi(string output)
    {
        var folder = Path.Combine(output, "history-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "addresses.json");
        try
        {
            using (var first = new MainForm(history: new AddressHistoryStore(path)))
            {
                first.host.Text = "PC-01"; first.RememberAddress("PC-01");
                first.host.Text = "192.0.2.10"; first.RememberAddress("192.0.2.10");
            }
            using var second = new MainForm(history: new AddressHistoryStore(path));
            second.Show(); Application.DoEvents();
            void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
            Check(second.host.Items.Cast<string>().SequenceEqual(new[] { "192.0.2.10", "PC-01" }), "History did not survive a new form instance");
            Check(second.host.DropDownStyle == ComboBoxStyle.DropDown && second.host.AutoCompleteMode == AutoCompleteMode.SuggestAppend
                && second.host.AutoCompleteSource == AutoCompleteSource.ListItems, "Editable list/autocomplete missing");
            second.host.Text = "other-pc";
            second.grid.DataSource = new List<Session> { new(12, "user", "DOMAIN", "rdp-tcp", 0) };
            second.host.SelectedItem = "PC-01";
            Check(second.host.Text == "PC-01" && second.grid.DataSource == null && !second.connect.Enabled, "Choosing a saved PC must clear stale sessions");
            second.grid.DataSource = new List<Session> { new(12, "user", "DOMAIN", "rdp-tcp", 0) };
            var sessions = second.grid.DataSource;
            second.RememberAddress("PC-01");
            Check(ReferenceEquals(second.grid.DataSource, sessions) && second.host.Text == "PC-01", "Refreshing history must preserve unchanged target/session data");
            Check(second.host.Items.Cast<string>().SequenceEqual(new[] { "PC-01", "192.0.2.10" }), "History MRU/duplicate handling");
            second.Close();
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
