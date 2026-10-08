using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonarChatMix.EchoFix;

// Lo de DiscordSonarEchoFix (AutoMuter + Settings) adaptado: cada 3 s mantiene a Discord
// muteado en las salidas físicas para que el "compartir sonido" del streaming no devuelva
// las voces a la llamada. Usa el mismo settings.json que el programa original, así que la
// elección de dispositivos que hayas hecho ahí se respeta.
sealed class AntiEco : IDisposable
{
    static readonly string RutaSettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiscordEchoFix", "settings.json");

    readonly DiscordSessionController discord = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    Dictionary<string, bool> overrides = new();

    public bool Activo { get; private set; }

    public AntiEco()
    {
        timer.Tick += (_, _) => Aplicar();
        bool activo = true;
        try
        {
            if (File.Exists(RutaSettings) && JsonNode.Parse(File.ReadAllText(RutaSettings)) is JsonObject s)
            {
                activo = s["AutoMuteEnabled"]?.GetValue<bool>() ?? true;
                overrides = s["EndpointOverrides"]?.Deserialize<Dictionary<string, bool>>() ?? new();
            }
        }
        catch { /* settings roto: usamos la regla por defecto */ }
        Activar(activo, guardar: false);
    }

    public void Activar(bool activo, bool guardar = true)
    {
        Activo = activo;
        if (activo) { Aplicar(); timer.Start(); }
        else
        {
            timer.Stop();
            // Al apagarlo devolvemos el audio de Discord donde lo habíamos silenciado.
            try { discord.SetMuted(false, overrides); } catch { }
        }
        if (guardar) Guardar();
    }

    void Aplicar()
    {
        try { discord.EnforceMute(overrides); } catch { }
    }

    // Solo tocamos AutoMuteEnabled; el resto del archivo queda como lo dejó el programa original.
    void Guardar()
    {
        try
        {
            var s = File.Exists(RutaSettings) ? JsonNode.Parse(File.ReadAllText(RutaSettings)) as JsonObject : null;
            s ??= new JsonObject();
            s["AutoMuteEnabled"] = Activo;
            Directory.CreateDirectory(Path.GetDirectoryName(RutaSettings)!);
            File.WriteAllText(RutaSettings, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Dispose() => timer.Dispose();
}
