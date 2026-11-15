# Notas Escritorio

Panel de notas tipo widget para Windows 11.

| Qué | Cómo funciona |
|---|---|
| Borradores | Lo que escribes se queda en el panel (caché) aunque cierres o apagues el PC, hasta que quites la nota |
| Guardar | El `.txt` solo se crea al pulsar el botón de guardar de la nota (o Ctrl+S). Se guarda en la carpeta elegida en la tuerca |
| Abrir | Botón **Abrir** (o Ctrl+O) para traer uno o varios `.txt` que ya tengas |
| Color | Todas las notas son **blancas** por defecto (se puede cambiar con el botón de la paleta) |
| Diseño | Sin bordes redondeados, pegado arriba y al borde de la pantalla |
| Barra lateral | Todas las opciones (nueva nota, lista, abrir, formato, fuente, tema, opciones) en la barra vertical de la izquierda |
| Ordenar | Agarra una nota por su borde (o por el icono de mover) y suéltala donde quieras |
| Temas | Botón de la paleta abajo en la barra: Claro, Rosa pastel, Lavanda, Menta, Cielo, Arena, Melocotón, Oscuro, Noche azul, Vidrio |
| Ancho | Arrastra el borde interior del panel para ensancharlo o estrecharlo |
| Barra de tareas | El panel ocupa solo el alto libre, nunca pasa por encima de la barra |
| Widget | Queda fijo en el escritorio y siempre **por debajo** de las demás ventanas. Sigue visible con Win+D |

## Cada nota
- Cada tarjeta tiene un **marco** (color del tema) y dentro la **hoja** blanca donde se escribe.
- **Punto gris** arriba a la izquierda del marco: la nota está sin guardar o tiene cambios pendientes.
- Abajo: **guardar** · **color** · **quitar del panel** (el `.txt` guardado no se borra).
- Guardar crea `Título.txt` en la carpeta de guardado; las siguientes veces guarda los cambios en el mismo archivo.
- La carpeta de guardado se cambia en la **tuerca** → *Cambiar carpeta de guardado…* (por defecto `Documentos\Notas Escritorio`).
- La **fuente** y su tamaño se cambian con el botón de fuente de la barra lateral.

## Barra de formato
Un `.txt` no guarda estilos, así que el formato se escribe con marcas de texto (Markdown), que se leen bien en cualquier editor:

| Botón | Resultado en el .txt |
|---|---|
| H1 ▾ | `# Título 1`, `## Título 2`, `### Título 3` |
| Listas ▾ | `- viñeta`, `1. numerada`, `- [ ] casilla` |
| **B** / *I* / ~~S~~ | `**negrita**`, `*cursiva*`, `~~tachado~~` |
| Enlace | `[texto](https://...)` |
| Tabla ▾ | tabla con `| columnas |` |

Selecciona texto dentro de una nota y pulsa el botón. Pulsarlo otra vez quita el formato.

## Usarla
1. Doble clic en `NotasEscritorio.exe`. Si Windows muestra "Windows protegió tu PC", pulsa **Más información → Ejecutar de todas formas**.
2. Para cambiar a la versión nueva: clic derecho en el icono junto al reloj → **Salir**, y abre el `.exe` nuevo.
3. Se abre sola al encender el PC (se desactiva en `···` → *Iniciar con Windows*).

## Compilar de nuevo (solo si cambias el código)
1. Instala el SDK de .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0
2. Doble clic en `src\compilar.bat`. El `.exe` queda en `src\salida`.
