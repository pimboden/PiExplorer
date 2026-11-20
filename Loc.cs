using System.Globalization;

namespace ImbotechConsulting.PiExplorer;

/// <summary>
/// Minimal localisation: English, German, Spanish. The setting is "auto" (Windows display language),
/// "en", "de" or "es". Components subscribe to <see cref="Changed"/> to refresh their texts live.
/// </summary>
internal static class Loc
{
    public static readonly string[] Languages = { "en", "de", "es" };

    public static event Action? Changed;

    /// <summary>"auto", "en", "de" or "es" – what the user chose.</summary>
    public static string Setting { get; private set; } = "auto";

    /// <summary>The language actually in use: "en", "de" or "es".</summary>
    public static string Current { get; private set; } = "en";

    public static void Apply(string setting)
    {
        Setting = Languages.Contains(setting) ? setting : "auto";
        Current = Setting == "auto" ? FromWindows() : Setting;
        Changed?.Invoke();
    }

    private static string FromWindows()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Contains(two) ? two : "en";
    }

    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var v)) return key;
        return v[Array.IndexOf(Languages, Current)];
    }

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    // key -> { English, German, Spanish }
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["app.title"] = new[] { "PiExplorer – Multi-pane file explorer", "PiExplorer – Datei-Explorer mit mehreren Bereichen", "PiExplorer – Explorador de archivos multipanel" },

        // menu
        ["menu.settings"] = new[] { "Settings", "Einstellungen", "Configuración" },
        ["menu.language"] = new[] { "Language", "Sprache", "Idioma" },
        ["lang.auto"] = new[] { "Automatic (Windows language)", "Automatisch (Windows-Sprache)", "Automático (idioma de Windows)" },

        // toolbar
        ["tb.newpane"] = new[] { "New pane (Ctrl+T)", "Neuer Bereich (Strg+T)", "Nuevo panel (Ctrl+T)" },
        ["tb.closepane"] = new[] { "Close pane (Ctrl+W)", "Bereich schließen (Strg+W)", "Cerrar panel (Ctrl+W)" },
        ["tb.copy"] = new[] { "Copy → other pane (F5)", "Kopieren → anderer Bereich (F5)", "Copiar → otro panel (F5)" },
        ["tb.move"] = new[] { "Move → other pane (F6)", "Verschieben → anderer Bereich (F6)", "Mover → otro panel (F6)" },
        ["tb.newfolder"] = new[] { "New folder (F7)", "Neuer Ordner (F7)", "Nueva carpeta (F7)" },
        ["tb.delete"] = new[] { "Delete (Del)", "Löschen (Entf)", "Eliminar (Supr)" },
        ["tb.foldersnew"] = new[] { "Folders open in new pane", "Ordner in neuem Bereich öffnen", "Abrir carpetas en un panel nuevo" },
        ["tb.hidden"] = new[] { "Show hidden", "Versteckte anzeigen", "Mostrar ocultos" },

        // messages
        ["msg.selectfirst"] = new[] { "Select one or more files or folders first.", "Wählen Sie zuerst eine oder mehrere Dateien oder Ordner aus.", "Seleccione primero uno o más archivos o carpetas." },
        ["msg.needsecond"] = new[] { "Open a second pane first (Ctrl+T).", "Öffnen Sie zuerst einen zweiten Bereich (Strg+T).", "Abra primero un segundo panel (Ctrl+T)." },
        ["msg.targetpc"] = new[] { "The target pane shows 'This PC'. Open a folder there first.", "Der Zielbereich zeigt „Dieser PC“. Öffnen Sie dort zuerst einen Ordner.", "El panel de destino muestra «Este equipo». Abra primero una carpeta allí." },
        ["err.nofolder"] = new[] { "Cannot find folder:\n{0}", "Ordner nicht gefunden:\n{0}", "No se encuentra la carpeta:\n{0}" },
        ["err.badname"] = new[] { "Invalid characters in name.", "Ungültige Zeichen im Namen.", "Caracteres no válidos en el nombre." },
        ["rename.title"] = new[] { "Rename", "Umbenennen", "Cambiar nombre" },

        // pane
        ["pane.back"] = new[] { "Back (Alt+Left)", "Zurück (Alt+Links)", "Atrás (Alt+Izquierda)" },
        ["pane.up"] = new[] { "Up one folder (Backspace)", "Eine Ebene nach oben (Rücktaste)", "Subir una carpeta (Retroceso)" },
        ["pane.refresh"] = new[] { "Refresh", "Aktualisieren", "Actualizar" },
        ["pane.dup"] = new[] { "Duplicate this pane", "Diesen Bereich duplizieren", "Duplicar este panel" },
        ["pane.grip"] = new[] { "Drag to move this pane to another position", "Ziehen, um diesen Bereich zu verschieben", "Arrastre para mover este panel a otra posición" },
        ["col.name"] = new[] { "Name", "Name", "Nombre" },
        ["col.size"] = new[] { "Size", "Größe", "Tamaño" },
        ["col.modified"] = new[] { "Modified", "Geändert am", "Modificado" },
        ["size.free"] = new[] { "free", "frei", "libres" },
        ["thispc"] = new[] { "This PC", "Dieser PC", "Este equipo" },
        ["status.items"] = new[] { "{0} items", "{0} Elemente", "{0} elementos" },
        ["status.sel"] = new[] { "{0} selected", "{0} ausgewählt", "{0} seleccionados" },
        ["newfolder"] = new[] { "New folder", "Neuer Ordner", "Nueva carpeta" },

        // context menu
        ["ctx.open"] = new[] { "Open", "Öffnen", "Abrir" },
        ["ctx.openhere"] = new[] { "Open folder in this pane", "Ordner in diesem Bereich öffnen", "Abrir carpeta en este panel" },
        ["ctx.opennew"] = new[] { "Open in new pane", "In neuem Bereich öffnen", "Abrir en un panel nuevo" },
        ["ctx.copy"] = new[] { "Copy to other pane   F5", "In anderen Bereich kopieren   F5", "Copiar al otro panel   F5" },
        ["ctx.move"] = new[] { "Move to other pane   F6", "In anderen Bereich verschieben   F6", "Mover al otro panel   F6" },
        ["ctx.rename"] = new[] { "Rename   F2", "Umbenennen   F2", "Cambiar nombre   F2" },
        ["ctx.delete"] = new[] { "Delete   Del", "Löschen   Entf", "Eliminar   Supr" },
        ["ctx.newfolder"] = new[] { "New folder   F7", "Neuer Ordner   F7", "Nueva carpeta   F7" },
        ["ctx.pin"] = new[] { "Pin to favorites", "An Favoriten anheften", "Anclar a favoritos" },
        ["ctx.unpin"] = new[] { "Unpin from favorites", "Von Favoriten lösen", "Quitar de favoritos" },

        // navigation pane
        ["nav.header"] = new[] { "Navigation", "Navigation", "Navegación" },
        ["nav.pin"] = new[] { " Pin current folder", " Aktuellen Ordner anheften", " Anclar carpeta actual" },
        ["nav.home"] = new[] { "Home", "Start", "Inicio" },
        ["nav.gallery"] = new[] { "Gallery", "Galerie", "Galería" },
        ["nav.pinned"] = new[] { "Pinned", "Angeheftet", "Anclado" },
        ["nav.locations"] = new[] { "Locations", "Orte", "Ubicaciones" },
        ["nav.thispctip"] = new[] { "Drives and libraries", "Laufwerke und Bibliotheken", "Unidades y bibliotecas" },
        ["lib.desktop"] = new[] { "Desktop", "Desktop", "Escritorio" },
        ["lib.documents"] = new[] { "Documents", "Dokumente", "Documentos" },
        ["lib.downloads"] = new[] { "Downloads", "Downloads", "Descargas" },
        ["lib.music"] = new[] { "Music", "Musik", "Música" },
        ["lib.pictures"] = new[] { "Pictures", "Bilder", "Imágenes" },
        ["lib.videos"] = new[] { "Videos", "Videos", "Vídeos" },
        ["drive.network"] = new[] { "Network drive", "Netzlaufwerk", "Unidad de red" },
        ["drive.local"] = new[] { "Local Disk", "Lokaler Datenträger", "Disco local" },
        ["drive.generic"] = new[] { "Drive", "Laufwerk", "Unidad" },
    };
}
