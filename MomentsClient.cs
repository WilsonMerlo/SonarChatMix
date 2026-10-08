using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SonarChatMix;

// Habla con SteelSeries Moments (el grabador de clips de GG). API no oficial: son las mismas
// llamadas que hace la interfaz de GG (GET para leer, POST con JSON para escribir).
sealed class MomentsClient : IDisposable
{
    // Atajo de Moments "Activar silencio del micrófono" en GG (/keyboardShortcuts).
    const int IdAtajoSilenciarMic = 1;
    const string AtajoVacio = "{\"page\":1,\"type\":2,\"direction\":1}";

    readonly HttpClient http;
    string? gg;       // ej. "127.0.0.1:6327" (servidor principal de GG)
    string? moments;  // ej. "127.0.0.1:49728" — cambia cada vez que se reinicia GG

    public MomentsClient()
    {
        var handler = new HttpClientHandler
        {
            // GG usa un certificado autofirmado. Solo lo aceptamos para localhost.
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
                req.RequestUri is { IsLoopback: true }
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public void Reset() => moments = gg = null;

    public sealed record Estado(bool Habilitado, bool GrabandoEscritorio, bool HayJuego);

    async Task<(string Gg, string Moments)> DireccionesAsync()
    {
        if (gg is not null && moments is not null) return (gg, moments);

        string coreProps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
        using var core = JsonDocument.Parse(await File.ReadAllTextAsync(coreProps));
        string dirGg = core.RootElement.GetProperty("ggEncryptedAddress").GetString()
                       ?? throw new SonarException("coreProps.json sin dirección de GG");

        using var subApps = JsonDocument.Parse(await http.GetStringAsync($"https://{dirGg}/subApps"));
        var m = subApps.RootElement.GetProperty("subApps").GetProperty("moments");
        if (!m.GetProperty("isRunning").GetBoolean()) throw new SonarException("Moments no está corriendo");
        string dirMoments = m.GetProperty("metadata").GetProperty("encryptedWebServerAddress").GetString() ?? "";
        if (dirMoments == "") throw new SonarException("Moments todavía no está listo");

        gg = dirGg;
        moments = dirMoments;
        return (gg, moments);
    }

    async Task<JsonDocument> GetAsync(string url) => JsonDocument.Parse(await http.GetStringAsync(url));

    async Task PostAsync(string url, object? cuerpo)
    {
        using var contenido = cuerpo is null ? null
            : new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, contenido);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<Estado> GetEstadoAsync()
    {
        var (_, m) = await DireccionesAsync();
        using var doc = await GetAsync($"https://{m}/moments/status");
        var s = doc.RootElement.GetProperty("MomentsStatus");
        return new Estado(
            Habilitado: s.GetProperty("status").GetString() != "disabled",
            GrabandoEscritorio: s.GetProperty("desktop_mode_running").GetBoolean(),
            HayJuego: s.GetProperty("game").GetProperty("id").GetInt32() != 0);
    }

    public async Task IniciarEscritorioAsync()
    {
        var (_, m) = await DireccionesAsync();
        await PostAsync($"https://{m}/moments/desktopMode/start", null);
    }

    public async Task<bool> MicSilenciadoAsync()
    {
        var (_, m) = await DireccionesAsync();
        using var doc = await GetAsync($"https://{m}/moments/settings");
        return doc.RootElement.GetProperty("momentsSettings").GetProperty("is_mic_muted").GetBoolean();
    }

    // Igual que la interfaz de GG: se manda solo el campo que cambia.
    public async Task DesilenciarMicAsync()
    {
        var (_, m) = await DireccionesAsync();
        await PostAsync($"https://{m}/moments/settings", new Dictionary<string, object> { ["is_mic_muted"] = false });
    }

    // true si el atajo "Activar silencio del micrófono" de Moments tiene una tecla asignada.
    public async Task<bool> AtajoSilenciarMicAsignadoAsync()
    {
        var (g, _) = await DireccionesAsync();
        using var doc = await GetAsync($"https://{g}/keyboardShortcuts");
        foreach (var a in doc.RootElement.GetProperty("keyboardShortcuts").EnumerateArray())
        {
            if (a.GetProperty("id").GetInt32() != IdAtajoSilenciarMic) continue;
            using var trigger = JsonDocument.Parse(a.GetProperty("trigger_data").GetString() ?? "{}");
            return trigger.RootElement.TryGetProperty("hidCode", out _);
        }
        return false;
    }

    public async Task QuitarAtajoSilenciarMicAsync()
    {
        var (g, _) = await DireccionesAsync();
        await PostAsync($"https://{g}/keyboardShortcuts",
                        new Dictionary<string, object> { ["id"] = IdAtajoSilenciarMic, ["trigger_data"] = AtajoVacio });
    }

    public void Dispose() => http.Dispose();
}
