using System.Net.Http;

namespace SonarChatMix;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Evita abrirlo dos veces (los atajos no se pueden registrar dos veces).
        using var mutex = new Mutex(true, "SonarChatMixHotkeys", out bool primeraInstancia);
        if (!primeraInstancia) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Osd osd = new();
    readonly SonarClient sonar = new();
    readonly SemaphoreSlim cola = new(1, 1); // procesa las pulsaciones de a una, en orden
    readonly HotkeyWindow hotkeys = new();
    readonly DialAuricular dial = new();
    readonly EchoFix.AntiEco antiEco = new();
    readonly NotifyIcon tray;
    readonly Icon icono;
    readonly ToolStripMenuItem itemEco;

    readonly MomentsClient moments = new();
    readonly System.Windows.Forms.Timer vigiaMoments = new() { Interval = 30_000 };
    bool vigilando;

    Ajustes ajustes = Ajustes.Cargar();
    readonly HashSet<Accion> ocupados = [];
    OpcionesForm? opciones;

    // Al des-silenciar el maestro, Sonar des-silencia todas las salidas. Guardamos cuáles
    // estaban silenciadas antes para dejarlas como estaban.
    Dictionary<string, bool>? mutesAntesDelMaestro;

    // Clics del dial que todavía no se mandaron a Sonar.
    int clicsPendientes;
    bool moviendoPorDial;

    public TrayApp()
    {
        using (var s = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico")!)
            icono = new Icon(s);

        var menu = new ContextMenuStrip();
        var itemOpciones = new ToolStripMenuItem("Opciones…", null, (_, _) => AbrirOpciones());
        itemOpciones.Font = new Font(itemOpciones.Font, FontStyle.Bold);
        menu.Items.Add(itemOpciones);
        menu.Items.Add(new ToolStripSeparator());
        itemEco = new ToolStripMenuItem("Arreglo de eco de Discord") { Checked = antiEco.Activo };
        itemEco.Click += (_, _) => { antiEco.Activar(!antiEco.Activo); itemEco.Checked = antiEco.Activo; };
        menu.Items.Add(itemEco);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = new Icon(icono, SystemInformation.SmallIconSize),
            Text = "Sonar ChatMix",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => AbrirOpciones();

        hotkeys.Pressed += async id => await Ejecutar((Accion)(id - 1));
        RegistrarAtajos();

        dial.Giro += AlGirarDial;
        dial.Configurar(ajustes.DialChatMix, ajustes.DialSalida, ajustes.VolumenDial);

        vigiaMoments.Tick += async (_, _) => await VigilarMoments();
        vigiaMoments.Start();
        _ = VigilarMoments();
    }

    // Cada 30 s: si Moments dejó de grabar el escritorio lo vuelve a poner, y se asegura de que
    // el micrófono de los clips no esté silenciado (ni tenga atajo para silenciarlo).
    async Task VigilarMoments()
    {
        if (vigilando || (!ajustes.MomentsEscritorio && !ajustes.MomentsMicSiempre)) return;
        vigilando = true;
        try
        {
            if (ajustes.MomentsMicSiempre)
            {
                if (await moments.AtajoSilenciarMicAsignadoAsync()) await moments.QuitarAtajoSilenciarMicAsync();
                if (await moments.MicSilenciadoAsync()) await moments.DesilenciarMicAsync();
            }

            if (ajustes.MomentsEscritorio)
            {
                var estado = await moments.GetEstadoAsync();
                // Si está grabando un juego no lo tocamos: el modo escritorio es para todo lo demás.
                if (estado.Habilitado && !estado.GrabandoEscritorio && !estado.HayJuego)
                    await moments.IniciarEscritorioAsync();
            }
        }
        catch
        {
            // GG cerrado, reiniciándose o Moments desactivado: probamos de nuevo en la próxima vuelta.
            moments.Reset();
        }
        finally
        {
            vigilando = false;
        }
    }

    void RegistrarAtajos()
    {
        hotkeys.UnregisterAll();
        ocupados.Clear();
        foreach (var (accion, atajo) in ajustes.Atajos)
            if (!hotkeys.Register((int)accion + 1, atajo.Mods, atajo.Key)) // el id 0 no vale
                ocupados.Add(accion);

        if (ocupados.Count > 0)
            tray.ShowBalloonTip(5000, "Sonar ChatMix",
                $"{ocupados.Count} atajo(s) ya los usa otro programa (¿Sonar?): " +
                string.Join(", ", ocupados.Select(a => ajustes.Atajos[a])) +
                ". Abrí Opciones para cambiarlos.", ToolTipIcon.Warning);
    }

    void AbrirOpciones()
    {
        if (opciones is not null) { opciones.Activate(); return; }

        // Mientras la ventana está abierta soltamos los atajos, así podés "grabarlos" en las casillas.
        hotkeys.UnregisterAll();
        opciones = new OpcionesForm(ajustes.Clonar(), ocupados, antiEco.Activo, icono);
        opciones.FormClosed += (_, _) =>
        {
            if (opciones.Aceptado)
            {
                ajustes = opciones.Ajustes;
                try { ajustes.Guardar(); }
                catch (Exception e) { tray.ShowBalloonTip(5000, "Sonar ChatMix", "No pude guardar: " + e.Message, ToolTipIcon.Error); }
                if (opciones.EcoActivo != antiEco.Activo)
                {
                    antiEco.Activar(opciones.EcoActivo);
                    itemEco.Checked = antiEco.Activo;
                }
                dial.Configurar(ajustes.DialChatMix, ajustes.DialSalida, ajustes.VolumenDial);
            }
            opciones.Dispose();
            opciones = null;
            RegistrarAtajos();
            _ = VigilarMoments(); // aplica enseguida lo que hayas cambiado de Moments
        };
        opciones.Show();
        opciones.Activate();
    }

    Task Ejecutar(Accion accion) => Ejecutar(() => Hacer(accion));

    async Task Ejecutar(Func<Task> tarea)
    {
        await cola.WaitAsync();
        try
        {
            await ConReintento(tarea);
        }
        catch (Exception e)
        {
            osd.MostrarTexto(e is SonarException ? e.Message : "Sonar no responde\n" + e.Message);
        }
        finally
        {
            cola.Release();
        }
    }

    Task Hacer(Accion a) => a switch
    {
        Accion.ChatMixJuego => MoverChatMix(-ajustes.PasoChatMix),
        Accion.ChatMixChat => MoverChatMix(+ajustes.PasoChatMix),
        Accion.ChatMixCentro => MoverChatMix(null),

        Accion.JuegoDispAnterior => RotarDispositivo("game", "Juego", -1),
        Accion.JuegoDispSiguiente => RotarDispositivo("game", "Juego", +1),
        Accion.ChatDispAnterior => RotarDispositivo("chat", "Chat", -1),
        Accion.ChatDispSiguiente => RotarDispositivo("chat", "Chat", +1),
        Accion.MicDispAnterior => RotarDispositivo("mic", "Micrófono", -1),
        Accion.MicDispSiguiente => RotarDispositivo("mic", "Micrófono", +1),

        Accion.MaestroSubir => MoverVolumen("master", "Maestro", +1),
        Accion.MaestroBajar => MoverVolumen("master", "Maestro", -1),
        Accion.MaestroMute => AlternarMute("master", "Maestro"),
        Accion.JuegoSubir => MoverVolumen("game", "Juego", +1),
        Accion.JuegoBajar => MoverVolumen("game", "Juego", -1),
        Accion.JuegoMute => AlternarMute("game", "Juego"),
        Accion.ChatSubir => MoverVolumen("chatRender", "Chat", +1),
        Accion.ChatBajar => MoverVolumen("chatRender", "Chat", -1),
        Accion.ChatMute => AlternarMute("chatRender", "Chat"),
        Accion.MicSubir => MoverVolumen("chatCapture", "Micrófono", +1),
        Accion.MicBajar => MoverVolumen("chatCapture", "Micrófono", -1),
        Accion.MicMute => AlternarMute("chatCapture", "Micrófono"),
        _ => Task.CompletedTask,
    };

    // direccion: +1 = el lado de subir volumen. Si girás rápido, los clics que llegan mientras Sonar responde
    // se juntan y van en una sola llamada, por la misma cola que los atajos.
    async void AlGirarDial(int direccion)
    {
        clicsPendientes += ajustes.DialInvertido ? -direccion : direccion;
        if (moviendoPorDial) return;
        moviendoPorDial = true;
        try
        {
            while (clicsPendientes != 0)
            {
                int clics = clicsPendientes;
                clicsPendientes = 0;
                await Ejecutar(() => MoverChatMix(clics * ajustes.PasoDial));
            }
        }
        finally
        {
            moviendoPorDial = false;
        }
    }

    // delta = null → centrar
    async Task MoverChatMix(double? delta)
    {
        // Leemos el valor actual de Sonar en cada pulsación, así respeta lo que hayas movido a mano en GG.
        double actual = await sonar.GetChatMixAsync();
        double nuevo = delta is double d ? Math.Clamp(Math.Round(actual + d, 2), -1, 1) : 0;
        await sonar.SetChatMixAsync(nuevo);
        osd.Mostrar(nuevo);
    }

    static string GlifoDe(string nombre) => nombre switch
    {
        "Juego" => Glifo.Juego,
        "Chat" => Glifo.Chat,
        "Micrófono" => Glifo.Microfono,
        _ => Glifo.Volumen,
    };

    // Funciona como carrusel: la primera pulsación solo muestra el actual y el que viene;
    // mientras el cartel sigue a la vista, cada pulsación cambia y muestra el próximo.
    async Task RotarDispositivo(string canal, string nombre, int direccion)
    {
        bool entrada = canal == "mic";
        var lista = (await sonar.GetDispositivosAsync()).Where(d => d.EsEntrada == entrada).ToList();
        if (lista.Count == 0) throw new SonarException("No hay dispositivos para " + nombre);
        int n = lista.Count;

        string? idActual = await sonar.GetDispositivoActualAsync(canal);
        int i = lista.FindIndex(d => d.Id == idActual);
        string nombreActual = i >= 0 ? lista[i].Nombre : "Otro dispositivo";

        if (osd.MostrandoDispositivo(canal) && (n > 1 || i < 0))
        {
            i = i < 0 ? 0 : (i + direccion + n) % n;
            await sonar.SetDispositivoAsync(canal, lista[i].Id);
            nombreActual = lista[i].Nombre;
        }

        string? proximo = i < 0 ? lista[0].Nombre
                        : n > 1 ? lista[(i + direccion + n) % n].Nombre
                        : null;
        osd.MostrarDispositivo(canal, GlifoDe(nombre), nombre, nombreActual, proximo, direccion);
    }

    async Task MoverVolumen(string canal, string nombre, int direccion)
    {
        var (volumen, mute) = (await sonar.GetVolumenesAsync())[canal];
        double nuevo = Math.Clamp(Math.Round(volumen + direccion * ajustes.PasoVolumen, 2), 0, 1);
        await sonar.SetVolumenAsync(canal, nuevo);
        osd.MostrarVolumen(GlifoDe(nombre), nombre, nuevo, mute);
    }

    async Task AlternarMute(string canal, string nombre)
    {
        var vols = await sonar.GetVolumenesAsync();
        var (volumen, mute) = vols[canal];
        bool silenciar = !mute;

        if (canal == "master" && silenciar)
            mutesAntesDelMaestro = vols.Where(v => v.Key != "master").ToDictionary(v => v.Key, v => v.Value.Mute);

        await sonar.SetMuteAsync(canal, silenciar);

        if (canal == "master" && !silenciar && mutesAntesDelMaestro is not null)
        {
            foreach (var (c, estabaMuteado) in mutesAntesDelMaestro)
                if (estabaMuteado) await sonar.SetMuteAsync(c, true);
            mutesAntesDelMaestro = null;
        }

        osd.MostrarVolumen(GlifoDe(nombre), nombre, volumen, silenciar);
    }

    // Si GG se reinició, el puerto cambia: redescubrimos la dirección y probamos una vez más.
    async Task ConReintento(Func<Task> accion)
    {
        try { await accion(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            sonar.Reset();
            await accion();
        }
    }

    protected override void ExitThreadCore()
    {
        opciones?.Close();
        vigiaMoments.Dispose();
        moments.Dispose();
        hotkeys.Dispose();
        dial.Dispose();
        antiEco.Dispose();
        tray.Visible = false;
        tray.Dispose();
        sonar.Dispose();
        osd.Dispose();
        base.ExitThreadCore();
    }
}
