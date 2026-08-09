# Arquitectura y estudio de viabilidad

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## Conclusión

Project Zomboid puede cargar varios mods lógicos desde un solo elemento Workshop:

```text
un PublishedFileId de Workshop
└── mods/
    ├── ModA/          → mod.info: id=ModA
    ├── ModB/          → mod.info: id=ModB
    └── PZASM_Notice/  → mod.info: id=PZASM_Notice_SUFFIX
```

El juego ve varios **Mod IDs**, pero solo **un Workshop ID que sincronizar**. Esto resuelve el desfase de versiones sin los riesgos de mezclar físicamente todos los archivos.

## Comprobación cliente/servidor

La inspección local de la versión 42.20.2 muestra dos fases: primero se comparan los IDs y marcas de tiempo de Workshop; después se carga `Mods=` por Mod ID. Los submods de un mismo elemento no reciben una marca de tiempo Workshop independiente.

Las comprobaciones normales, incluido `DoLuaChecksum`, siguen activas. Este comportamiento debe volver a probarse después de actualizaciones importantes del juego.

## Estructura y conflictos

```text
steamapps/workshop/content/108600/<WorkshopId>/
└── mods/<CarpetaLogica>/
    ├── mod.info
    ├── media/
    ├── common/mod.info + media/
    └── 42.x/mod.info + media/
```

`media` puede contener Lua, scripts, mapas, texturas, modelos, animaciones, sonidos, radios, traducciones e interfaz. Dos mods pueden reutilizar globals Lua, IDs de scripts, celdas de mapa, nombres de recursos o claves de traducción. Cambiar solo las rutas no corrige las referencias internas.

## Modos

**Bundle**, recomendado, conserva carpetas y Mod IDs originales bajo un Workshop ID. Mantiene la mayor compatibilidad con dependencias y API de mods.

**Strict Fusion**, avanzado, genera `PZASM_Pack_<suffix>`, combina el contenido efectivo, deduplica archivos idénticos y bloquea cualquier colisión distinta. Solo es apropiado para conjuntos controlados y probados.

## Proyectos y versiones fijadas

Cada proyecto tiene un GUID inmutable y su propio `publishedfileid`. El valor `0` crea un elemento; SteamCMD escribe el nuevo ID y PZASM lo conserva para las actualizaciones siguientes.

Al añadir una fuente, PZASM crea una instantánea privada y calcula su SHA-256. Los builds usan esa copia fijada, no la caché mutable de Steam. La actualización de fuentes es explícita y reemplaza las instantáneas de forma atómica. `pack.lock.json` describe exactamente el contenido entregado.

## Publicación y servidor

