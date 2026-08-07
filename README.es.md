# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager (PZASM) es un gestor local para Project Zomboid y su servidor dedicado. Distribuye un conjunto coherente de mods mediante **un único Workshop ID**, de modo que el servidor sincronice el paquete completo y no cada elemento de origen por separado.

> Estado: versión funcional para Windows y Linux. Incluye Bundle, instantáneas fijadas, catálogo Workshop interno, SteamCMD, planificación autónoma o coordinada, aviso de conexión, gestión del servidor y CLI sin interfaz. Prueba siempre la primera publicación con un elemento privado.

## Veredicto técnico

Un elemento de Workshop puede contener varias carpetas bajo `mods/`, cada una con su propio `mod.info` e `id=`:

```ini
WorkshopItems=ID_UNICO_DEL_PAQUETE
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIX
```

Servidor y clientes comparan la versión del único elemento Workshop. Después, los Mod IDs internos controlan la carga. Las comprobaciones normales de Lua y checksum siguen activas.

El modo recomendado es **Bundle**, que conserva carpetas y Mod IDs originales. **Strict Fusion** crea un solo Mod ID, pero rechaza cualquier colisión entre archivos diferentes.

Consulta el [estudio completo de arquitectura](docs/ARCHITECTURE.es.md).

## Funciones principales

- detección del juego, servidor dedicado, bibliotecas Steam, SteamCMD y mods locales/Workshop;
- compatibilidad con estructuras Build 41/42 y carpetas de versión;
- proyectos independientes y reutilizables, cada uno con su GUID y Workshop ID;
- instantáneas privadas SHA-256 para fijar exactamente las versiones de origen;
- importación por Workshop ID y adición de dependencias `require=` disponibles;
- catálogo Workshop interno con búsqueda, ordenación, etiquetas, vistas previas, paginación, acceso directo por ID y cesta de selección persistente entre páginas con eliminación individual;
- selector visual compartido para packs y listas `WorkshopItems`/`Mods` de servidores locales o dedicados, conservando la edición en bruto;
- instalación portátil de SteamCMD en un clic desde Valve para Windows y Linux, también mediante `pzasm steamcmd install`;
- descarga anónima de fuentes públicas del Workshop, separada de la cuenta autenticada de publicación;
- Bundle sin reescritura de manifest, Lua, scripts, mapas ni recursos;
- Strict Fusion con deduplicación de archivos idénticos e informe de conflictos;
- descripción Workshop, manifiesto público y lockfile exhaustivos;
- registro de autores, licencias, permisos y pruebas privadas no publicadas;
- ventana de conexión opcional, activada por defecto;
- creación y actualización posterior del mismo elemento Workshop;
- espacio de proyecto moderno con pestañas, ajustes guiados y controles expertos de valores exactos;
- asistente de prioridad de mapas basado en `map.info`, dependencias `lots=`, conflictos de celdas `.lotheader`, arrastrar y soltar y edición manual de `Map=`;
- editor guiado del servidor para identidad, acceso, RCON, sesión, copias y contenido, además del editor INI completo;
- parada RCON mediante `save`/`quit` y reinicio coordinado;
- UI local y CLI headless para Windows y Linux;
- daemon `automation run` con bloqueos entre procesos.

## Inicio

Para compilar se necesita el [SDK .NET 9](https://dotnet.microsoft.com/download/dotnet/9.0). Los artefactos autónomos de CI no necesitan el runtime.

```powershell
Start-PZASM.cmd
```

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

La UI escucha localmente en `http://localhost:5160`. Usa `--data-root <ruta>` para compartir un directorio de datos entre la UI y el CLI.
SteamCMD se instala desde el panel o la pestaña Distribución. Las fuentes públicas de Project Zomboid se descargan anónimamente por defecto; solo la publicación necesita la cuenta editora.

SteamCMD descarga IDs conocidos, pero no ofrece una búsqueda completa. El catálogo interno enumera resultados públicos de Steam Community, obtiene sus metadatos públicos y entrega la selección a SteamCMD. La publicación programada no requiere un servidor local; la coordinación RCON es opcional.

## Flujo recomendado

1. Crea un proyecto en modo **Bundle**.
2. Añade mods detectados o importa un Workshop ID.
3. Registra autor y autorización de cada origen.
4. Revisa el orden de mods y mapas.
5. Construye y examina `pack.lock.json` y `server-config.txt`.
6. Instala SteamCMD en un clic, configura la cuenta editora y publica primero como privado.
7. Prueba en un servidor de staging antes de producción.

## CLI headless

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Servidor principal"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Cada proyecto es un paquete global independiente. Nada se actualiza automáticamente hasta que el administrador activa la automatización. Hay unidades systemd de referencia en `deploy/systemd/`.

## Derechos y responsabilidad

PZASM no concede derechos sobre los mods incluidos. La [política oficial de Project Zomboid](https://projectzomboid.com/blog/modding-policy/) exige permisos adecuados y una lista completa para paquetes públicos o no listados. Steam también exige aceptar su [acuerdo de Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

El creador y publicador del paquete es el único responsable de permisos, licencias, créditos y contenido de terceros. LemonCorp y los colaboradores de PZASM no son responsables de los paquetes creados o publicados por los usuarios.

## Desarrollo

El repositorio incluye un `Justfile` multiplataforma. Instala [just](https://github.com/casey/just) y ejecuta:

```text
just                 # mostrar todas las recetas
just check           # comprobar formato, compilar Release y ejecutar pruebas
just build           # compilar toda la solución
just test            # ejecutar todas las pruebas
just run-ui           # iniciar la UI y abrir el navegador
just run-cli help     # ejecutar un comando CLI
just automation      # iniciar el planificador headless
just publish          # publicar para el sistema actual
just publish-all      # publicar win-x64 y linux-x64
```

Las variables `CONFIGURATION` y `PUBLISH_DIR` cambian la configuración `Release` y el directorio `publish` predeterminados. Las recetas también aceptan argumentos adicionales.

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

No expongas el puerto de PZASM a Internet: la interfaz es una herramienta de administración local sin autenticación de red.
