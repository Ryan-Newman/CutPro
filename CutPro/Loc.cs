using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace CutPro;

/// <summary>
/// Interface language: English text is the key; a table gives German, Spanish and French for the menus, panel headings, buttons, dialogs,
/// the command palette and the shortcut list. <see cref="Apply"/> walks a window / menu / dialog and swaps every fixed string it knows (bound
/// text and text boxes — user content — are never touched); it is stateless (a translated string maps back to its English key), so switching
/// language again just walks again. Strings built at run time (status messages, clip-dependent text) stay English.
/// Also accessibility: icon-only buttons get their tooltip as their automation name (<see cref="Name"/>).
/// </summary>
public static class Loc
{
    public static readonly (string code, string name)[] Languages = { ("en", "English"), ("de", "Deutsch"), ("es", "Español"), ("fr", "Français") };
    public static string Current { get; private set; } = "en";
    static int Col => Current switch { "de" => 1, "es" => 2, "fr" => 3, _ => 0 };

    // English | Deutsch | Español | Français
    static readonly string[] rows =
    {
        // menu bar
        "File|Datei|Archivo|Fichier", "Edit|Bearbeiten|Edición|Édition", "View|Ansicht|Ver|Affichage", "Sequence|Sequenz|Secuencia|Séquence",
        "Markers|Marken|Marcadores|Marqueurs", "Publish|Veröffentlichen|Publicar|Publier", "Window|Fenster|Ventana|Fenêtre", "Help|Hilfe|Ayuda|Aide",
        // File
        "Import Media…|Medien importieren…|Importar medios…|Importer des médias…", "Open Project…|Projekt öffnen…|Abrir proyecto…|Ouvrir un projet…",
        "Save Project|Projekt speichern|Guardar proyecto|Enregistrer le projet", "Relink Offline Media…|Offline-Medien neu verknüpfen…|Volver a vincular medios sin conexión…|Relier les médias hors ligne…",
        "Collect Files…|Dateien sammeln…|Recopilar archivos…|Rassembler les fichiers…", "Export…|Exportieren…|Exportar…|Exporter…", "Publish…|Veröffentlichen…|Publicar…|Publier…",
        "Exit|Beenden|Salir|Quitter", "Export MP4…|MP4 exportieren…|Exportar MP4…|Exporter en MP4…",
        // Edit
        "Undo|Rückgängig|Deshacer|Annuler", "Redo|Wiederholen|Rehacer|Rétablir", "History|Protokoll|Historial|Historique", "Cut|Ausschneiden|Cortar|Couper",
        "Copy|Kopieren|Copiar|Copier", "Paste|Einfügen|Pegar|Coller", "Paste Insert|Einfügen und verschieben|Pegar e insertar|Coller avec insertion",
        "Paste Attributes…|Attribute einfügen…|Pegar atributos…|Coller les attributs…", "Remove Attributes…|Attribute entfernen…|Eliminar atributos…|Supprimer les attributs…",
        "Add Edit|Schnitt hinzufügen|Añadir edición|Ajouter un montage", "Add Edit to All Tracks|Schnitt auf allen Spuren|Añadir edición a todas las pistas|Ajouter un montage sur toutes les pistes",
        "Add Edit (cut at playhead)|Schnitt hinzufügen (an der Abspielposition)|Añadir edición (en el cabezal)|Ajouter un montage (à la tête de lecture)",
        "Delete|Löschen|Eliminar|Supprimer", "Ripple Delete|Lückenlos löschen|Eliminar con rizo|Supprimer et raccorder",
        "Select All|Alles auswählen|Seleccionar todo|Tout sélectionner", "Deselect All|Auswahl aufheben|Anular selección|Tout désélectionner",
        "Link / Unlink|Verbinden / Trennen|Vincular / Desvincular|Lier / Délier", "Link / Unlink Clips|Clips verbinden / trennen|Vincular / desvincular clips|Lier / délier les plans",
        "Link|Verbinden|Vincular|Lier", "Unlink|Trennen|Desvincular|Délier", "Group|Gruppieren|Agrupar|Grouper", "Ungroup|Gruppierung aufheben|Desagrupar|Dissocier",
        "Mark In|In-Punkt setzen|Marcar entrada|Marquer l'entrée", "Mark Out|Out-Punkt setzen|Marcar salida|Marquer la sortie", "Clear Marks|Marken löschen|Borrar marcas|Effacer les points",
        "Lift|Herausnehmen|Levantar|Lever", "Extract|Extrahieren|Extraer|Extraire", "Lift In–Out|In–Out herausnehmen|Levantar entrada–salida|Lever entrée–sortie",
        "Extract In–Out|In–Out extrahieren|Extraer entrada–salida|Extraire entrée–sortie",
        "Insert from Source|Aus Quelle einfügen|Insertar desde el origen|Insérer depuis la source", "Overwrite from Source|Aus Quelle überschreiben|Sobrescribir desde el origen|Remplacer depuis la source",
        "Match Frame|Frame abgleichen|Coincidir fotograma|Image correspondante", "Reveal in Project|Im Projekt anzeigen|Mostrar en el proyecto|Afficher dans le projet",
        "Reveal in File Explorer|Im Datei-Explorer anzeigen|Mostrar en el Explorador de archivos|Afficher dans l'Explorateur de fichiers",
        "Keyboard Shortcuts…|Tastenkombinationen…|Métodos abreviados de teclado…|Raccourcis clavier…", "Keyboard Shortcuts|Tastenkombinationen|Métodos abreviados de teclado|Raccourcis clavier",
        "Ripple Trim Previous Edit to Playhead|Vorherigen Schnitt bis zur Abspielposition trimmen|Recortar con rizo la edición anterior hasta el cabezal|Raccorder le montage précédent à la tête de lecture",
        "Ripple Trim Next Edit to Playhead|Nächsten Schnitt bis zur Abspielposition trimmen|Recortar con rizo la edición siguiente hasta el cabezal|Raccorder le montage suivant à la tête de lecture",
        "Trim In Point by 1 Frame|In-Punkt um 1 Frame kürzen|Recortar el punto de entrada 1 fotograma|Raccorder le point d'entrée d'1 image",
        "Trim Out Point by 1 Frame|Out-Punkt um 1 Frame kürzen|Recortar el punto de salida 1 fotograma|Raccorder le point de sortie d'1 image",
        "Trim In Point by 5 Frames|In-Punkt um 5 Frames kürzen|Recortar el punto de entrada 5 fotogramas|Raccorder le point d'entrée de 5 images",
        "Trim Out Point by 5 Frames|Out-Punkt um 5 Frames kürzen|Recortar el punto de salida 5 fotogramas|Raccorder le point de sortie de 5 images",
        // markers
        "Add Marker|Marke hinzufügen|Añadir marcador|Ajouter un marqueur", "Add Clip Marker|Clipmarke hinzufügen|Añadir marcador de clip|Ajouter un marqueur de plan",
        "Edit Marker…|Marke bearbeiten…|Editar marcador…|Modifier le marqueur…", "Edit Marker at the Playhead…|Marke an der Abspielposition bearbeiten…|Editar marcador en el cabezal…|Modifier le marqueur à la tête de lecture…",
        "Go to Next Marker|Zur nächsten Marke|Ir al marcador siguiente|Aller au marqueur suivant", "Go to Previous Marker|Zur vorherigen Marke|Ir al marcador anterior|Aller au marqueur précédent",
        "Clear All Markers|Alle Marken löschen|Borrar todos los marcadores|Effacer tous les marqueurs", "Copy YouTube Chapters|YouTube-Kapitel kopieren|Copiar capítulos de YouTube|Copier les chapitres YouTube",
        "Delete Marker|Marke löschen|Eliminar marcador|Supprimer le marqueur", "Chapter|Kapitel|Capítulo|Chapitre", "Colour|Farbe|Color|Couleur", "Marker|Marke|Marcador|Marqueur",
        "Comments|Kommentare|Comentarios|Commentaires", "Duration (s)|Dauer (s)|Duración (s)|Durée (s)", "Name|Name|Nombre|Nom",
        "Green|Grün|Verde|Vert", "Red|Rot|Rojo|Rouge", "Purple|Lila|Morado|Violet", "Orange|Orange|Naranja|Orange", "Yellow|Gelb|Amarillo|Jaune", "White|Weiß|Blanco|Blanc",
        "Blue|Blau|Azul|Bleu", "Cyan|Cyan|Cian|Cyan",
        // View
        "Command Palette…|Befehlspalette…|Paleta de comandos…|Palette de commandes…", "Project|Projekt|Proyecto|Projet", "Titles|Titel|Títulos|Titres", "Effects|Effekte|Efectos|Effets",
        "Audio Track Mixer|Audiospurmischer|Mezclador de pistas de audio|Mixeur de pistes audio", "Lumetri Scopes|Lumetri-Scopes|Ámbitos Lumetri|Moniteurs Lumetri",
        "Captions|Untertitel|Subtítulos|Sous-titres", "Settings|Einstellungen|Configuración|Paramètres", "Side Bar|Seitenleiste|Barra lateral|Barre latérale",
        "Effect Controls|Effekteinstellungen|Controles de efectos|Options d'effet", "Color Theme|Farbdesign|Tema de color|Thème de couleur", "Source|Quelle|Origen|Source",
        "Program|Programm|Programa|Programme", "Timeline|Zeitleiste|Línea de tiempo|Montage", "Show History|Protokoll anzeigen|Mostrar historial|Afficher l'historique",
        "Show Markers|Markierungen anzeigen|Mostrar marcadores|Afficher les marqueurs", "Markers|Markierungen|Marcadores|Marqueurs",
        "Toggle Side Bar|Seitenleiste ein/aus|Mostrar/ocultar barra lateral|Afficher/masquer la barre latérale",
        "Toggle Effect Controls|Effekteinstellungen ein/aus|Mostrar/ocultar controles de efectos|Afficher/masquer les options d'effet",
        "Full-Screen Playback|Vollbildwiedergabe|Reproducción a pantalla completa|Lecture plein écran",
        "Maximize Panel Under the Mouse|Bedienfeld unter der Maus maximieren|Maximizar el panel bajo el ratón|Agrandir le panneau sous la souris",
        // Sequence
        "New Sequence…|Neue Sequenz…|Nueva secuencia…|Nouvelle séquence…", "Duplicate Sequence|Sequenz duplizieren|Duplicar secuencia|Dupliquer la séquence",
        "Rename Sequence…|Sequenz umbenennen…|Cambiar nombre de secuencia…|Renommer la séquence…", "Delete Sequence…|Sequenz löschen…|Eliminar secuencia…|Supprimer la séquence…",
        "Open Sequence|Sequenz öffnen|Abrir secuencia|Ouvrir la séquence", "Go to Gap|Zur Lücke|Ir al hueco|Aller à l'espace",
        "Next in Sequence|Nächste in der Sequenz|Siguiente en la secuencia|Suivant dans la séquence", "Previous in Sequence|Vorherige in der Sequenz|Anterior en la secuencia|Précédent dans la séquence",
        "Next in Track|Nächste in der Spur|Siguiente en la pista|Suivant dans la piste", "Previous in Track|Vorherige in der Spur|Anterior en la pista|Précédent dans la piste",
        "Go to Next Gap in Sequence|Zur nächsten Lücke in der Sequenz|Ir al siguiente hueco de la secuencia|Aller à l'espace suivant de la séquence",
        "Go to Previous Gap in Sequence|Zur vorherigen Lücke in der Sequenz|Ir al hueco anterior de la secuencia|Aller à l'espace précédent de la séquence",
        "Go to Next Gap in Track|Zur nächsten Lücke in der Spur|Ir al siguiente hueco de la pista|Aller à l'espace suivant de la piste",
        "Go to Previous Gap in Track|Zur vorherigen Lücke in der Spur|Ir al hueco anterior de la pista|Aller à l'espace précédent de la piste",
        "Zoom|Zoom|Zoom|Zoom", "Zoom In|Vergrößern|Acercar|Zoom avant", "Zoom Out|Verkleinern|Alejar|Zoom arrière", "Zoom to Sequence|Auf Sequenz zoomen|Ajustar a la secuencia|Zoom sur la séquence",
        "Zoom to Selection|Auf Auswahl zoomen|Ajustar a la selección|Zoom sur la sélection",
        "Layout (split screen / picture in picture)|Layout (geteilter Bildschirm / Bild im Bild)|Diseño (pantalla dividida / imagen en imagen)|Disposition (écran partagé / incrustation)",
        "Add Tracks…|Spuren hinzufügen…|Añadir pistas…|Ajouter des pistes…", "Delete Empty Tracks|Leere Spuren löschen|Eliminar pistas vacías|Supprimer les pistes vides",
        "Add Adjustment Layer|Einstellungsebene hinzufügen|Añadir capa de ajuste|Ajouter un calque de réglage", "Nest…|Verschachteln…|Anidar…|Imbriquer…",
        "Open Nested Sequence|Verschachtelte Sequenz öffnen|Abrir secuencia anidada|Ouvrir la séquence imbriquée", "Sequence Settings…|Sequenzeinstellungen…|Ajustes de secuencia…|Paramètres de séquence…",
        "Render Previews|Vorschau rendern|Renderizar vistas previas|Rendre les prévisualisations",
        "Playback Uses Render Previews|Wiedergabe nutzt gerenderte Vorschau|La reproducción usa las vistas previas|La lecture utilise les prévisualisations",
        "Auto Reframe|Automatisch neu framen|Reencuadre automático|Recadrage automatique", "Speed/Duration…|Geschwindigkeit/Dauer…|Velocidad/duración…|Vitesse/durée…",
        "Add Frame Hold|Standbild hinzufügen|Añadir fotograma congelado|Ajouter un arrêt sur image", "Insert Frame Hold Segment|Standbildsegment einfügen|Insertar segmento de fotograma congelado|Insérer un segment d'arrêt sur image",
        "Remove Frame Hold|Standbild entfernen|Quitar fotograma congelado|Supprimer l'arrêt sur image", "Audio Track Mixer…|Audiospurmischer…|Mezclador de pistas de audio…|Mixeur de pistes audio…",
        "Effects (transitions)…|Effekte (Übergänge)…|Efectos (transiciones)…|Effets (transitions)…",
        "Apply Default Transitions to Selection|Standardübergänge auf die Auswahl anwenden|Aplicar transiciones predeterminadas a la selección|Appliquer les transitions par défaut à la sélection",
        "Apply Default Transitions|Standardübergänge anwenden|Aplicar transiciones predeterminadas|Appliquer les transitions par défaut",
        "Apply Video Transition|Videoübergang anwenden|Aplicar transición de vídeo|Appliquer une transition vidéo", "Apply Audio Transition|Audioübergang anwenden|Aplicar transición de audio|Appliquer une transition audio",
        "Picture in picture: top left|Bild im Bild: oben links|Imagen en imagen: arriba a la izquierda|Incrustation : en haut à gauche",
        "Picture in picture: top right|Bild im Bild: oben rechts|Imagen en imagen: arriba a la derecha|Incrustation : en haut à droite",
        "Picture in picture: bottom left|Bild im Bild: unten links|Imagen en imagen: abajo a la izquierda|Incrustation : en bas à gauche",
        "Picture in picture: bottom right|Bild im Bild: unten rechts|Imagen en imagen: abajo a la derecha|Incrustation : en bas à droite",
        "Split: side by side (2)|Geteilt: nebeneinander (2)|Dividida: lado a lado (2)|Partagé : côte à côte (2)", "Split: top and bottom (2)|Geteilt: oben und unten (2)|Dividida: arriba y abajo (2)|Partagé : haut et bas (2)",
        "Split: three across (3)|Geteilt: drei nebeneinander (3)|Dividida: tres en fila (3)|Partagé : trois côte à côte (3)", "Grid: 2 × 2 (4)|Raster: 2 × 2 (4)|Cuadrícula: 2 × 2 (4)|Grille : 2 × 2 (4)",
        "Full frame (reset)|Vollbild (zurücksetzen)|Fotograma completo (restablecer)|Plein cadre (réinitialiser)",
        // timeline menus
        "Transition|Übergang|Transición|Transition", "Duration|Dauer|Duración|Durée", "Alignment|Ausrichtung|Alineación|Alignement", "Center at Cut|Am Schnitt zentrieren|Centrar en el corte|Centrer sur la coupe",
        "Start at Cut|Am Schnitt beginnen|Empezar en el corte|Commencer à la coupe", "End at Cut|Am Schnitt enden|Terminar en el corte|Finir à la coupe",
        "Delete Transition|Übergang löschen|Eliminar transición|Supprimer la transition", "Replace / Relink Media…|Medien ersetzen / neu verknüpfen…|Reemplazar / volver a vincular medios…|Remplacer / relier le média…",
        "Properties…|Eigenschaften…|Propiedades…|Propriétés…", "Properties|Eigenschaften|Propiedades|Propriétés",
        "Add Video Track Above|Videospur darüber hinzufügen|Añadir pista de vídeo encima|Ajouter une piste vidéo au-dessus", "Add Video Track Below|Videospur darunter hinzufügen|Añadir pista de vídeo debajo|Ajouter une piste vidéo en dessous",
        "Add Audio Track Above|Audiospur darüber hinzufügen|Añadir pista de audio encima|Ajouter une piste audio au-dessus", "Add Audio Track Below|Audiospur darunter hinzufügen|Añadir pista de audio debajo|Ajouter une piste audio en dessous",
        "Rename…|Umbenennen…|Cambiar nombre…|Renommer…", "Lock Track|Spur sperren|Bloquear pista|Verrouiller la piste", "Delete Track|Spur löschen|Eliminar pista|Supprimer la piste",
        "Track Height|Spurhöhe|Altura de pista|Hauteur de piste", "All Tracks|Alle Spuren|Todas las pistas|Toutes les pistes", "Small|Klein|Pequeña|Petite", "Medium|Mittel|Mediana|Moyenne",
        "Large|Groß|Grande|Grande", "Extra large|Sehr groß|Muy grande|Très grande",
        // Project panel menu
        "Open in Source Monitor|Im Quellmonitor öffnen|Abrir en el monitor de origen|Ouvrir dans le moniteur source", "Insert at the playhead|An der Abspielposition einfügen|Insertar en el cabezal|Insérer à la tête de lecture",
        "Overwrite at the playhead|An der Abspielposition überschreiben|Sobrescribir en el cabezal|Remplacer à la tête de lecture",
        "Add to the timeline (first free place)|Zur Zeitleiste hinzufügen (erste freie Stelle)|Añadir a la línea de tiempo (primer hueco libre)|Ajouter au montage (premier emplacement libre)",
        "Clear source In and Out|Quell-In und -Out löschen|Borrar entrada y salida del origen|Effacer l'entrée et la sortie source", "Relink…|Neu verknüpfen…|Volver a vincular…|Relier…",
        "Replace Footage…|Material ersetzen…|Reemplazar metraje…|Remplacer le métrage…", "Help: Keyboard Shortcuts|Hilfe: Tastenkombinationen|Ayuda: métodos abreviados de teclado|Aide : raccourcis clavier",
        "About CutPro|Über CutPro|Acerca de CutPro|À propos de CutPro",
        // panels (headings and fixed labels)
        "PROJECT|PROJEKT|PROYECTO|PROJET", "TITLES|TITEL|TÍTULOS|TITRES", "EFFECTS|EFFEKTE|EFECTOS|EFFETS", "AUDIO TRACK MIXER|AUDIOSPURMISCHER|MEZCLADOR DE PISTAS DE AUDIO|MIXEUR DE PISTES AUDIO",
        "LUMETRI SCOPES|LUMETRI-SCOPES|ÁMBITOS LUMETRI|MONITEURS LUMETRI", "CAPTIONS|UNTERTITEL|SUBTÍTULOS|SOUS-TITRES", "SETTINGS|EINSTELLUNGEN|CONFIGURACIÓN|PARAMÈTRES",
        "EFFECT CONTROLS|EFFEKTEINSTELLUNGEN|CONTROLES DE EFECTOS|OPTIONS D'EFFET", "KEYFRAMES|KEYFRAMES|FOTOGRAMAS CLAVE|IMAGES CLÉS", "TIMELINE|ZEITLEISTE|LÍNEA DE TIEMPO|MONTAGE",
        "HISTORY|PROTOKOLL|HISTORIAL|HISTORIQUE", "SOURCE|QUELLE|ORIGEN|SOURCE", "Import Media|Medien importieren|Importar medios|Importer des médias", "Snap|Ausrichten|Ajustar|Magnétisme",
        "Select|Auswählen|Seleccionar|Sélection", "Razor|Rasierklinge|Cuchilla|Rasoir", "Title|Titel|Título|Titre", "Subtitle|Unterüberschrift|Subtítulo|Sous-titre",
        "Lower third|Bauchbinde|Rótulo inferior|Bandeau-titre", "Caption|Untertitel|Leyenda|Légende",
        "No media yet. Drop video, audio or images here, or:|Noch keine Medien. Video, Audio oder Bilder hier ablegen, oder:|Aún no hay medios. Suelta aquí vídeo, audio o imágenes, o:|Aucun média. Déposez ici des vidéos, du son ou des images, ou :",
        "No selection|Keine Auswahl|Sin selección|Aucune sélection", "Keyboard shortcuts|Tastenkombinationen|Métodos abreviados de teclado|Raccourcis clavier",
        "Title theme|Titeldesign|Tema de títulos|Thème des titres", "Format|Format|Formato|Format", "Export|Exportieren|Exportar|Exporter", "Objects|Objekte|Objetos|Objets",
        "Select object|Objekt auswählen|Seleccionar objeto|Sélectionner un objet",
        "Tip: Ctrl+Shift+P opens the command palette.|Tipp: Strg+Umschalt+P öffnet die Befehlspalette.|Consejo: Ctrl+Mayús+P abre la paleta de comandos.|Astuce : Ctrl+Maj+P ouvre la palette de commandes.",
        "The look of the editor. Also: View menu, status bar, or Ctrl+Shift+P → Color Theme.|Das Aussehen des Editors. Auch: Menü Ansicht, Statusleiste oder Strg+Umschalt+P → Farbdesign.|El aspecto del editor. También: menú Ver, barra de estado o Ctrl+Mayús+P → Tema de color.|L'apparence de l'éditeur. Aussi : menu Affichage, barre d'état ou Ctrl+Maj+P → Thème de couleur.",
        "Theme animations — wind, creatures, glints, sparkles|Design-Animationen — Wind, Tiere, Glanzlichter, Funkeln|Animaciones del tema — viento, criaturas, destellos, chispas|Animations du thème — vent, créatures, reflets, étincelles",
        "Styles — click to apply to the selected titles (or add a new title)|Stile — klicken, um sie auf die ausgewählten Titel anzuwenden (oder einen neuen Titel hinzuzufügen)|Estilos — haz clic para aplicarlos a los títulos seleccionados (o añadir un título nuevo)|Styles — cliquez pour les appliquer aux titres sélectionnés (ou ajouter un titre)",
        "Auto reframe|Automatisch neu framen|Reencuadre automático|Recadrage automatique", "Fill (crop)|Füllen (beschneiden)|Rellenar (recortar)|Remplir (rogner)",
        "Fit (bars)|Einpassen (Balken)|Ajustar (barras)|Ajuster (bandes)", "Fit + blur|Einpassen + Unschärfe|Ajustar + desenfoque|Ajuster + flou", "Fit|Einpassen|Ajustar|Ajuster",
        "Full|Voll|Completa|Pleine", "Graph|Diagramm|Gráfico|Graphique", "Dope sheet|Dope-Sheet|Hoja de animación|Feuille d'exposition", "Linear|Linear|Lineal|Linéaire",
        "Hold|Halten|Mantener|Maintien", "Ease In|Ease-In|Entrada suave|Lissage entrant", "Ease Out|Ease-Out|Salida suave|Lissage sortant", "Easy Ease|Easy Ease|Suavizado|Lissage de vitesse",
        // settings
        "Language|Sprache|Idioma|Langue", "Interface size|Oberflächengröße|Tamaño de la interfaz|Taille de l'interface",
        "Timeline scrolling while playing|Zeitleiste beim Abspielen scrollen|Desplazamiento de la línea de tiempo al reproducir|Défilement du montage pendant la lecture",
        "Page|Seite|Página|Page", "Smooth|Fließend|Suave|Fluide", "None|Keine|Ninguno|Aucun", "All|Alle|Todos|Tout",
        "Reset all to Premiere defaults|Alle auf Premiere-Standards zurücksetzen|Restablecer todo a los valores de Premiere|Tout réinitialiser aux valeurs de Premiere",
        // dialogs
        "OK|OK|Aceptar|OK", "Cancel|Abbrechen|Cancelar|Annuler", "Close|Schließen|Cerrar|Fermer", "Add|Hinzufügen|Añadir|Ajouter", "Replace|Ersetzen|Reemplazar|Remplacer",
        "Remove all|Alle entfernen|Quitar todos|Tout supprimer", "Reset|Zurücksetzen|Restablecer|Réinitialiser", "Remove|Entfernen|Quitar|Supprimer", "Later|Später|Más tarde|Plus tard",
        "Rename|Umbenennen|Cambiar nombre|Renommer", "Keyboard shortcut|Tastenkombination|Método abreviado de teclado|Raccourci clavier",
        "Paste Attributes|Attribute einfügen|Pegar atributos|Coller les attributs", "Remove Attributes|Attribute entfernen|Eliminar atributos|Supprimer les attributs",
        "Collect Files|Dateien sammeln|Recopilar archivos|Rassembler les fichiers", "Add tracks|Spuren hinzufügen|Añadir pistas|Ajouter des pistes", "Video tracks|Videospuren|Pistas de vídeo|Pistes vidéo",
        "Audio tracks|Audiospuren|Pistas de audio|Pistes audio", "Reset shortcuts|Tastenkombinationen zurücksetzen|Restablecer métodos abreviados|Réinitialiser les raccourcis",
        "Replace footage|Material ersetzen|Reemplazar metraje|Remplacer le métrage", "Files collected|Dateien gesammelt|Archivos recopilados|Fichiers rassemblés",
        "Motion (scale, position, rotation, anchor)|Bewegung (Skalierung, Position, Drehung, Ankerpunkt)|Movimiento (escala, posición, rotación, punto de anclaje)|Trajectoire (échelle, position, rotation, point d'ancrage)",
        "Opacity and blend mode|Deckkraft und Mischmodus|Opacidad y modo de fusión|Opacité et mode de fusion", "Speed / reverse|Geschwindigkeit / rückwärts|Velocidad / invertir|Vitesse / inverser",
        "Volume|Lautstärke|Volumen|Volume", "Lumetri Color|Lumetri-Farbe|Color de Lumetri|Couleur Lumetri",
        "Stylize (drawn look, sharpen, glow, grain, VHS, glitch)|Stilisieren (gezeichneter Look, Schärfen, Leuchten, Körnung, VHS, Glitch)|Estilizar (aspecto dibujado, enfocar, resplandor, grano, VHS, glitch)|Styliser (aspect dessiné, netteté, lueur, grain, VHS, glitch)",
        "VHS|VHS|VHS|VHS", "Glitch|Glitch|Glitch|Glitch",
        "SDR (Rec.709)|SDR (Rec.709)|SDR (Rec.709)|SDR (Rec.709)", "HLG HDR|HLG-HDR|HDR HLG|HDR HLG", "HDR10 (PQ)|HDR10 (PQ)|HDR10 (PQ)|HDR10 (PQ)",
        "Crop and flip|Zuschneiden und spiegeln|Recortar y voltear|Recadrer et retourner", "Ultra key|Ultra-Key|Clave ultra|Incrustation ultra", "Corner pin|Eckpunkte|Anclaje de esquinas|Coins",
        "Lens distortion|Objektivverzerrung|Distorsión de lente|Distorsion de l'objectif", "Drop shadow|Schlagschatten|Sombra paralela|Ombre portée", "Gaussian blur|Gaußscher Weichzeichner|Desenfoque gaussiano|Flou gaussien",
        "Stabilize|Stabilisieren|Estabilizar|Stabiliser", "Audio effects|Audioeffekte|Efectos de audio|Effets audio",
        // history steps
        "New project|Neues Projekt|Proyecto nuevo|Nouveau projet", "Open|Öffnen|Abrir|Ouvrir", "Add Clip|Clip hinzufügen|Añadir clip|Ajouter un plan", "Add Title|Titel hinzufügen|Añadir título|Ajouter un titre",
        "Add Object Mask|Objektmaske hinzufügen|Añadir máscara de objeto|Ajouter un masque d'objet", "Object Mask|Objektmaske|Máscara de objeto|Masque d'objet",
        "Remove Object Mask|Objektmaske entfernen|Quitar máscara de objeto|Supprimer le masque d'objet", "Move Marker|Marke verschieben|Mover marcador|Déplacer le marqueur",
        "Edit Marker|Marke bearbeiten|Editar marcador|Modifier le marqueur", "Edit Clip Marker|Clipmarke bearbeiten|Editar marcador de clip|Modifier le marqueur de plan",
        "Delete Clip Marker|Clipmarke löschen|Eliminar marcador de clip|Supprimer le marqueur de plan", "Clear Markers|Marken löschen|Borrar marcadores|Effacer les marqueurs",
        "Clear Clip Markers|Clipmarken löschen|Borrar marcadores de clip|Effacer les marqueurs de plan", "Change Property|Eigenschaft ändern|Cambiar propiedad|Modifier la propriété",
        "Move|Verschieben|Mover|Déplacer", "Trim|Trimmen|Recortar|Raccourcir", "Move / Trim|Verschieben / Trimmen|Mover / recortar|Déplacer / raccourcir",
        "Add Transition|Übergang hinzufügen|Añadir transición|Ajouter une transition", "Transition Duration|Übergangsdauer|Duración de la transición|Durée de la transition",
        "Insert / Overwrite|Einfügen / Überschreiben|Insertar / sobrescribir|Insérer / remplacer", "Remove Range|Bereich entfernen|Quitar intervalo|Supprimer la plage",
        "Ripple Trim|Trimmen und verschieben|Recorte con rizo|Raccord", "Speed/Duration|Geschwindigkeit/Dauer|Velocidad/duración|Vitesse/durée", "Freeze Frame|Standbild|Congelar fotograma|Arrêt sur image",
        "Nest|Verschachteln|Anidar|Imbriquer", "Add Track|Spur hinzufügen|Añadir pista|Ajouter une piste", "Add Tracks|Spuren hinzufügen|Añadir pistas|Ajouter des pistes",
        "Delete Tracks|Spuren löschen|Eliminar pistas|Supprimer des pistes", "Rename Track|Spur umbenennen|Cambiar nombre de pista|Renommer la piste",
        "Apply Transitions|Übergänge anwenden|Aplicar transiciones|Appliquer des transitions", "Move Keyframe|Keyframe verschieben|Mover fotograma clave|Déplacer l'image clé",
        "Volume Band|Lautstärkelinie|Banda de volumen|Ligne de volume", "Lift / Extract|Herausnehmen / Extrahieren|Levantar / extraer|Lever / extraire", "Layout Preset|Layout-Vorgabe|Diseño predefinido|Disposition prédéfinie",
        "Sequence Settings|Sequenzeinstellungen|Ajustes de secuencia|Paramètres de séquence", "Apply Title Theme|Titeldesign anwenden|Aplicar tema de títulos|Appliquer le thème des titres",
        "New Sequence|Neue Sequenz|Nueva secuencia|Nouvelle séquence", "Delete Sequence|Sequenz löschen|Eliminar secuencia|Supprimer la séquence", "Rename Sequence|Sequenz umbenennen|Cambiar nombre de secuencia|Renommer la séquence",
        "Keyframes|Keyframes|Fotogramas clave|Images clés", "Composite|Komposition|Composición|Composition", "Audio Effects|Audioeffekte|Efectos de audio|Effets audio", "Corner Pin|Eckpunkte|Anclaje de esquinas|Coins",
        "Intro & Hook|Intro & Hook|Intro y gancho|Intro et accroche", "Speed|Geschwindigkeit|Velocidad|Vitesse", "Lens Distortion|Objektivverzerrung|Distorsión de lente|Distorsion de l'objectif",
        "Add Clip Marker|Clipmarke hinzufügen|Añadir marcador de clip|Ajouter un marqueur de plan", "Delete Marker|Marke löschen|Eliminar marcador|Supprimer le marqueur",
        "steps|Schritte|pasos|étapes", "undone|rückgängig|deshechos|annulées",
        // command palette / shortcut groups
        "Clip|Clip|Clip|Plan", "Playback|Wiedergabe|Reproducción|Lecture", "Tools|Werkzeuge|Herramientas|Outils", "Object|Objekt|Objeto|Objet", "Audio|Audio|Audio|Audio",
        "Preferences|Einstellungen|Preferencias|Préférences", "Title Theme|Titeldesign|Tema de títulos|Thème des titres",
        "Play / Pause|Wiedergabe / Pause|Reproducir / pausa|Lecture / pause", "Stop|Stopp|Detener|Arrêt", "Play|Abspielen|Reproducir|Lecture", "Back One Second|Eine Sekunde zurück|Retroceder un segundo|Reculer d'une seconde",
        "Previous Frame|Vorheriger Frame|Fotograma anterior|Image précédente", "Next Frame|Nächster Frame|Fotograma siguiente|Image suivante", "Back 1 Second|1 Sekunde zurück|Retroceder 1 segundo|Reculer d'1 seconde",
        "Forward 1 Second|1 Sekunde vor|Avanzar 1 segundo|Avancer d'1 seconde", "Go to Previous Edit|Zum vorherigen Schnitt|Ir a la edición anterior|Aller au montage précédent",
        "Go to Next Edit|Zum nächsten Schnitt|Ir a la edición siguiente|Aller au montage suivant", "Go to Start|Zum Anfang|Ir al inicio|Aller au début", "Go to End|Zum Ende|Ir al final|Aller à la fin",
        "Selection Tool|Auswahlwerkzeug|Herramienta de selección|Outil Sélection", "Razor Tool|Rasierklinge|Herramienta Cuchilla|Outil Rasoir", "Toggle Snapping|Ausrichten ein/aus|Activar/desactivar ajuste|Activer/désactiver le magnétisme",
        "Insert at the Playhead|An der Abspielposition einfügen|Insertar en el cabezal|Insérer à la tête de lecture", "Overwrite at the Playhead|An der Abspielposition überschreiben|Sobrescribir en el cabezal|Remplacer à la tête de lecture",
        "Import Media…|Medien importieren…|Importar medios…|Importer des médias…",
        // high-effort features: Clip menu, bins, transcript, presets, auto-save
        "Auto-Save Versions…|Automatisch gespeicherte Versionen…|Versiones autoguardadas…|Versions enregistrées automatiquement…", "New Bin|Neue Ablage|Nueva bandeja|Nouveau chutier",
        "Record Voice-over…|Sprachaufnahme…|Grabar locución…|Enregistrer une voix off…", "Export EDL…|EDL exportieren…|Exportar EDL…|Exporter l'EDL…",
        "Export OpenTimelineIO…|OpenTimelineIO exportieren…|Exportar OpenTimelineIO…|Exporter en OpenTimelineIO…", "Transcript|Transkript|Transcripción|Transcription",
        "TRANSCRIPT|TRANSKRIPT|TRANSCRIPCIÓN|TRANSCRIPTION", "Transcribe|Transkribieren|Transcribir|Transcrire", "Select fillers|Füllwörter auswählen|Seleccionar muletillas|Sélectionner les hésitations",
        "Shorten pauses…|Pausen kürzen…|Acortar pausas…|Raccourcir les pauses…", "Search|Suchen|Buscar|Rechercher",
        "Replace with Clip from Project|Durch Clip aus dem Projekt ersetzen|Reemplazar con clip del proyecto|Remplacer par un plan du projet",
        "Fit to Fill (source into In–Out)|Anpassen und füllen (Quelle in In–Out)|Ajustar para rellenar (origen en entrada–salida)|Adapter au remplissage (source dans entrée–sortie)",
        "Scene Edit Detection…|Szenenschnitterkennung…|Detección de edición de escenas…|Détection des changements de scène…", "Synchronize (by sound)|Synchronisieren (nach Ton)|Sincronizar (por sonido)|Synchroniser (par le son)",
        "Detect Beats (markers)…|Beats erkennen (Marken)…|Detectar ritmos (marcadores)…|Détecter les temps (marqueurs)…", "Cut to the Beat|Im Takt schneiden|Cortar al ritmo|Couper sur le temps",
        "Auto Ducking…|Automatisches Ducking…|Atenuación automática…|Atténuation automatique…", "Auto Colour|Automatische Farbe|Color automático|Couleur automatique",
        "Colour Match…|Farbabgleich…|Igualar color…|Correspondance des couleurs…", "Remove Background|Hintergrund entfernen|Quitar el fondo|Supprimer l'arrière-plan",
        "Auto Reframe with Push-in…|Automatisch reframen mit Zoom…|Reencuadre automático con acercamiento…|Recadrage automatique avec zoom…", "Save Preset…|Vorgabe speichern…|Guardar ajuste preestablecido…|Enregistrer le paramètre prédéfini…",
        "Apply Preset|Vorgabe anwenden|Aplicar ajuste preestablecido|Appliquer le paramètre prédéfini", "Presets|Vorgaben|Ajustes preestablecidos|Paramètres prédéfinis",
        "Move to Bin|In Ablage verschieben|Mover a bandeja|Déplacer vers le chutier", "Label|Beschriftung|Etiqueta|Étiquette", "Rating|Bewertung|Valoración|Note", "Keywords…|Schlüsselwörter…|Palabras clave…|Mots-clés…",
        "No Label|Keine Beschriftung|Sin etiqueta|Aucune étiquette", "No Rating|Keine Bewertung|Sin valoración|Aucune note", "Open Bin|Ablage öffnen|Abrir bandeja|Ouvrir le chutier",
        "Rename Bin…|Ablage umbenennen…|Cambiar nombre de bandeja…|Renommer le chutier…", "New Bin Inside|Neue Ablage darin|Nueva bandeja dentro|Nouveau chutier à l'intérieur",
        "Delete Bin (keeps its media)|Ablage löschen (Medien bleiben)|Eliminar bandeja (conserva sus medios)|Supprimer le chutier (garde ses médias)", "Up One Bin|Eine Ablage höher|Subir una bandeja|Remonter d'un chutier",
        "Transcribe (text-based editing)|Transkribieren (textbasierte Bearbeitung)|Transcribir (edición basada en texto)|Transcrire (montage par le texte)",
        "Replace Selected Clip with This|Ausgewählten Clip hierdurch ersetzen|Reemplazar el clip seleccionado con este|Remplacer le plan sélectionné par celui-ci",
        "Video|Video|Vídeo|Vidéo", "Images|Bilder|Imágenes|Images", "Sequences|Sequenzen|Secuencias|Séquences", "Labelled|Beschriftet|Etiquetados|Étiquetés", "Rated|Bewertet|Valorados|Notés",
        "Offline|Offline|Sin conexión|Hors ligne", "Not used|Nicht verwendet|Sin usar|Non utilisés", "Type|Typ|Tipo|Type", "Date added|Hinzugefügt am|Fecha de adición|Date d'ajout",
        "Scene Edit Detection|Szenenschnitterkennung|Detección de edición de escenas|Détection des changements de scène", "Synchronize|Synchronisieren|Sincronizar|Synchroniser",
        "Beat Markers|Beat-Marken|Marcadores de ritmo|Marqueurs de temps", "Delete Words|Wörter löschen|Eliminar palabras|Supprimer des mots", "Delete Word|Wort löschen|Eliminar palabra|Supprimer le mot",
        "Shorten Pauses|Pausen kürzen|Acortar pausas|Raccourcir les pauses", "Overwrite|Überschreiben|Sobrescribir|Remplacer", "Insert|Einfügen|Insertar|Insérer",
        "Replace Edit|Ersetzen|Edición de reemplazo|Montage de remplacement", "Fit to Fill|Anpassen und füllen|Ajustar para rellenar|Adapter au remplissage",
        "Auto Ducking|Automatisches Ducking|Atenuación automática|Atténuation automatique", "Colour Match|Farbabgleich|Igualar color|Correspondance des couleurs",
        "Unsaved changes|Nicht gespeicherte Änderungen|Cambios sin guardar|Modifications non enregistrées", "Save|Speichern|Guardar|Enregistrer", "Don't Save|Nicht speichern|No guardar|Ne pas enregistrer",
        "Later|Später|Más tarde|Plus tard", "Recover|Wiederherstellen|Recuperar|Récupérer", "Discard|Verwerfen|Descartar|Abandonner", "Recover project|Projekt wiederherstellen|Recuperar proyecto|Récupérer le projet",
        "Open Version|Version öffnen|Abrir versión|Ouvrir la version", "Show Transcript|Transkript anzeigen|Mostrar transcripción|Afficher la transcription",
        // extra-high items: tools, generators, queue, import, colour, distort, text animation, sound, proxies
        "Ripple Edit Tool|Löschen-und-Lücke-schließen-Werkzeug|Herramienta Edición de rizo|Outil Modification par propagation",
        "Rolling Edit Tool|Rollen-Werkzeug|Herramienta Edición de desplazamiento|Outil Modification par déplacement",
        "Rate Stretch Tool|Rate-Stretch-Werkzeug|Herramienta Ajuste de velocidad|Outil Variation de vitesse",
        "Slip Tool|Unterschieben-Werkzeug|Herramienta Desplazar|Outil Déplacement de montage", "Slide Tool|Verschieben-Werkzeug|Herramienta Deslizar|Outil Glissement",
        "Rolling|Rollen|Desplazamiento|Déplacement", "Rate Stretch|Rate-Stretch|Ajuste de velocidad|Variation de vitesse", "Rolling Edit|Rollen-Bearbeitung|Edición de desplazamiento|Modification par déplacement",
        "Shuttle Forward|Shuttle vorwärts|Avance rápido|Navette avant", "Shuttle Reverse|Shuttle rückwärts|Retroceso rápido|Navette arrière",
        "Audio Scrubbing|Audio-Scrubbing|Arrastre de audio|Défilement audio", "Audio Time Units|Audio-Zeiteinheiten|Unidades de tiempo de audio|Unités de temps audio",
        "Toggle Proxies|Proxys ein/aus|Activar/desactivar proxies|Activer/désactiver les proxys", "Proxies|Proxys|Proxies|Proxys",
        "New Item|Neues Element|Nuevo elemento|Nouvel élément", "Colour Matte…|Farbfläche…|Mate de color…|Cache couleur…", "Gradient…|Verlauf…|Degradado…|Dégradé…",
        "Bars and Tone…|Farbbalken und Ton…|Barras y tono…|Barres et ton…", "Counting Leader…|Countdown-Vorspann…|Contador universal…|Amorce de décompte…",
        "Import Sequence (XML, OTIO, EDL)…|Sequenz importieren (XML, OTIO, EDL)…|Importar secuencia (XML, OTIO, EDL)…|Importer une séquence (XML, OTIO, EDL)…",
        "Render Queue|Renderwarteschlange|Cola de procesamiento|File d'attente de rendu", "RENDER QUEUE|RENDERWARTESCHLANGE|COLA DE PROCESAMIENTO|FILE D'ATTENTE DE RENDU",
        "Add to Queue…|Zur Warteschlange hinzufügen…|Añadir a la cola…|Ajouter à la file…", "Clear Finished|Fertige entfernen|Borrar terminados|Effacer les terminés",
        "Pause|Pause|Pausa|Pause", "Resume|Fortsetzen|Reanudar|Reprendre", "Waiting|Wartet|En espera|En attente",
        "5.1 Output|5.1-Ausgabe|Salida 5.1|Sortie 5.1", "Front left / right|Vorne links / rechts|Frontal izquierdo / derecho|Avant gauche / droite",
        "Centre (dialogue)|Mitte (Dialog)|Centro (diálogo)|Centre (dialogue)", "Surround left / right|Surround links / rechts|Envolvente izquierdo / derecho|Surround gauche / droite",
        "LFE (low frequencies)|LFE (Tiefen)|LFE (graves)|LFE (basses fréquences)",
        "Create Proxies (540p)|Proxys erstellen (540p)|Crear proxies (540p)|Créer des proxys (540p)", "Create Proxies (720p)|Proxys erstellen (720p)|Crear proxies (720p)|Créer des proxys (720p)",
        "Attach Proxy…|Proxy anhängen…|Adjuntar proxy…|Associer un proxy…", "Detach Proxies|Proxys trennen|Separar proxies|Dissocier les proxys",
        "Use Proxies in the Preview|Proxys in der Vorschau verwenden|Usar proxies en la vista previa|Utiliser les proxys dans l'aperçu",
        "Create Upscaled Copy|Hochskalierte Kopie erstellen|Crear copia ampliada|Créer une copie agrandie",
        "Tone-map to SDR|In SDR umwandeln (Tone-Mapping)|Mapear tonos a SDR|Convertir en SDR (mappage des tons)",
        "Show as is|Unverändert anzeigen|Mostrar tal cual|Afficher tel quel",
        "10-bit|10 Bit|10 bits|10 bits",
        "Avid DNxHR HQ|Avid DNxHR HQ|Avid DNxHR HQ|Avid DNxHR HQ", "Avid DNxHR HQX (10-bit)|Avid DNxHR HQX (10 Bit)|Avid DNxHR HQX (10 bits)|Avid DNxHR HQX (10 bits)",
        "Avid DNxHR 444|Avid DNxHR 444|Avid DNxHR 444|Avid DNxHR 444",
        "Text animation|Textanimation|Animación de texto|Animation du texte", "Roll / crawl|Rollen / Kriechen|Desplazamiento vertical / horizontal|Défilement / bandeau",
        "HSL Secondary|HSL-Sekundär|HSL secundaria|TSL secondaire", "Show mask|Maske anzeigen|Mostrar máscara|Afficher le masque", "Hue range|Farbtonbereich|Rango de tono|Plage de teinte",
        "Hue shift|Farbtonverschiebung|Cambio de tono|Décalage de teinte", "Softness|Weichheit|Suavidad|Douceur",
        "Distort|Verzerren|Distorsionar|Déformation", "Mesh Warp|Gitterverzerrung|Deformación de malla|Déformation par maillage", "Reset the mesh|Gitter zurücksetzen|Restablecer la malla|Réinitialiser le maillage",
        "Noise reduction|Rauschunterdrückung|Reducción de ruido|Réduction du bruit", "Interpolation|Interpolation|Interpolación|Interpolation",
        "Clean up|Bereinigen|Limpiar|Nettoyer", "Attach to object…|An Objekt anheften…|Adjuntar al objeto…|Attacher à l'objet…",
        "Attach to Object|An Objekt anheften|Adjuntar al objeto|Attacher à l'objet", "Detach from Object|Vom Objekt lösen|Separar del objeto|Détacher de l'objet",
        "Time Interpolation|Zeitinterpolation|Interpolación de tiempo|Interpolation temporelle", "Audio Channels|Audiokanäle|Canales de audio|Canaux audio",
        "Clean Up Sound|Ton bereinigen|Limpiar sonido|Nettoyer le son",
        "Blur Faces…|Gesichter weichzeichnen…|Desenfocar caras…|Flouter les visages…", "Blur Faces|Gesichter weichzeichnen|Desenfocar caras|Flouter les visages",
        "Mask size (%)|Maskengröße (%)|Tamaño de máscara (%)|Taille du masque (%)", "Find small faces too (slower)|Auch kleine Gesichter finden (langsamer)|Buscar también caras pequeñas (más lento)|Trouver aussi les petits visages (plus lent)",
        "Black out|Schwärzen|Tapar en negro|Masquer en noir",
        "Multi-Camera|Mehrkamera|Multicámara|Multicaméra", "Multi-Camera View|Mehrkamera-Ansicht|Vista multicámara|Vue multicaméra",
        "Create Multi-Camera Source Sequence…|Mehrkamera-Quellsequenz erstellen…|Crear secuencia de origen multicámara…|Créer une séquence source multicaméra…",
        "Multi-Camera Source Sequence|Mehrkamera-Quellsequenz|Secuencia de origen multicámara|Séquence source multicaméra",
        "Flatten|Reduzieren|Acoplar|Aplatir", "Synchronize by|Synchronisieren nach|Sincronizar por|Synchroniser par", "Sound|Ton|Sonido|Son",
        "Put it on the timeline at the playhead|An der Abspielposition in die Zeitleiste legen|Ponerla en la línea de tiempo en el cabezal|La placer sur la timeline à la tête de lecture",
        "Multi-Cam|Mehrkamera-Modus|Multicám|Multicam", "Clip starts|Clipanfänge|Inicios de los clips|Débuts des clips", "In points|In-Punkte|Puntos de entrada|Points d'entrée",
        "Timecode|Timecode|Código de tiempo|Timecode",
        "Create Source Sequence…|Quellsequenz erstellen…|Crear secuencia de origen…|Créer la séquence source…",
        "Cut to Camera 1|Schnitt zu Kamera 1|Cortar a cámara 1|Couper vers la caméra 1", "Cut to Camera 2|Schnitt zu Kamera 2|Cortar a cámara 2|Couper vers la caméra 2",
        "Cut to Camera 3|Schnitt zu Kamera 3|Cortar a cámara 3|Couper vers la caméra 3", "Cut to Camera 4|Schnitt zu Kamera 4|Cortar a cámara 4|Couper vers la caméra 4",
        "Cut to Camera 5|Schnitt zu Kamera 5|Cortar a cámara 5|Couper vers la caméra 5", "Cut to Camera 6|Schnitt zu Kamera 6|Cortar a cámara 6|Couper vers la caméra 6",
        "Cut to Camera 7|Schnitt zu Kamera 7|Cortar a cámara 7|Couper vers la caméra 7", "Cut to Camera 8|Schnitt zu Kamera 8|Cortar a cámara 8|Couper vers la caméra 8",
        "Cut to Camera 9|Schnitt zu Kamera 9|Cortar a cámara 9|Couper vers la caméra 9",
        "Shapes and stickers|Formen und Sticker|Formas y stickers|Formes et autocollants", "Save as Graphic Template…|Als Grafikvorlage speichern…|Guardar como plantilla gráfica…|Enregistrer comme modèle graphique…",
        "Graphics — click to add one at the playhead|Grafiken – klicken, um eine an der Abspielposition einzufügen|Gráficos: haz clic para añadir uno en el cabezal|Graphiques — cliquez pour en ajouter un à la tête de lecture",
        "Styles — click to apply to the selected titles (or add a new title)|Stile – klicken, um sie auf die ausgewählten Titel anzuwenden (oder einen neuen Titel hinzuzufügen)|Estilos: haz clic para aplicarlos a los títulos seleccionados (o añadir un título)|Styles — cliquez pour les appliquer aux titres sélectionnés (ou ajouter un titre)",
        "Over the text|Über dem Text|Sobre el texto|Au-dessus du texte", "Both ends|Beide Enden|Ambos extremos|Aux deux bouts", "Delete Template|Vorlage löschen|Eliminar plantilla|Supprimer le modèle",
        "Details|Details|Detalles|Détails", "Corners|Ecken|Esquinas|Coins", "Points|Zacken|Puntas|Branches", "Inner %|Innen %|Interior %|Intérieur %", "Thickness|Stärke|Grosor|Épaisseur",
        "Head|Spitze|Punta|Pointe", "Filled|Gefüllt|Relleno|Rempli", "Outline width|Konturbreite|Ancho del contorno|Largeur du contour", "Shadow dist.|Schattenabst.|Dist. sombra|Dist. ombre",
        "Delay (s)|Verzögerung (s)|Retraso (s)|Délai (s)", "Loop|Schleife|Bucle|Boucle", "Duplicate|Duplizieren|Duplicar|Dupliquer",
        "Smart render|Smart Rendering|Render inteligente|Rendu intelligent",
        "Copy unchanged stretches (smart render)|Unveränderte Abschnitte kopieren (Smart Rendering)|Copiar los tramos sin cambios (render inteligente)|Copier les passages inchangés (rendu intelligent)",
    };
    static readonly Dictionary<string, string[]> table = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> back = new(StringComparer.Ordinal);
    static Loc()
    {
        foreach (var r in rows)
        {
            var p = r.Split('|');
            if (p.Length != 4) continue;
            table[p[0]] = p;
            for (int i = 1; i < 4; i++) back.TryAdd(p[i], p[0]);
        }
    }

