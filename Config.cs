namespace SonarChatMix;

// Los atajos y los pasos ahora se cambian desde clic derecho → Opciones (se guardan en ajustes.json).
static class Config
{
    // Cuánto tiempo queda visible el cartelito en pantalla (ms).
    public const int OsdMs = 1200;

    // El carrusel de dispositivos queda más tiempo: mientras se ve, la próxima pulsación cambia.
    public const int OsdDispositivoMs = 2500;

    // Dial del auricular. Cada clic manda dos pulsaciones de volumen separadas por 6-10 ms (y entre un clic
    // y otro pasan 30 ms o más, aunque lo gires rápido): si la segunda llega antes de esto, es el mismo clic.
    public const int DialClicMs = 25;

    // Con la salida en "Automático", se usa la primera cuyo nombre contenga esto.
    public const string DialNombreAuto = "BlackShark";
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
