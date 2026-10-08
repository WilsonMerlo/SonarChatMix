using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace SonarChatMix;

sealed class SonarException(string message) : Exception(message);

// Habla con el servidor local que levanta SteelSeries GG / Sonar.
// API no oficial: es la misma que usan steelseries-sonar-py y SteelSeries-NET-API.
sealed class SonarClient : IDisposable
{
    readonly HttpClient http;
    string? webServer; // ej. "http://127.0.0.1:12345" — cambia cada vez que se reinicia GG

    public SonarClient()
    {
        var handler = new HttpClientHandler
        {
            // GG usa un certificado autofirmado. Solo lo aceptamos para localhost.
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
                req.RequestUri is { IsLoopback: true }
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    // Olvida la dirección guardada (por si GG se reinició y cambió de puerto).
    public void Reset() => webServer = null;

    async Task<string> GetWebServerAsync()
    {
        if (webServer is not null) return webServer;

        string coreProps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
        if (!File.Exists(coreProps))
            throw new SonarException("no encuentro SteelSeries GG instalado");

        using var core = JsonDocument.Parse(await File.ReadAllTextAsync(coreProps));
        string gg = core.RootElement.GetProperty("ggEncryptedAddress").GetString()
                    ?? throw new SonarException("coreProps.json sin dirección de GG");

        using var subApps = JsonDocument.Parse(await http.GetStringAsync($"https://{gg}/subApps"));
        var sonar = subApps.RootElement.GetProperty("subApps").GetProperty("sonar");

        if (!sonar.GetProperty("isEnabled").GetBoolean()) throw new SonarException("Sonar está desactivado");
        if (!sonar.GetProperty("isRunning").GetBoolean()) throw new SonarException("Sonar no está corriendo");

        string addr = sonar.GetProperty("metadata").GetProperty("webServerAddress").GetString() ?? "";
        if (addr is "" or "null") throw new SonarException("Sonar todavía no está listo");

        return webServer = addr.TrimEnd('/');
    }

    async Task<JsonDocument> GetAsync(string ruta)
    {
        string web = await GetWebServerAsync();
        return JsonDocument.Parse(await http.GetStringAsync($"{web}/{ruta}"));
    }

    async Task PutAsync(string ruta)
    {
        string web = await GetWebServerAsync();
        using var resp = await http.PutAsync($"{web}/{ruta}", content: null);
        resp.EnsureSuccessStatusCode();
    }

    // InvariantCulture: con la configuración regional argentina 0.3 saldría "0,3" y Sonar lo rechaza.
    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public async Task<double> GetChatMixAsync()
    {
        using var doc = await GetAsync("v1/chatMix");
        var root = doc.RootElement;

        if (root.TryGetProperty("state", out var st) && st.GetString() is string estado && estado != "enabled")
            throw new SonarException(estado == "differentDeviceSelected"
                ? "ChatMix bloqueado: Juego y Chat salen por dispositivos distintos"
                : "ChatMix desactivado en Sonar (" + estado + ")");

        if (root.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number)
            return b.GetDouble();

        throw new SonarException("respuesta inesperada de v1/chatMix: " + root.GetRawText());
    }

    public Task SetChatMixAsync(double value) => PutAsync($"v1/chatMix?balance={Num(value)}");

    // canal: "master", "game", "chatRender", "chatCapture", "media", "aux"
    public async Task<Dictionary<string, (double Volumen, bool Mute)>> GetVolumenesAsync()
    {
        using var doc = await GetAsync("volumeSettings/classic");
        var root = doc.RootElement;
        static (double, bool) Leer(JsonElement e)
        {
            var c = e.GetProperty("classic");
            return (c.GetProperty("volume").GetDouble(), c.GetProperty("muted").GetBoolean());
        }

        var res = new Dictionary<string, (double, bool)> { ["master"] = Leer(root.GetProperty("masters")) };
        foreach (var canal in root.GetProperty("devices").EnumerateObject())
            res[canal.Name] = Leer(canal.Value);
        return res;
    }

    public Task SetVolumenAsync(string canal, double v) => PutAsync($"volumeSettings/classic/{canal}/Volume/{Num(v)}");

    // Ojo: silenciar "master" silencia todas las salidas, y des-silenciarlo las des-silencia TODAS.
    public Task SetMuteAsync(string canal, bool mute) =>
        PutAsync($"volumeSettings/classic/{canal}/Mute/{(mute ? "true" : "false")}");

    public sealed record Dispositivo(string Id, string Nombre, bool EsEntrada);

    // Dispositivos físicos (sin los virtuales de Sonar), en el orden en que los da Sonar.
    public async Task<List<Dispositivo>> GetDispositivosAsync()
    {
        using var doc = await GetAsync("audioDevices");
        return doc.RootElement.EnumerateArray()
            .Where(d => !d.GetProperty("isVad").GetBoolean())
            .Select(d => new Dispositivo(
                d.GetProperty("id").GetString()!,
                d.GetProperty("friendlyName").GetString()!,
                d.GetProperty("dataFlow").GetString() == "capture"))
            .ToList();
    }

    // canal: "game", "chat", "mic", "media", "aux"
    public async Task<string?> GetDispositivoActualAsync(string canal)
    {
        using var doc = await GetAsync("classicRedirections");
        return doc.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetString() == canal)
            .GetProperty("deviceId").GetString();
    }

    public Task SetDispositivoAsync(string canal, string deviceId) =>
        PutAsync($"classicRedirections/{canal}/deviceId/{Uri.EscapeDataString(deviceId)}");

    public void Dispose() => http.Dispose();
}