    /// <summary>The current language's text for an English string (itself when it isn't in the table).</summary>
    public static string T(string en)
    {
        if (Col == 0 || en.Length == 0) return en;
        if (table.TryGetValue(en, out var p)) return p[Col];
        // "Undo Razor", "Group: Name": translate the known parts
        int c = en.IndexOf(": ", StringComparison.Ordinal);
        if (c > 0 && table.ContainsKey(en[..c])) return T(en[..c]) + ": " + T(en[(c + 2)..]);
        return en;
    }
    /// <summary>The English key of a (possibly translated) string.</summary>
    public static string English(string s)
    {
        if (back.TryGetValue(s, out var e)) return e;
        int c = s.IndexOf(": ", StringComparison.Ordinal);                // ("Ausrichten: On" → "Snap: On")
        return c > 0 && back.TryGetValue(s[..c], out var g) ? g + ": " + English(s[(c + 2)..]) : s;
    }
    static bool walked;

    public static void Use(string code)
    {
        Current = Languages.Any(l => l.code == code) ? code : "en";
        EnsureHooks();
    }
    static bool hooked;
    static void EnsureHooks()
    {
        if (hooked) return;
        hooked = true;
        // every context menu (title-bar menus, timeline, panels) is translated as it opens
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Apply((DependencyObject)s)));
    }

    /// <summary>Translates the fixed strings in a tree (window, menu, dialog content) to the current language — or back to English — and names icon-only
    /// buttons for screen readers.</summary>
    public static void Apply(DependencyObject root)
    {
        if (Col == 0 && !walked) { Name(root); return; }                    // (English and never translated: only the accessibility names)
        walked = true;
        var seen = new HashSet<DependencyObject>();
        Walk(root, seen);
    }
    static string Swap(string s)
    {
        var en = English(s);
        var t = T(en);
        return t;
    }
    static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(d)) return;
        switch (d)
        {
            case TextBox or PasswordBox: return;                            // (user text)
            case TextBlock tb when BindingOperations.GetBindingExpression(tb, TextBlock.TextProperty) == null && tb.Text is { Length: > 0 } s:
                var ns = Swap(s); if (ns != s) tb.Text = ns; break;
            case HeaderedItemsControl hi when hi.Header is string h && BindingOperations.GetBindingExpression(hi, HeaderedItemsControl.HeaderProperty) == null:
                var nh = Swap(h); if (nh != h) hi.Header = nh; break;
            case HeaderedContentControl hc when hc.Header is string h2:
                var nh2 = Swap(h2); if (nh2 != h2) hc.Header = nh2; break;
            case ContentControl cc when cc.Content is string c && BindingOperations.GetBindingExpression(cc, ContentControl.ContentProperty) == null && !IsGlyph(c):
                var nc = Swap(c); if (nc != c) cc.Content = nc; break;
        }
        if (d is FrameworkElement fe) NameOne(fe);
        foreach (var ch in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList()) Walk(ch, seen);
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), seen);
        if (d is ItemsControl ic) foreach (var it in ic.Items.OfType<DependencyObject>().ToList()) Walk(it, seen);
    }
    /// <summary>Text of only icon-font glyphs (Segoe MDL2 private use area) or symbols — an icon button.</summary>
    static bool IsGlyph(string s) => s.Length is > 0 and <= 3 && s.All(ch => ch >= '' && ch <= '' || char.IsSymbol(ch) || ch is '◀' or '▶' or '●' or '◯' or '▭' or '✎' or '✦');

    /// <summary>Accessibility: buttons that show only an icon get their tooltip (or a fixed name) as their automation name.</summary>
    public static void Name(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        void W(DependencyObject d)
        {
            if (!seen.Add(d)) return;
            if (d is FrameworkElement fe) NameOne(fe);
            foreach (var ch in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList()) W(ch);
            if (d is Visual) for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) W(VisualTreeHelper.GetChild(d, i));
        }
        W(root);
    }
    static void NameOne(FrameworkElement fe)
    {
        if (fe is not (ButtonBase or Slider or ComboBox) || AutomationProperties.GetName(fe) is { Length: > 0 }) return;
        string? text = fe is ContentControl { Content: string c } && !IsGlyph(c) ? null : fe.ToolTip as string;
        if (text == null && fe is Slider or ComboBox) text = fe.ToolTip as string;
        if (text is { Length: > 0 }) AutomationProperties.SetName(fe, text.Split('\n')[0].Split(" — ")[0]);
    }
}
