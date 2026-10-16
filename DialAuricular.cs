using System.Runtime.InteropServices;
using static SonarChatMix.Audio;

namespace SonarChatMix;

// Usa el dial de volumen del auricular (Razer BlackShark V2 Pro, dongle en modo PC) para mover el ChatMix.
//
// Qué hace el dial (visto con Diagnostico/): por cada clic, el auricular manda dos Volume Up (0xE9) o
// Volume Down (0xEA) por su colección HID de Consumer Control, y Windows le cambia ±2 % por cada uno a la
// salida del propio auricular. El auricular sigue ese volumen: al llegar a 100 % (o a 0 %) hace "bip bip"
// y deja de mandar el giro para ese lado. Entonces:
//   - con Raw Input se escuchan solo los reportes del auricular (mismo ContainerId que su salida de audio),
//     no las teclas de volumen del teclado, y cada clic se avisa con Giro (+1 subir, -1 bajar);
//   - el volumen de esa salida queda fijo en un valor con margen para los dos lados (VolumenDial): cada
//     vez que cambia, se lo vuelve a poner. Esos cambios se marcan con un GUID propio, así la app no
//     reacciona a lo que hizo ella misma.
// Al auricular no se le manda nada: Raw Input solo lee.
sealed class DialAuricular : NativeWindow, IMMNotificationClient, IAudioEndpointVolumeCallback, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Destino; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Tipo, Tam; public IntPtr Dispositivo, WParam; }

    [DllImport("user32.dll")]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pedidos, uint n, uint tam);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr raw, uint comando, IntPtr datos, ref uint tam, uint tamCabecera);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr dispositivo, uint comando, IntPtr datos, ref uint tam);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("hid.dll")]
    static extern int HidP_GetUsages(int tipoReporte, ushort pagina, ushort coleccion, [Out] ushort[] usos,
                                     ref uint cantidad, IntPtr preparsed, byte[] reporte, uint largo);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint nodo, string instancia, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_PropertyW(uint nodo, in PROPERTYKEY clave, out uint tipo,
                                               out Guid valor, ref uint tam, uint flags);

    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const int WM_VOLUMEN = 0x8001, WM_DISPOSITIVOS = 0x8002; // WM_APP + n
    const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    const uint RIM_TYPEHID = 2, ERROR = uint.MaxValue, DEVPROP_TYPE_GUID = 0x0D;
    const ushort PaginaConsumer = 0x0C, ConsumerControl = 0x01, VolumeUp = 0xE9, VolumeDown = 0xEA;
    const int HidP_Input = 0, HIDP_STATUS_SUCCESS = 0x00110000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    // Marca de los cambios de volumen que hace la app.
    static readonly Guid MiContexto = new("6f1c2a52-3c8e-4d3b-9a57-5e0c1d7b8a41");

    // Contenedor de lo que no es un dispositivo físico (las salidas virtuales de Sonar): no se aparea con ningún HID.
    static readonly Guid SinContenedor = new("00000000-0000-0000-ffff-ffffffffffff");

    readonly IntPtr hwnd; // para los PostMessage desde los hilos de Core Audio
    IMMDeviceEnumerator? enumerador;
    bool activo;
    string nombreSalida = "";
    float referencia = 0.8f;

    // Salida de audio del auricular
    IAudioEndpointVolume? volumen;
    Guid contenedor;

    // Colecciones HID que mandan Consumer Control: null si no son del auricular.
    readonly Dictionary<IntPtr, ConsumerDelAuricular?> hids = [];
    long ultimaPulsacion;
    int ultimaDireccion;
    bool ultimaContada;

    public event Action<int>? Giro;

    public DialAuricular()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        hwnd = Handle;
    }

    // Con activar = false la app no escucha el dial ni toca el volumen.
    // nivel: volumen fijo para la salida del auricular (0 a 1).
    public void Configurar(bool activar, string salida, double nivel)
    {
        referencia = (float)nivel;
        if (activar == activo && salida == nombreSalida)
        {
            FijarVolumen(); // por si cambió el nivel
            return;
        }
        Apagar();
        if (!activar) return;

        activo = true;
        nombreSalida = salida;
        RAWINPUTDEVICE[] pedido =
        [
            new() { UsagePage = PaginaConsumer, Usage = ConsumerControl, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Destino = hwnd },
        ];
        RegisterRawInputDevices(pedido, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        enumerador = NuevoEnumerador();
        enumerador.RegisterEndpointNotificationCallback(this);
        Suscribir();
    }

    void Apagar()
    {
        if (!activo) return;
        activo = false;
        RAWINPUTDEVICE[] quitar = [new() { UsagePage = PaginaConsumer, Usage = ConsumerControl, Flags = RIDEV_REMOVE }];
        RegisterRawInputDevices(quitar, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (enumerador is not null)
        {
            enumerador.UnregisterEndpointNotificationCallback(this);
            Marshal.ReleaseComObject(enumerador);
            enumerador = null;
        }
        SoltarSalida();
    }

    // Busca la salida del auricular, se suscribe a sus cambios de volumen y la deja en el nivel fijo. Se llama
    // al activar y cada vez que Windows avisa que se conectó o desconectó un dispositivo de audio (el dongle).
    void Suscribir()
    {
        SoltarSalida();
        var salida = Buscar(nombreSalida);
        if (salida is null || enumerador is null || enumerador.GetDevice(salida.Id, out var d) != 0) return;

        int hr = d.Activate(IID_IAudioEndpointVolume, CLSCTX_ALL, IntPtr.Zero, out object o);
        Marshal.ReleaseComObject(d);
        if (hr != 0) return;

        var v = (IAudioEndpointVolume)o;
        if (v.RegisterControlChangeNotify(this) != 0)
        {
            Marshal.ReleaseComObject(v);
            return;
        }
        volumen = v;
        contenedor = salida.Contenedor == SinContenedor ? Guid.Empty : salida.Contenedor;
        FijarVolumen();
    }

    void SoltarSalida()
    {
        if (volumen is not null)
        {
            volumen.UnregisterControlChangeNotify(this);
            Marshal.ReleaseComObject(volumen);
            volumen = null;
        }
        contenedor = Guid.Empty;
        foreach (var h in hids.Values) h?.Dispose();
        hids.Clear();
    }

    // Lejos de 100 % y de 0 %, el auricular siempre manda el giro para los dos lados. La tolerancia (medio
    // paso de Windows) evita pelearse con redondeos.
    void FijarVolumen()
    {
        if (volumen is not null && volumen.GetMasterVolumeLevelScalar(out float v) == 0 && Math.Abs(v - referencia) > 0.01f)
            volumen.SetMasterVolumeLevelScalar(referencia, MiContexto);
    }

    // IAudioEndpointVolumeCallback: llega en un hilo de Windows. Lo que no hizo la app se corrige en el hilo principal.
    public int OnNotify(IntPtr datos)
    {
        if (Marshal.PtrToStructure<Guid>(datos) != MiContexto) PostMessage(hwnd, WM_VOLUMEN, IntPtr.Zero, IntPtr.Zero);
        return 0;
    }

    // IMMNotificationClient: si se conecta o desconecta algo, se vuelve a buscar la salida del auricular.
    public int OnDeviceStateChanged(string id, uint estado) => AvisarCambioDeDispositivos();
    public int OnDeviceAdded(string id) => AvisarCambioDeDispositivos();
    public int OnDeviceRemoved(string id) => AvisarCambioDeDispositivos();
    public int OnDefaultDeviceChanged(EDataFlow flujo, ERole rol, string? id) => 0;
    public int OnPropertyValueChanged(string id, PROPERTYKEY clave) => 0;

    int AvisarCambioDeDispositivos()
    {
        PostMessage(hwnd, WM_DISPOSITIVOS, IntPtr.Zero, IntPtr.Zero);
        return 0;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_INPUT when activo:
                AlRecibir(m.LParam);
                break;
            case WM_INPUT_DEVICE_CHANGE: // se conectó o desconectó una colección HID: se vuelve a clasificar
                if (hids.Remove(m.LParam, out var h)) h?.Dispose();
                break;
            case WM_VOLUMEN when activo:
                FijarVolumen();
                break;
            case WM_DISPOSITIVOS when activo:
                Suscribir();
                break;
        }
        base.WndProc(ref m); // DefWindowProc libera el WM_INPUT
    }

    void AlRecibir(IntPtr raw)
    {
        uint tamCab = (uint)Marshal.SizeOf<RAWINPUTHEADER>(), tam = 0;
        GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref tam, tamCab);
        if (tam == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)tam);
        try
        {
            if (GetRawInputData(raw, RID_INPUT, buf, ref tam, tamCab) == ERROR) return;
            var cab = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (cab.Tipo != RIM_TYPEHID || DelAuricular(cab.Dispositivo) is not { } hid) return;

            // RAWHID: dwSizeHid, dwCount y los reportes uno atrás del otro.
            IntPtr datos = buf + (int)tamCab;
            int largo = Marshal.ReadInt32(datos, 0), cuenta = Marshal.ReadInt32(datos, 4);
            var reporte = new byte[largo];
            for (int i = 0; i < cuenta; i++)
            {
                Marshal.Copy(datos + 8 + i * largo, reporte, 0, largo);
                int direccion = hid.Pulsacion(reporte);
                if (direccion != 0) AlPulsar(direccion);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // Cada clic del dial manda dos pulsaciones seguidas: la segunda no cuenta como otro movimiento.
    void AlPulsar(int direccion)
    {
        long ahora = Environment.TickCount64;
        bool segunda = ultimaContada && direccion == ultimaDireccion && ahora - ultimaPulsacion < Config.DialClicMs;
        ultimaContada = !segunda;
        ultimaDireccion = direccion;
        ultimaPulsacion = ahora;
        if (!segunda) Giro?.Invoke(direccion);
    }

    ConsumerDelAuricular? DelAuricular(IntPtr dispositivo)
    {
        if (hids.TryGetValue(dispositivo, out var h)) return h;
        h = contenedor != Guid.Empty && ContenedorDe(RutaDe(dispositivo)) == contenedor
            ? ConsumerDelAuricular.Crear(dispositivo) : null;
        return hids[dispositivo] = h;
    }

    static string RutaDe(IntPtr dispositivo)
    {
        uint tam = 0; // en caracteres
        GetRawInputDeviceInfoW(dispositivo, RIDI_DEVICENAME, IntPtr.Zero, ref tam);
        if (tam == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)tam * 2);
        try
        {
            return GetRawInputDeviceInfoW(dispositivo, RIDI_DEVICENAME, buf, ref tam) == ERROR
                ? "" : Marshal.PtrToStringUni(buf) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // "\\?\HID#VID_1532&PID_0A49&MI_03&Col02#8&...#{guid}" → instancia "HID\VID_1532&...\8&..." → ContainerId
    static Guid ContenedorDe(string ruta)
    {
        string r = ruta.TrimStart('\\', '?'); // "\\?\" o "\??\"
        int g = r.LastIndexOf("#{", StringComparison.Ordinal);
        string instancia = (g >= 0 ? r[..g] : r).Replace('#', '\\');
        uint tam = 16;
        return instancia != ""
            && CM_Locate_DevNodeW(out uint nodo, instancia, 0) == 0
            && CM_Get_DevNode_PropertyW(nodo, PKEY_Device_ContainerId, out uint tipo, out Guid id, ref tam, 0) == 0
            && tipo == DEVPROP_TYPE_GUID ? id : Guid.Empty;
    }

    public void Dispose()
    {
        Apagar();
        DestroyHandle();
    }

    // Colección Consumer Control del auricular. Sus reportes se interpretan con el descriptor que Windows ya
    // tiene guardado (preparsed data), sin preguntarle nada al dispositivo.
    sealed class ConsumerDelAuricular(IntPtr preparsed) : IDisposable
    {
        readonly ushort[] usos = new ushort[32];
        bool subiendo, bajando;

        public static ConsumerDelAuricular? Crear(IntPtr dispositivo)
        {
            uint tam = 0;
            GetRawInputDeviceInfoW(dispositivo, RIDI_PREPARSEDDATA, IntPtr.Zero, ref tam);
            if (tam == 0) return null;
            IntPtr p = Marshal.AllocHGlobal((int)tam);
            if (GetRawInputDeviceInfoW(dispositivo, RIDI_PREPARSEDDATA, p, ref tam) != ERROR) return new(p);
            Marshal.FreeHGlobal(p);
            return null;
        }

        // +1 si se acaba de apretar Volume Up, -1 si Volume Down, 0 si no hay nada nuevo (soltar o seguir apretado).
        public int Pulsacion(byte[] reporte)
        {
            uint n = (uint)usos.Length;
            if (HidP_GetUsages(HidP_Input, PaginaConsumer, 0, usos, ref n, preparsed, reporte, (uint)reporte.Length)
                != HIDP_STATUS_SUCCESS) return 0; // otro reporte del mismo dispositivo, sin teclas de volumen

            var apretados = usos.AsSpan(0, (int)n);
            bool sube = apretados.Contains(VolumeUp), baja = apretados.Contains(VolumeDown);
            int direccion = sube && !subiendo ? +1 : baja && !bajando ? -1 : 0;
            subiendo = sube;
            bajando = baja;
            return direccion;
        }

        public void Dispose() => Marshal.FreeHGlobal(preparsed);
    }
}
