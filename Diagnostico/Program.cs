using System.Globalization;
using System.Text;

namespace DiagnosticoDial;

// Registra todo lo que podría generar el dial de volumen del auricular, para saber qué hace en modo PC
// antes de implementar nada. Es solo lectura: no cambia volúmenes ni le manda nada a ningún dispositivo.
//
// Uso (desde la carpeta del repo):  dotnet run --project Diagnostico [-- --segundos 60] [--bloquear-volumen]
// El log queda en diagnostico-dial.log, en la carpeta desde donde lo corrés.
// --bloquear-volumen descarta todas las teclas de volumen mientras corre (también las del teclado): sirve para
// ver si el cartel de volumen de Windows depende de esas teclas.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        int segundos = 0; // 0 = hasta Ctrl+C
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--segundos") int.TryParse(args[i + 1], out segundos);
        bool bloquear = args.Contains("--bloquear-volumen");

        // Números con punto en el log, también en los hilos de los avisos de Core Audio.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

        string ruta = Path.GetFullPath("diagnostico-dial.log");
        Log.Abrir(ruta);
        Log.Info($"Diagnóstico del dial. Log en {ruta}");
        Log.Info($"Windows {Environment.OSVersion.Version}, proceso de {(Environment.Is64BitProcess ? 64 : 32)} bits");

        // Los avisos de Core Audio llegan en otros hilos: lo que haya que hacer se pasa a este.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ui = SynchronizationContext.Current!;

        using var audio = new MonitorAudio(ui);
        using var raw = new MonitorRawInput();
        using var hid = new MonitorHid();
        using var teclas = new MonitorTeclas(bloquear);

        audio.Iniciar();
        raw.Iniciar();
        hid.Sincronizar();

        // Cada 2 s se fija si apareció o desapareció algún dispositivo HID de Razer (dongle reconectado).
        using var vigia = new System.Windows.Forms.Timer { Interval = 2000 };
        vigia.Tick += (_, _) => hid.Sincronizar();
        vigia.Start();

        using var fin = new System.Windows.Forms.Timer { Interval = Math.Max(segundos, 1) * 1000 };
        fin.Tick += (_, _) => Application.ExitThread();
        if (segundos > 0) fin.Start();

        Log.Info("Listo. Girá el dial unas vueltas para un lado, esperá 3 s y girá unas vueltas para el otro. " +
                 (segundos > 0 ? $"Termina solo en {segundos} s." : "Ctrl+C para terminar."));
        Application.Run();
        Log.Info("Fin.");
    }
}

static class Log
{
    static readonly object candado = new();
    static StreamWriter? archivo;

    public static void Abrir(string ruta) =>
        archivo = new StreamWriter(ruta, append: false, new UTF8Encoding(false)) { AutoFlush = true };

    public static void Info(string texto) => Escribir("INFO", texto);

    public static void Escribir(string origen, string texto)
    {
        string linea = $"{DateTime.Now:HH:mm:ss.fff} [{origen,-5}] {texto}";
        lock (candado)
        {
            Console.WriteLine(linea);
            archivo?.WriteLine(linea);
        }
    }

    public static string Hex(byte[] datos, int inicio, int largo) =>
        string.Join(" ", datos.Skip(inicio).Take(largo).Select(b => b.ToString("X2")));
}
