using System.Runtime.InteropServices;

namespace DiagnosticoDial;

// Se suscribe a los cambios de volumen y mute de todas las salidas activas (IAudioEndpointVolumeCallback)
// y a las altas/bajas de dispositivos (IMMNotificationClient). Solo lee: nunca cambia nada.
sealed class MonitorAudio(SynchronizationContext ui) : IMMNotificationClient, IDisposable
{
    const uint DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, STGM_READ = 0;
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    readonly IMMDeviceEnumerator enumerador = (IMMDeviceEnumerator)new MMDeviceEnumerator();
    readonly Dictionary<string, Salida> salidas = [];
    bool registrado;

    public void Iniciar()
    {
        Sincronizar();
        LogPredeterminadas();
        int hr = enumerador.RegisterEndpointNotificationCallback(this);
        registrado = hr == 0;
        if (!registrado) Log.Escribir("AUDIO", $"RegisterEndpointNotificationCallback falló (0x{hr:X8})");
    }

    // Se suscribe a las salidas nuevas y suelta las que ya no están. Corre en el hilo principal.
    void Sincronizar()
    {
        var activas = new Dictionary<string, IMMDevice>();
        if (enumerador.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var col) == 0)
        {
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
                if (col.Item(i, out var d) == 0 && d.GetId(out string id) == 0) activas[id] = d;
        }

        foreach (var id in salidas.Keys.Except(activas.Keys).ToList())
        {
            Log.Escribir("AUDIO", $"deja de estar activa: \"{salidas[id].Nombre}\"");
            salidas[id].Dispose();
            salidas.Remove(id);
        }

        foreach (var (id, d) in activas)
        {
            if (salidas.ContainsKey(id)) continue;
            if (d.Activate(IID_IAudioEndpointVolume, CLSCTX_ALL, IntPtr.Zero, out object o) != 0) continue;
            var s = new Salida(NombreDe(d), id, (IAudioEndpointVolume)o);
            if (s.Suscribir()) salidas[id] = s;
        }
    }

    void LogPredeterminadas()
    {
        foreach (var (rol, texto) in new[] { (ERole.eConsole, "predeterminada"), (ERole.eCommunications, "de comunicaciones") })
            if (enumerador.GetDefaultAudioEndpoint(EDataFlow.eRender, rol, out var d) == 0)
                Log.Escribir("AUDIO", $"salida {texto}: \"{NombreDe(d)}\"");
    }

    static string NombreDe(IMMDevice d)
    {
        if (d.OpenPropertyStore(STGM_READ, out var props) != 0) return "?";
        var clave = PKEY_Device_FriendlyName;
        if (props.GetValue(ref clave, out var pv) != 0) return "?";
        try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p1) ?? "?" : "?"; } // VT_LPWSTR
        finally { PropVariantClear(ref pv); }
    }

    string NombreDe(string id)
    {
        if (salidas.TryGetValue(id, out var s)) return s.Nombre;
        return enumerador.GetDevice(id, out var d) == 0 ? NombreDe(d) : id;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pv);

    // IMMNotificationClient: llega en un hilo de Windows donde no conviene llamar al enumerador,
    // así que solo se pasa el trabajo al hilo principal.
    static string Estado(uint e) => e switch { 1 => "activo", 2 => "deshabilitado", 4 => "no presente", 8 => "desenchufado", _ => e.ToString() };

    public int OnDeviceStateChanged(string id, uint estado)
    {
        ui.Post(_ => { Log.Escribir("AUDIO", $"cambia de estado a {Estado(estado)}: \"{NombreDe(id)}\""); Sincronizar(); }, null);
        return 0;
    }

    public int OnDeviceAdded(string id)
    {
        ui.Post(_ => { Log.Escribir("AUDIO", $"dispositivo agregado: \"{NombreDe(id)}\""); Sincronizar(); }, null);
        return 0;
    }

    public int OnDeviceRemoved(string id)
    {
        ui.Post(_ => { Log.Escribir("AUDIO", $"dispositivo quitado: {id}"); Sincronizar(); }, null);
        return 0;
    }

    public int OnDefaultDeviceChanged(EDataFlow flujo, ERole rol, string? id)
    {
        if (flujo == EDataFlow.eRender && id is not null)
            ui.Post(_ => Log.Escribir("AUDIO", $"nueva salida predeterminada ({rol}): \"{NombreDe(id)}\""), null);
        return 0;
    }

    public int OnPropertyValueChanged(string id, PROPERTYKEY clave) => 0;

    public void Dispose()
    {
        if (registrado) enumerador.UnregisterEndpointNotificationCallback(this);
        foreach (var s in salidas.Values) s.Dispose();
        salidas.Clear();
    }
}

