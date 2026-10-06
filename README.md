# STUDIO V01

## ETAPA 3 — NDI
- Detección dinámica del NDI Runtime instalado en Windows.
- Detección y listado de fuentes NDI disponibles en la red.
- Botón de actualización de fuentes NDI.
- Nombre configurable para la futura SALIDA GENERAL NDI.
- La aplicación no incluye ni redistribuye el DLL propietario de NDI: utiliza el Runtime instalado en el equipo.


## ETAPA 2
- Panel de fuentes y fuente al aire
- Controles de reloj, clima y mosca
- Preview de programa
- Reproductor M3U8

## ETAPA 1 — desde cero

Base inicial:
- 4 fuentes RTSP
- 2 páginas HTML
- arranque automático
- cambio automático cada 20 segundos
- preview central
- 1920×1080 / 29.97 FPS
- sin botones de operación

Todavía NO incluye NDI, overlays, playlist/M3U8, YouTube, SRT ni stingers.

Requisitos:
- Windows
- .NET 8 SDK
- WebView2 Runtime
- FFmpeg

La configuración real de cámaras se mantiene local y no se publica en GitHub.
La versión final tendrá una pestaña para editar nombres y enlaces sin recompilar.
