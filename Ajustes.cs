using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonarChatMix;

enum Accion
{
    ChatMixJuego, ChatMixChat, ChatMixCentro,
    JuegoDispAnterior, JuegoDispSiguiente,
    ChatDispAnterior, ChatDispSiguiente,
    MicDispAnterior, MicDispSiguiente,
    MaestroSubir, MaestroBajar, MaestroMute,
    JuegoSubir, JuegoBajar, JuegoMute,
    ChatSubir, ChatBajar, ChatMute,
    MicSubir, MicBajar, MicMute,
}

static class Acciones
{
    // Orden y textos de la pestaña "Atajos" de Opciones.
    public static readonly (string Grupo, (Accion Accion, string Nombre)[] Items)[] Grupos =
    [
        ("ChatMix", [
            (Accion.ChatMixJuego, "Más juego"),
            (Accion.ChatMixChat, "Más chat"),
            (Accion.ChatMixCentro, "Centrar"),
        ]),
        ("Cambio de dispositivo", [
            (Accion.JuegoDispAnterior, "Juego - Dispositivo anterior"),
            (Accion.JuegoDispSiguiente, "Juego - Siguiente dispositivo"),
            (Accion.ChatDispAnterior, "Chat - Dispositivo anterior"),
            (Accion.ChatDispSiguiente, "Chat - Siguiente dispositivo"),
            (Accion.MicDispAnterior, "Micrófono - Dispositivo anterior"),
            (Accion.MicDispSiguiente, "Micrófono - Siguiente dispositivo"),
        ]),
        ("Volumen y silenciar", [
            (Accion.MaestroSubir, "Maestro - Subir volumen"),
            (Accion.MaestroBajar, "Maestro - Bajar volumen"),
            (Accion.MaestroMute, "Maestro - Silenciar"),
            (Accion.JuegoSubir, "Juego - Subir volumen"),
            (Accion.JuegoBajar, "Juego - Bajar volumen"),
            (Accion.JuegoMute, "Juego - Silenciar"),
            (Accion.ChatSubir, "Chat - Subir volumen"),
            (Accion.ChatBajar, "Chat - Bajar volumen"),
            (Accion.ChatMute, "Chat - Silenciar"),
            (Accion.MicSubir, "Micrófono - Subir volumen"),
            (Accion.MicBajar, "Micrófono - Bajar volumen"),
            (Accion.MicMute, "Micrófono - Silenciar"),
        ]),
    ];
}

sealed record Atajo(Mod Mods, Keys Key)
{
    static readonly Dictionary<Keys, string> Nombres = new()
    {
        [Keys.PageUp] = "Re Pág", [Keys.PageDown] = "Av Pág",
        [Keys.Up] = "Arriba", [Keys.Down] = "Abajo", [Keys.Left] = "Izquierda", [Keys.Right] = "Derecha",
        [Keys.Delete] = "Supr", [Keys.Insert] = "Insert", [Keys.Home] = "Inicio", [Keys.End] = "Fin",
        [Keys.Space] = "Espacio", [Keys.Return] = "Enter", [Keys.Oemplus] = "+", [Keys.OemMinus] = "-",
        [Keys.Add] = "Num +", [Keys.Subtract] = "Num -", [Keys.Multiply] = "Num *", [Keys.Divide] = "Num /",
    };

    public override string ToString()
    {
        var partes = new List<string>();
        if (Mods.HasFlag(Mod.Ctrl)) partes.Add("Ctrl");
        if (Mods.HasFlag(Mod.Alt)) partes.Add("Alt");
        if (Mods.HasFlag(Mod.Shift)) partes.Add("Shift");
        if (Mods.HasFlag(Mod.Win)) partes.Add("Win");
        partes.Add(Nombres.TryGetValue(Key, out var n) ? n
                 : Key is >= Keys.D0 and <= Keys.D9 ? ((char)Key).ToString()
                 : Key is >= Keys.NumPad0 and <= Keys.NumPad9 ? "Num " + (Key - Keys.NumPad0)
                 : Key.ToString());
        return string.Join(" + ", partes);
    }
}

// Se guarda en %APPDATA%\SonarChatMix\ajustes.json. Se edita desde clic derecho → Opciones.
sealed class Ajustes
{
    static readonly string Ruta = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonarChatMix", "ajustes.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<Accion, Atajo> Atajos { get; set; } = new()
    {
        [Accion.ChatMixJuego] = new(Mod.None, Keys.PageUp),
        [Accion.ChatMixChat] = new(Mod.None, Keys.PageDown),
        [Accion.ChatMixCentro] = new(Mod.Ctrl | Mod.Alt, Keys.F11),
        // Los mismos que tenías configurados en Sonar.
        [Accion.JuegoDispAnterior] = new(Mod.Ctrl, Keys.Up),
        [Accion.ChatDispSiguiente] = new(Mod.Ctrl, Keys.Down),
        [Accion.MaestroSubir] = new(Mod.None, Keys.F12),
        [Accion.MaestroBajar] = new(Mod.None, Keys.F11),
        [Accion.MaestroMute] = new(Mod.None, Keys.F10),
    };

    // ChatMix va de -1 (todo juego) a 1 (todo chat); el volumen de 0 a 1.
    public double PasoChatMix { get; set; } = 0.1;
    public double PasoVolumen { get; set; } = 0.05;

    // Dial de volumen del auricular → ChatMix. Apagado, la app no toca ni el dial ni el volumen.
    public bool DialChatMix { get; set; }
    public double PasoDial { get; set; } = 0.05; // por clic
    public bool DialInvertido { get; set; } // normal: subir volumen = más chat
    public string DialSalida { get; set; } = ""; // nombre de la salida del auricular; "" = buscar "BlackShark"
    // Volumen fijo de esa salida mientras el dial maneja el ChatMix. Si llega a 100 % (o 0 %), el auricular
    // hace "bip bip" y deja de mandar el giro para ese lado: por eso queda lejos de los extremos.
    public double VolumenDial { get; set; } = 0.8;

    // Moments: que siempre esté grabando el escritorio, y que el micrófono nunca quede silenciado en los clips.
    public bool MomentsEscritorio { get; set; } = true;
    public bool MomentsMicSiempre { get; set; } = true;

    public static Ajustes Cargar()
    {
        try
        {
            if (File.Exists(Ruta))
                return JsonSerializer.Deserialize<Ajustes>(File.ReadAllText(Ruta), Json) ?? new();
        }
        catch { /* archivo roto: volvemos a los valores por defecto */ }
        return new();
    }

    public void Guardar()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
        File.WriteAllText(Ruta, JsonSerializer.Serialize(this, Json));
    }

    public Ajustes Clonar() => JsonSerializer.Deserialize<Ajustes>(JsonSerializer.Serialize(this, Json), Json)!;
}
