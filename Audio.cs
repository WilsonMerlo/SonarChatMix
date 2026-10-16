using System.Runtime.InteropServices;

namespace SonarChatMix;

// Salidas de audio de Windows (Core Audio) para el dial del auricular. Va aparte de EchoFix/CoreAudio.cs
// (código de terceros) porque acá hacen falta los callbacks de volumen y de dispositivos.
static class Audio
{
    public sealed record Salida(string Id, string Nombre, Guid Contenedor);

    public const uint DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, STGM_READ = 0;
    const ushort VT_LPWSTR = 31, VT_CLSID = 72;
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    // El "dispositivo físico" al que pertenece: la salida de audio y el HID del auricular comparten este id.
    // Sirve también como DEVPKEY_Device_ContainerId (DEVPROPKEY y PROPERTYKEY son la misma estructura).
    public static readonly PROPERTYKEY PKEY_Device_ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    public static IMMDeviceEnumerator NuevoEnumerador() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    // Salidas activas, en el orden de Windows.
    public static List<Salida> Salidas()
    {
        var lista = new List<Salida>();
        var e = NuevoEnumerador();
        try
        {
            if (e.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var col) != 0) return lista;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) != 0) continue;
                if (d.GetId(out string id) == 0)
                    lista.Add(new Salida(id,
                        Propiedad(d, PKEY_Device_FriendlyName, VT_LPWSTR, Marshal.PtrToStringUni) ?? id,
                        Propiedad(d, PKEY_Device_ContainerId, VT_CLSID, Marshal.PtrToStructure<Guid>)));
                Marshal.ReleaseComObject(d);
            }
            Marshal.ReleaseComObject(col);
        }
        finally
        {
            Marshal.ReleaseComObject(e);
        }
        return lista;
    }

    // La salida elegida en Opciones; con nombre vacío, la primera que se llame como el auricular (BlackShark).
    public static Salida? Buscar(string nombre, List<Salida>? salidas = null) =>
        (salidas ?? Salidas()).FirstOrDefault(s => nombre == ""
            ? s.Nombre.Contains(Config.DialNombreAuto, StringComparison.OrdinalIgnoreCase)
            : s.Nombre.Equals(nombre, StringComparison.OrdinalIgnoreCase));

    static T? Propiedad<T>(IMMDevice d, PROPERTYKEY clave, ushort tipo, Func<IntPtr, T> leer)
    {
        if (d.OpenPropertyStore(STGM_READ, out var props) != 0) return default;
        try
        {
            if (props.GetValue(ref clave, out var pv) != 0) return default;
            try { return pv.vt == tipo && pv.p1 != IntPtr.Zero ? leer(pv.p1) : default; }
            finally { PropVariantClear(ref pv); }
        }
        finally
        {
            Marshal.ReleaseComObject(props);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pv);

    public enum EDataFlow { eRender, eCapture, eAll }
    public enum ERole { eConsole, eMultimedia, eCommunications }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt, r1, r2, r3;
        public IntPtr p1, p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flujo, uint estados, out IMMDeviceCollection col);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flujo, ERole rol, out IMMDevice d);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient cliente);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient cliente);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int Item(uint i, out IMMDevice d);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, uint clsCtx, IntPtr parametros, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(uint acceso, out IPropertyStore props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint estado);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY clave);
        [PreserveSig] int GetValue(ref PROPERTYKEY clave, out PROPVARIANT valor);
        [PreserveSig] int SetValue(ref PROPERTYKEY clave, ref PROPVARIANT valor);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint estado);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flujo, ERole rol, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY clave);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
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
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr datos);
    }
}
