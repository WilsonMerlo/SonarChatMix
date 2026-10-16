# Sonar ChatMix por teclado

App chiquita en la bandeja del sistema que maneja SteelSeries Sonar con atajos globales (ChatMix, volumen
y silencio de cada canal, rotar dispositivos de Juego/Chat/Micrófono) y, además, evita que tus amigos se
escuchen a sí mismos cuando compartís pantalla con sonido en Discord.

Los atajos y los pasos se cambian desde **clic derecho en el ícono → Opciones** (o doble clic en el ícono)
y se guardan en `%APPDATA%\SonarChatMix\ajustes.json`. Si un atajo también está asignado en Sonar,
sacalo de Sonar: si no, las dos apps reaccionan a la misma tecla.

Una tecla sin modificador (Re Pág, F10…) deja de funcionar en el resto de Windows mientras la app esté abierta.
F12 lo reserva Windows, así que ese se escucha con un hook de teclado en vez de RegisterHotKey.

El ChatMix solo funciona si en Sonar los canales Juego y Chat salen por el mismo dispositivo
(si no, Sonar lo bloquea y la app muestra un error).

## Dial del auricular → ChatMix

Con un Razer BlackShark V2 Pro (dongle en modo PC), el dial de volumen del auricular puede mover el ChatMix.
Se activa en **Opciones → General → Dial del auricular**: ahí elegís cuánto se mueve por clic, el sentido, la
salida de audio del auricular (en "Automático" usa la primera que se llame BlackShark) y su volumen fijo.

Por cada clic, el auricular manda teclas de volumen (HID Consumer Control) y Windows se las aplica a la salida
del propio auricular. La app escucha con Raw Input solo las que vienen del auricular (las del teclado no) y
mueve el ChatMix. Al auricular no se le manda nada.

El auricular sigue el volumen de esa salida, y en 100 % (o 0 %) hace "bip bip" y deja de mandar el giro para
ese lado. Por eso, mientras está activado, la app deja esa salida fija en el volumen que elegiste (80 % por
defecto) y lo vuelve a poner cada vez que cambia. El volumen de lo que escuchás se maneja desde Sonar.
El cartel de volumen de Windows sigue apareciendo al girar el dial.

Para ver qué hace el dial de otro auricular está `Diagnostico/`, una herramienta de consola aparte (solo lee):
`dotnet run --project Diagnostico`, girás el dial, Ctrl+C, y queda todo en `diagnostico-dial.log`.

## Arreglo de eco de Discord

Integrado desde [DiscordSonarEchoFix](https://github.com/codedbyyou/DiscordSonarEchoFix) 1.0.1 (MIT, ver
`EchoFix/LICENSE-DiscordSonarEchoFix.txt`). Cada 3 s mantiene a Discord silenciado en las salidas que no son
mezclas de Sonar, así el audio que captura el streaming no incluye las voces. Se prende/apaga desde el menú
de la bandeja ("Arreglo de eco de Discord") y usa el mismo `%APPDATA%\DiscordEchoFix\settings.json`
que el programa original, así que ya no hace falta tenerlo abierto aparte.

## Compilar y correr

1. Instalá el .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`
2. En esta carpeta: `dotnet run`
3. Para tener un .exe (necesita el .NET 8 Desktop Runtime instalado):
   `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
   → queda en `publish\SonarChatMix.exe`
   Si lo querés para gente que no tiene .NET, cambiá a `--self-contained true` (pesa bastante más).

## Que arranque con Windows

Agregá en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` un valor `SonarChatMix` con la ruta al .exe,
o Win+R → `shell:startup` → pegá ahí un acceso directo a `SonarChatMix.exe`.

## Asignarlo al teclado

En el software del teclado, podés asignar teclas Fn a los atajos que elijas en Opciones
(como pulsación de teclas / macro).

## Notas

- Usa la API local no oficial de Sonar (la misma que steelseries-sonar-py / SteelSeries-NET-API).
  Una actualización de GG la puede romper.
- GG tiene que estar abierto y el ChatMix habilitado en Sonar.
- El cartelito no se ve sobre juegos en pantalla completa exclusiva (sí en ventana sin bordes),
  pero los atajos funcionan igual.

## Licencia

MIT, ver `LICENSE`. El código de `EchoFix/` viene de DiscordSonarEchoFix (MIT, codedbyyou).
