using System.IO;
using System.Net;
using System.Text.Json;

namespace PointerBridge_Client;

internal sealed class ClientSettingsStore
{
    private readonly string _filePath;

    public ClientSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PointerBridge", "Client", "settings.json");
    }

    public string? LoadLastUdpIp()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_filePath));
            return TryNormalize(settings?.LastUdpIp, out string? ip) ? ip : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 首次运行、配置损坏或无读取权限时使用空地址，不影响启动。
            return null;
        }
    }

    public bool TrySaveLastUdpIp(string ip, out string? error)
    {
        error = null;
        if (!TryNormalize(ip, out string? normalized))
        {
            error = "IP 地址无效。";
            return false;
        }

        string temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Settings { LastUdpIp = normalized }));
            // 同目录临时文件写完后再替换，避免写入中断破坏原有配置。
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (!IPAddress.TryParse(value, out IPAddress? address)
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        normalized = address.ToString();
        return true;
    }

    private sealed class Settings
    {
        public string? LastUdpIp { get; set; }
    }
}