La [guía Steamworks Workshop](https://partner.steamgames.com/doc/features/workshop/implementation) documenta la creación y actualización mediante `workshop_build_item`.

La publicación es incremental en dos niveles. PZASM calcula por separado las huellas del contenido entregado, los metadatos y la vista previa, y omite del VDF las dimensiones sin cambios. SteamCMD y Steam comparan después el manifiesto enviado con el anterior y solo transfieren los chunks ausentes. PZASM nunca vuelve a descargar el paquete tras la subida.

Un resultado «sin cambios» exige las tres huellas locales y una nueva lectura por la API pública de los identificadores remotos de contenido y vista previa, el tamaño, la hora de actualización, el título, la descripción y la visibilidad. Si falta alguna prueba o está obsoleta, se realiza una publicación conservadora. El modo forzado envía todas las dimensiones a SteamCMD, pero Steam reutiliza los chunks idénticos. El código de proceso `0` no basta: la actividad actual debe confirmar explícitamente `Upload finished ... : OK`, y cualquier error explícito del Workshop prevalece.

El servidor coordinado permanece en línea durante la compilación y toda la subida. Si cambió el contenido entregado, el gestor espera tras la confirmación el plazo configurado — cinco minutos como mínimo —, envía `save` y `quit` y aplica la estrategia de reinicio. Un no-change verificado o un cambio solo de metadatos o vista previa no reinicia el servidor.

El planificador informa de los permisos, valida las dependencias, actualiza opcionalmente las fuentes, construye, publica y coordina el servidor por RCON cuando procede. Un inicio de sesión supervisado envía la contraseña por la entrada estándar de SteamCMD sin guardarla. Una cuenta sin Steam Guard continúa directamente. Para una cuenta protegida, SteamCMD envía una solicitud de aprobación a Steam Mobile y la comprueba automáticamente mientras la interfaz muestra la espera activa. El código actual solo se solicita si la aprobación caduca o si el usuario elige esta alternativa; PZASM reintenta entonces con el comando documentado `set_steam_guard_code`, también por la entrada estándar. Steam ofrece el QR en su cliente y páginas web, pero SteamCMD no expone ninguna carga QR ni comando de inicio de sesión QR documentado, por lo que un QR web separado no puede establecer la sesión de publicación. SteamCMD conserva su propio token en la carpeta portátil; las publicaciones manuales y programadas usan únicamente esa sesión. El gestor solo registra la hora de la última verificación. Una sesión caducada solicita reconexión sin quedar esperando una entrada invisible. La interfaz transmite el progreso en directo, impone un tiempo máximo y puede cancelar el proceso externo.

SteamCMD abre una sesión de Steam independiente, por lo que la automatización debe usar una cuenta de publicación dedicada que posea Project Zomboid y no la cuenta activa en el cliente de escritorio. El primer acceso crea el token portátil; las comprobaciones posteriores usan `steamcmd verify`, sin contraseña ni token nuevo. PZASM nunca importa cookies ni archivos de acceso del cliente Steam. Publicar mediante la sesión del cliente exigiría una aplicación Steamworks autorizada: el editor de Project Zomboid debe añadir el AppID de la herramienta a los App Publish Permissions del Workshop para `ISteamUGC`, mientras OAuth requiere un cliente asignado por Valve con acceso `write_cloud` limitado al AppID. Una herramienta externa no puede concederse ninguno de esos permisos.

## Aplicación externa

Un mod ejecutado dentro del juego no puede administrar de forma fiable SteamCMD, horarios sin el juego, archivos privados ni varios perfiles de servidor. Por eso PZASM usa una aplicación ASP.NET Core local y un CLI headless con el mismo núcleo. Solo el aviso Lua generado se ejecuta en Project Zomboid.

## Seguridad y derechos

La [política oficial](https://projectzomboid.com/blog/modding-policy/) se presenta al administrador, que sigue siendo el único responsable de sus decisiones. Los estados de permiso, las pruebas y la confirmación de lectura son únicamente informativos: nunca bloquean la compilación, publicación o automatización. Las situaciones desconocidas, sin prueba o rechazadas siguen mostrándose claramente; las pruebas privadas no entran en `Contents` y la descripción pública enumera todas las fuentes.

Steam puede ocultar un elemento nuevo hasta aceptar el [acuerdo de Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

## Riesgos restantes

- cambios futuros del protocolo o de Build 42;
- cambios de Mod ID, dependencia, mapa o licencia;
- dependencias no declaradas y orden manual de mapas;
- conflictos lógicos que no pueden detectarse estáticamente;
- intervención ocasional de SteamCMD;
- reinicio necesario solo cuando cambia el contenido entregado, después de confirmar la subida y esperar el plazo configurado.

## Orquestación local y remota

Un perfil representa un INI local o una conexión a un VPS/servidor dedicado remoto. Un perfil remoto puede usar solo RCON; SSH y la gestión del INI son opcionales. El estado realiza una autenticación RCON real, la consola envía los comandos de administración admitidos y la parada limpia usa `save` y después `quit`.

Los perfiles locales tienen un modo de ejecución explícito. Un perfil **Host local** se inicia desde el menú Host del cliente y usa un proceso `zombie.network.GameServer -coop` y `coop-console.txt`. Un perfil **Dedicated local** se inicia mediante la herramienta Steam separada Project Zomboid Dedicated Server (AppID 380870) y usa `server-console.txt`. Ambos modos comparten deliberadamente los archivos nativos `Zomboid/Server/<nombre>.ini`; el gestor guarda por separado el uso elegido. Un auxiliar `-coop` solo cuenta como servidor activo con progreso reciente válido o un marcador de servidor listo; un fallo posterior lo descarta sin crear un falso conflicto.

Con systemd, Docker, un panel de alojamiento u otro supervisor que reinicie Project Zomboid tras `quit`, un perfil RCON-only puede coordinar la publicación: primero termina la subida al Workshop y después el gestor envía `save` y `quit`. SSH queda limitado a la gestión INI opcional o a un comando explícito que inicie solo el juego. Se rechazan `reboot`, `shutdown` y `poweroff` del host. El secreto RCON se guarda en los datos locales del gestor para automatizar estas operaciones, por lo que ese directorio debe protegerse.
