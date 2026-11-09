using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RdpShadowManager.Core;

public sealed record PowerShellStreams(string[] ServiceMessages, string[] Errors);
public static class PowerShellDiagnostics
{
    public static PowerShellStreams Analyze(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new([], []);
        List<string> service = []; List<string> errors = [];
        foreach (var block in Regex.Split(raw.TrimStart('\uFEFF'), @"(?m)^[\t ]*#< CLIXML[\t ]*\r?$"))
        {
            var text = block.Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith("<Objs", StringComparison.Ordinal))
            {
                try
                {
                    int closing = text.IndexOf("</Objs>", StringComparison.Ordinal);
                    var xml = closing < 0 ? text : text[..(closing + 7)];
                    var tail = closing < 0 ? "" : text[(closing + 7)..];
                    using var input = new StringReader(xml);
                    using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
                    var root = XDocument.Load(reader).Root;
                    if (root?.Name.LocalName != "Objs") throw new XmlException("Unknown PowerShell XML root");
                    foreach (var item in root.Elements())
                    {
                        var stream = (string?)item.Attribute("S") ?? "";
                        var message = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "S"
                            && (string?)e.Attribute("N") is "Message" or "StatusDescription" or "ToString")?.Value ?? item.Value;
                        message = Decode(message);
                        if (new[] { "progress", "verbose", "debug", "information", "warning" }.Contains(stream, StringComparer.OrdinalIgnoreCase))
                            service.Add($"{stream}: {message}");
                        else if (stream.Equals("Error", StringComparison.OrdinalIgnoreCase))
                            errors.Add(message.Length == 0 ? item.ToString(SaveOptions.DisableFormatting) : message);
                        else errors.Add("Неизвестный поток PowerShell: " + item.ToString(SaveOptions.DisableFormatting));
                    }
                    ClassifyPlain(tail, service, errors);
                    continue;
                }
                catch (Exception ex) when (ex is XmlException or InvalidOperationException)
                {
                    errors.Add("Не удалось разобрать служебный XML PowerShell: " + text);
                    continue;
                }
            }
            ClassifyPlain(text, service, errors);
        }
        return new(service.ToArray(), errors.ToArray());
    }
    private static void ClassifyPlain(string text, List<string> service, List<string> errors)
    {
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (Regex.IsMatch(line, @"\A(VERBOSE|DEBUG|WARNING|INFORMATION|PROGRESS|ПОДРОБНО|ОТЛАДКА|ПРЕДУПРЕЖДЕНИЕ|ИНФОРМАЦИЯ):", RegexOptions.IgnoreCase)
                || line is "Preparing modules for first use." or "Подготовка модулей к первому использованию.")
                service.Add(line);
            else errors.Add(line);
        }
    }
    private static string Decode(string value) => Regex.Replace(value, "_x([0-9a-fA-F]{4})_",
        m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
