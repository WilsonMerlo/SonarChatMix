namespace SonarChatMix;

// Los atajos y los pasos ahora se cambian desde clic derecho → Opciones (se guardan en ajustes.json).
static class Config
{
    // Cuánto tiempo queda visible el cartelito en pantalla (ms).
    public const int OsdMs = 1200;

    // El carrusel de dispositivos queda más tiempo: mientras se ve, la próxima pulsación cambia.
    public const int OsdDispositivoMs = 2500;
}

[Flags]
enum Mod : uint
{
    None  = 0,
    Alt   = 0x1,
    Ctrl  = 0x2,
    Shift = 0x4,
    Win   = 0x8,
}