// Una salida de audio con su callback de volumen.
sealed class Salida(string nombre, string id, IAudioEndpointVolume volumen) : IAudioEndpointVolumeCallback, IDisposable
{
    public string Nombre => nombre;
    float anterior;
    bool registrado;

    static string SiNo(bool b) => b ? "sí" : "no";

    public bool Suscribir()
    {
        volumen.GetMasterVolumeLevelScalar(out anterior);
        volumen.GetMute(out bool mute);
        volumen.GetVolumeStepInfo(out _, out uint pasos);
        volumen.GetVolumeRange(out float min, out float max, out float inc);
        volumen.QueryHardwareSupport(out uint hw);
        int hr = volumen.RegisterControlChangeNotify(this);
        registrado = hr == 0;

        Log.Escribir("AUDIO", $"escuchando \"{nombre}\": vol {anterior:0.000}, mute {SiNo(mute)}, {pasos} pasos, " +
                              $"{min:0.#}..{max:0.#} dB de a {inc:0.###} dB, en hardware: volumen {SiNo((hw & 1) != 0)}, " +
                              $"mute {SiNo((hw & 2) != 0)}" + (registrado ? "" : $" — NO se pudo suscribir (0x{hr:X8})"));
        Log.Escribir("AUDIO", $"    id {id}");
        return registrado;
    }

    // AUDIO_VOLUME_NOTIFICATION_DATA: GUID contexto, BOOL mute, float maestro, UINT canales, float[canales].
    public int OnNotify(IntPtr p)
    {
        var contexto = Marshal.PtrToStructure<Guid>(p);
        bool mute = Marshal.ReadInt32(p, 16) != 0;
        float vol = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 20));
        int canales = Math.Min(Marshal.ReadInt32(p, 24), 8);
        var porCanal = Enumerable.Range(0, canales)
            .Select(i => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, 28 + 4 * i)).ToString("0.000"));
        float delta = vol - anterior;
        anterior = vol;

        Log.Escribir("AUDIO", $"\"{nombre}\" vol {vol:0.000} (Δ {delta:+0.000;-0.000;0}) mute {SiNo(mute)} " +
                              $"canales [{string.Join(" ", porCanal)}] contexto {contexto}" +
                              (contexto == Guid.Empty ? " (vacío)" : ""));
        return 0;
    }

    public void Dispose()
    {
        if (registrado) volumen.UnregisterControlChangeNotify(this);
        Marshal.ReleaseComObject(volumen);
    }
}

enum EDataFlow { eRender, eCapture, eAll }
enum ERole { eConsole, eMultimedia, eCommunications }

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY(Guid fmtid, uint pid)
{
    public Guid fmtid = fmtid;
    public uint pid = pid;
}

[StructLayout(LayoutKind.Sequential)]
struct PROPVARIANT
{
    public ushort vt, r1, r2, r3;
    public IntPtr p1, p2;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow flujo, uint estados, out IMMDeviceCollection col);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flujo, ERole rol, out IMMDevice d);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient cliente);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient cliente);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int Item(uint i, out IMMDevice d);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    [PreserveSig] int OpenPropertyStore(uint acceso, out IPropertyStore props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint estado);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint n);
    [PreserveSig] int GetAt(uint i, out PROPERTYKEY clave);
    [PreserveSig] int GetValue(ref PROPERTYKEY clave, out PROPVARIANT valor);
    [PreserveSig] int SetValue(ref PROPERTYKEY clave, ref PROPVARIANT valor);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint estado);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flujo, ERole rol, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY clave);
}

// Solo se declaran (no se usan) los Set*, para respetar el orden de la vtable.
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float db, in Guid contexto);
    [PreserveSig] int SetMasterVolumeLevelScalar(float nivel, in Guid contexto);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float nivel);
    [PreserveSig] int SetChannelVolumeLevel(uint canal, float db, in Guid contexto);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint canal, float nivel, in Guid contexto);
    [PreserveSig] int GetChannelVolumeLevel(uint canal, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint canal, out float nivel);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid contexto);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint paso, out uint pasos);
    [PreserveSig] int VolumeStepUp(in Guid contexto);
    [PreserveSig] int VolumeStepDown(in Guid contexto);
    [PreserveSig] int QueryHardwareSupport(out uint mascara);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float pasoDb);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr datos);
}
