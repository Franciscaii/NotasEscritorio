// Notas Escritorio: panel de notas tipo widget para Windows 11.
// - Lo que escribes se guarda solo como BORRADOR (caché) hasta que quites la nota del panel
// - El archivo .txt solo se escribe al pulsar "Guardar", y tú eliges dónde
// - Puedes abrir cualquier .txt existente
// - Notas blancas por defecto, sin bordes redondeados
// - El panel ocupa el alto libre de la pantalla (no pasa la barra de tareas),
//   pegado arriba y al borde, y queda fijo en el escritorio por debajo de las demás ventanas

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace NotasEscritorio
{
    public static class Program
    {
        public static Mutex InstanceMutex;

        [STAThread]
        public static void Main()
        {
            InstanceMutex = new Mutex(true, "NotasEscritorio_UnaSolaInstancia", out bool isNew);
            if (!isNew)
            {
                try { Native.AllowSetForegroundWindow(-1); } catch { }
                // Ya está abierta (por ejemplo, clic en el icono anclado): se le pide que se muestre
                try { using var ev = EventWaitHandle.OpenExisting(MainWindow.ShowEventName); ev.Set(); } catch { }
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { app.Resources.MergedDictionaries.Add(ModernStyles.Load()); } catch { }
            var win = new MainWindow();
            win.Show();
            app.Run();
            Environment.Exit(0); // que no quede ningún proceso abierto al cerrar
        }
    }

    // ================================================================ Markdown <-> documento con formato

    static class MarkdownIO
    {
        record Seg(string Text, bool Bold, bool Italic, bool Strike, string Link);

        public static string FromDoc(FlowDocument doc, double baseSize)
        {
            var lines = new List<string>();
            WriteBlocks(doc.Blocks, "", lines, baseSize);
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\r\n", lines);
        }

        static void WriteBlocks(BlockCollection blocks, string indent, List<string> lines, double baseSize)
        {
            foreach (var b in blocks)
            {
                if (b is Paragraph p) lines.Add(indent + ParagraphMd(p, baseSize));
                else if (b is List l)
                {
                    int i = l.StartIndex;
                    foreach (var li in l.ListItems)
                    {
                        var marker = l.MarkerStyle == TextMarkerStyle.Decimal ? (i++) + ". " : "- ";
                        int before = lines.Count;
                        WriteBlocks(li.Blocks, "", lines, baseSize);
                        for (int k = before; k < lines.Count; k++)
                            lines[k] = k == before ? indent + marker + lines[k] : indent + "  " + lines[k];
                    }
                }
                else if (b is Table t)
                {
                    bool first = true;
                    foreach (var rg in t.RowGroups)
                        foreach (var r in rg.Rows)
                        {
                            var cells = r.Cells.Select(c => string.Join(" ", c.Blocks.OfType<Paragraph>().Select(q => InlinesMd(q, false)))).ToList();
                            lines.Add(indent + "| " + string.Join(" | ", cells) + " |");
                            if (first) { lines.Add(indent + "|" + string.Concat(cells.Select(_ => " --- |"))); first = false; }
                        }
                }
                else if (b is Section sec) WriteBlocks(sec.Blocks, indent, lines, baseSize);
            }
        }

        static string ParagraphMd(Paragraph p, double baseSize)
        {
            var range = new TextRange(p.ContentStart, p.ContentEnd);
            var text = range.Text;
            string prefix = "";
            bool heading = false;
            if (text.Length > 0 && range.GetPropertyValue(TextElement.FontSizeProperty) is double size)
            {
                bool bold = range.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
                if (size >= baseSize + 8) { prefix = "# "; heading = true; }
                else if (size >= baseSize + 4) { prefix = "## "; heading = true; }
                else if (size >= baseSize + 2 && bold) { prefix = "### "; heading = true; }
            }
            var body = InlinesMd(p, heading);
            if (body.StartsWith("☐ ")) return "- [ ] " + body.Substring(2);
            if (body.StartsWith("☑ ")) return "- [x] " + body.Substring(2);
            if (body.StartsWith("☐")) return "- [ ] " + body.Substring(1);
            if (body.StartsWith("☑")) return "- [x] " + body.Substring(1);
            return prefix + body;
        }

        static string InlinesMd(Paragraph p, bool ignoreBold)
        {
            var segs = new List<Seg>();
            void Walk(InlineCollection inlines)
            {
                foreach (var inl in inlines)
                {
                    if (inl is Run r)
                    {
                        if (r.Text.Length == 0) continue;
                        bool bold = !ignoreBold && r.FontWeight.ToOpenTypeWeight() >= 600;
                        bool italic = r.FontStyle == FontStyles.Italic || r.FontStyle == FontStyles.Oblique;
                        bool strike = false; string link = null;
                        for (DependencyObject d = r; d is Inline x; d = x.Parent)
                        {
                            if (x.TextDecorations != null && x.TextDecorations.Any(t => t.Location == TextDecorationLocation.Strikethrough)) strike = true;
                            if (x is Hyperlink h && link == null) link = h.NavigateUri?.ToString();
                        }
                        segs.Add(new Seg(r.Text, bold, italic, strike, link));
                    }
                    else if (inl is Span sp) Walk(sp.Inlines);
                    else if (inl is LineBreak) segs.Add(new Seg("  \n", false, false, false, null));
                }
            }
            Walk(p.Inlines);

            // unir trozos con el mismo formato
            var merged = new List<Seg>();
            foreach (var sg in segs)
            {
                if (merged.Count > 0)
                {
                    var last = merged[^1];
                    if (last.Bold == sg.Bold && last.Italic == sg.Italic && last.Strike == sg.Strike && last.Link == sg.Link)
                    { merged[^1] = last with { Text = last.Text + sg.Text }; continue; }
                }
                merged.Add(sg);
            }

            var sb = new StringBuilder();
            foreach (var sg in merged)
            {
                var t = sg.Text;
                // los espacios de los bordes quedan fuera de las marcas
                var lead = t.Length - t.TrimStart().Length; var trail = t.Length - t.TrimEnd().Length;
                var core = t.Trim();
                if (core.Length == 0) { sb.Append(t); continue; }
                if (sg.Strike) core = "~~" + core + "~~";
                if (sg.Italic) core = "*" + core + "*";
                if (sg.Bold) core = "**" + core + "**";
                if (sg.Link != null) core = "[" + core + "](" + sg.Link + ")";
                sb.Append(t.Substring(0, lead)).Append(core).Append(t.Substring(t.Length - trail));
            }
            return sb.ToString();
        }

        // -------- lectura

        static readonly Regex InlineRx = new(@"\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\)|\*\*(?<b>.+?)\*\*|__(?<b2>.+?)__|~~(?<s>.+?)~~|\*(?<i>[^*]+?)\*|_(?<i2>[^_]+?)_");
        static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");
        static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$");
        static readonly Regex Number = new(@"^(\s*)(\d+)[.)]\s+(.*)$");
        static readonly Regex Check = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s?(.*)$");

        public static void ToDoc(FlowDocument doc, string md, double baseSize)
        {
            doc.Blocks.Clear();
            var lines = (md ?? "").Replace("\r\n", "\n").Split('\n');
            List current = null;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                Match m;
                if (line.TrimStart().StartsWith("|") && line.TrimEnd().EndsWith("|"))
                {
                    current = null;
                    var rows = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|")) rows.Add(lines[i++].Trim());
                    i--;
                    rows = rows.Where(r => !Regex.IsMatch(r, @"^\|(\s*:?-{2,}:?\s*\|)+$")).ToList();
                    if (rows.Count == 0) continue;
                    var cellsPerRow = rows.Select(r => r.Trim('|').Split('|').Select(c => c.Trim()).ToList()).ToList();
                    int cols = cellsPerRow.Max(r => r.Count);
                    var table = new Table { CellSpacing = 0, BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 4, 0, 4) };
                    for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
                    var g = new TableRowGroup();
                    for (int r = 0; r < cellsPerRow.Count; r++)
                    {
                        var row = new TableRow();
                        for (int c = 0; c < cols; c++)
                        {
                            var para = new Paragraph();
                            AddInlines(para.Inlines, c < cellsPerRow[r].Count ? cellsPerRow[r][c] : "");
                            row.Cells.Add(new TableCell(para)
                            {
                                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), BorderThickness = new Thickness(0, 0, 1, 1),
                                Padding = new Thickness(4, 2, 4, 2), FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                            });
                        }
                        g.Rows.Add(row);
                    }
                    table.RowGroups.Add(g);
                    doc.Blocks.Add(table);
                }
                else if ((m = Check.Match(line)).Success)
                {
                    current = null;
                    var para = new Paragraph();
                    para.Inlines.Add(new Run(m.Groups[2].Value.Trim().Length > 0 ? "☑ " : "☐ "));
                    AddInlines(para.Inlines, m.Groups[3].Value);
                    doc.Blocks.Add(para);
                }
                else if ((m = Heading.Match(line)).Success)
                {
                    current = null;
                    int level = m.Groups[1].Value.Length;
                    var para = new Paragraph
                    {
                        FontSize = level == 1 ? baseSize + 9 : level == 2 ? baseSize + 5 : baseSize + 2,
                        FontWeight = FontWeights.Bold,
                    };
                    AddInlines(para.Inlines, m.Groups[2].Value);
                    doc.Blocks.Add(para);
                }
                else if ((m = Bullet.Match(line)).Success || (m = Number.Match(line)).Success)
                {
                    bool numbered = m.Groups.Count == 4;
                    var marker = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                    if (current == null || current.MarkerStyle != marker)
                    {
                        current = new List { MarkerStyle = marker };
                        doc.Blocks.Add(current);
                    }
                    var para = new Paragraph();
                    AddInlines(para.Inlines, numbered ? m.Groups[3].Value : m.Groups[2].Value);
                    current.ListItems.Add(new ListItem(para));
                }
                else
                {
                    current = null;
                    var para = new Paragraph();
                    AddInlines(para.Inlines, line);
                    doc.Blocks.Add(para);
                }
            }
            if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        }

        static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                if (m.Groups["lt"].Success)
                {
                    var h = new Hyperlink();
                    try { h.NavigateUri = new Uri(m.Groups["lu"].Value, UriKind.RelativeOrAbsolute); } catch { }
                    AddInlines(h.Inlines, m.Groups["lt"].Value);
                    target.Add(h);
                }
                else
                {
                    var span = new Span();
                    string inner;
                    if (m.Groups["b"].Success || m.Groups["b2"].Success) { span.FontWeight = FontWeights.Bold; inner = m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value; }
                    else if (m.Groups["s"].Success) { span.TextDecorations = TextDecorations.Strikethrough; inner = m.Groups["s"].Value; }
                    else { span.FontStyle = FontStyles.Italic; inner = m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value; }
                    AddInlines(span.Inlines, inner);
                    target.Add(span);
                }
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }
    }

    // ================================================================ Estilo moderno de menús y avisos

    static class ModernStyles
    {
        const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <Style TargetType="ContextMenu">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="HasDropShadow" Value="False"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Foreground" Value="#1F1F1F"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Margin="6,4,14,14" Background="#FCFCFC" BorderBrush="#1A000000" BorderThickness="1" CornerRadius="10" Padding="5">
            <Border.Effect>
              <DropShadowEffect BlurRadius="18" ShadowDepth="3" Direction="270" Opacity="0.20"/>
            </Border.Effect>
            <ScrollViewer VerticalScrollBarVisibility="Auto" MaxHeight="640">
              <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="10,5,10,5" Background="#14000000"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="MenuItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="Foreground" Value="#1F1F1F"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Padding="8,7,12,7" MinWidth="190">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="26"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
              </Grid.ColumnDefinitions>
              <ContentPresenter x:Name="IconHost" ContentSource="Icon" VerticalAlignment="Center" HorizontalAlignment="Left"/>
              <TextBlock x:Name="Check" Text="&#xE73E;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="12"
                         Foreground="#0F6CBD" VerticalAlignment="Center" HorizontalAlignment="Left" Visibility="Collapsed"/>
              <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
              <TextBlock x:Name="Arrow" Grid.Column="2" Text="&#xE76C;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"
                         FontSize="10" Margin="14,0,0,0" VerticalAlignment="Center" Foreground="#6B6B6B" Visibility="Collapsed"/>
              <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="2" VerticalOffset="-10" AllowsTransparency="True"
                     Focusable="False" PopupAnimation="Fade"
                     IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}">
                <Border Margin="6,4,14,14" Background="#FCFCFC" BorderBrush="#1A000000" BorderThickness="1" CornerRadius="10" Padding="5">
                  <Border.Effect>
                    <DropShadowEffect BlurRadius="18" ShadowDepth="3" Direction="270" Opacity="0.20"/>
                  </Border.Effect>
                  <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </Border>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="#10000000"/>
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
              <Setter TargetName="Bd" Property="Background" Value="#0C0F6CBD"/>
            </Trigger>
            <Trigger Property="HasItems" Value="True">
              <Setter TargetName="Arrow" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Foreground" Value="#8A8A8A"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key="ThumbFino" TargetType="Thumb">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Thumb">
          <Border x:Name="t" Background="#38000000" CornerRadius="3" Margin="2,0,2,0"/>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="t" Property="Background" Value="#66000000"/></Trigger>
            <Trigger Property="IsDragging" Value="True"><Setter TargetName="t" Property="Background" Value="#80000000"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ScrollBar">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Width" Value="9"/>
    <Setter Property="MinWidth" Value="9"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ScrollBar">
          <Grid Background="Transparent">
            <Track x:Name="PART_Track" IsDirectionReversed="True">
              <Track.DecreaseRepeatButton>
                <RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style="{StaticResource ThumbFino}" MinHeight="28"/>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="Height" Value="9"/>
        <Setter Property="Visibility" Value="Collapsed"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType="ToolTip">
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="HasDropShadow" Value="False"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToolTip">
          <Border Margin="4,2,10,10" Background="#FCFCFC" BorderBrush="#1A000000" BorderThickness="1" CornerRadius="6" Padding="9,5,9,6">
            <Border.Effect>
              <DropShadowEffect BlurRadius="10" ShadowDepth="2" Direction="270" Opacity="0.16"/>
            </Border.Effect>
            <ContentPresenter TextBlock.Foreground="#1F1F1F"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";

        public static ResourceDictionary Load() => (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Xaml);
    }

    // ================================================================ Datos

    public class Config
    {
        public string Side { get; set; } = "derecha";
        public double Width { get; set; } = 360;
        public double PanelOpacity { get; set; } = 1.0;  // 1 = opaco (texto más nítido)
        public double NoteOpacity { get; set; } = 1.0;
        public double ExpandedWidth { get; set; } = 360; // ancho al que vuelve desde "solo barra"
        public bool FirstRunDone { get; set; }
        public bool MigratedV2 { get; set; }
        public string LastDir { get; set; }
        public string Theme { get; set; } = "claro";
        public string SaveDir { get; set; }
        public string FontName { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 13;
        public string FontColor { get; set; } = "#333333";
        public List<string> CacheOrder { get; set; } = new();

        // Solo para importar notas de la versión 1
        public Dictionary<string, string> Colors { get; set; }
        public Dictionary<string, string> Types { get; set; }
        public List<string> Order { get; set; }

        public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotasEscritorio");
        static string FilePath => Path.Combine(AppDataDir, "config.json");

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                    if (c != null) { c.CacheOrder ??= new(); return c; }
                }
            }
            catch { }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    public class TodoItem
    {
        public bool Done { get; set; }
        public string Text { get; set; } = "";
    }

    public class Note
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "nota";     // "nota" | "lista"
        public string Text { get; set; } = "";
        public List<TodoItem> Items { get; set; } = new();
        public string Color { get; set; } = "blanco";
        public string FilePath { get; set; }           // null = nunca guardada en .txt
        public string SavedContent { get; set; }       // contenido del .txt la última vez que se guardó/abrió
        public DateTime Modified { get; set; } = DateTime.Now;
        public double? CardHeight { get; set; }
        public bool IsFloating { get; set; }           // true = la nota está fuera del panel, en su propia ventana
        public double? FloatLeft { get; set; }
        public double? FloatTop { get; set; }
        public double? FloatWidth { get; set; }
        public double? FloatHeight { get; set; }
        public string RichXaml { get; set; }           // formato del texto (negrita, colores, fuentes…) guardado en el panel        // alto de la hoja elegido a mano (null = automático)

        public string Markdown { get; set; }           // el mismo texto con formato en Markdown (para guardar como .md)

        public bool IsMarkdownFile => FilePath != null && FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

        public string Serialize()
        {
            if (Kind != "lista") return IsMarkdownFile ? (Markdown ?? Text ?? "") : (Text ?? "");
            return string.Join("\r\n", Items.Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text));
        }

        public bool IsEmpty => Kind == "lista" ? Items.All(i => string.IsNullOrWhiteSpace(i.Text)) : string.IsNullOrWhiteSpace(Text);

        public bool IsDirty => FilePath == null ? !IsEmpty : Serialize() != (SavedContent ?? "");

        public static bool LooksLikeList(string content)
        {
            var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            return lines.Count > 0 && lines.All(l => IsTodoLine(l.TrimStart()));
        }

        static bool IsTodoLine(string l) => l.StartsWith("[ ]") || l.StartsWith("[x]") || l.StartsWith("[X]");

        public void Parse(string content)
        {
            if (Kind != "lista") { Text = content; return; }
            Items.Clear();
            foreach (var raw in content.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.Trim().Length == 0) continue;
                var t = l.TrimStart();
                if (IsTodoLine(t))
                {
                    var text = t.Substring(3);
                    if (text.StartsWith(" ")) text = text.Substring(1);
                    Items.Add(new TodoItem { Done = t[1] == 'x' || t[1] == 'X', Text = text });
                }
                else Items.Add(new TodoItem { Text = l.Trim() });
            }
        }
    }

    /// Borradores: cada nota vive en %LOCALAPPDATA%\NotasEscritorio\cache\<id>.json hasta que se quita del panel.
    public class NoteCache
    {
        public readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotasEscritorio", "cache");
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        public NoteCache() { Directory.CreateDirectory(Dir); }

        string PathOf(Note n) => Path.Combine(Dir, n.Id + ".json");

        public List<Note> LoadAll(List<string> order)
        {
            var notes = new List<Note>();
            foreach (var p in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var n = JsonSerializer.Deserialize<Note>(File.ReadAllText(p));
                    if (n != null) { n.Items ??= new(); notes.Add(n); }
                }
                catch { }
            }
            var idx = order.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            return notes.Where(n => !idx.ContainsKey(n.Id)).OrderByDescending(n => n.Modified)
                .Concat(notes.Where(n => idx.ContainsKey(n.Id)).OrderBy(n => idx[n.Id])).ToList();
        }

        public void Save(Note n)
        {
            try
            {
                var tmp = PathOf(n) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(n, Opts));
                File.Move(tmp, PathOf(n), true);
            }
            catch { }
        }

        public void Remove(Note n)
        {
            try { File.Delete(PathOf(n)); } catch { }
        }
    }

    static class TextFiles
    {
        static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// Lee un .txt en UTF-8 o, si no lo es, en la codificación clásica de Windows (acentos incluidos).
        public static string Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return StrictUtf8.GetString(bytes); }
            catch { return Encoding.GetEncoding(1252).GetString(bytes); }
        }

        public static void Write(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // ================================================================ Win32

    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int GWLP_HWNDPARENT = -8;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_DPICHANGED = 0x02E0;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public const int DWMWA_BORDER_COLOR = 34;
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
        public const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
        [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
    }

    // ================================================================ Ventana

    public class MainWindow : Window
    {
        readonly Config _cfg;
        readonly NoteCache _cache = new();
        List<Note> _notes = new();
        StackPanel _list;
        ScrollViewer _scroll;
        TextBox _search;
        WF.NotifyIcon _tray;
        IntPtr _hwnd;
        uint _taskbarCreatedMsg;
        bool _exiting;

        // Editor activo (para la barra de formato y Ctrl+S)
        TextBox _activeBox;
        Note _activeNote;

        readonly Dictionary<Note, DispatcherTimer> _cacheTimers = new();
        readonly Dictionary<Note, Action> _footerUpdaters = new();

        static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        static readonly (string key, string name, string hex)[] Palette =
        {
            ("blanco", "Blanco", "#FFFFFF"),
            ("amarillo", "Amarillo", "#FFF3B0"),
            ("rosa", "Rosa", "#FFDCE5"),
            ("azul", "Azul", "#DCEBFF"),
            ("verde", "Verde", "#DDF4E2"),
            ("lila", "Lila", "#ECE4FF"),
        };

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "NotasEscritorio";
        static readonly CornerRadius Square = new(0);

        public const string ShowEventName = "NotasEscritorio_Mostrar";
        EventWaitHandle _showEvent;
        bool _raise; // true mientras se muestra delante tras pinchar el icono anclado

        void ListenForShowRequests()
        {
            try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); } catch { return; }
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); } catch { return; }
                    Dispatcher.BeginInvoke(new Action(ShowFromPin));
                }
            }) { IsBackground = true, Name = "MostrarPanel" };
            t.Start();
        }

        /// Clic en el icono anclado: el primer clic trae el panel al frente; el siguiente lo devuelve al fondo.
        /// También vuelve al fondo si se pincha en otra ventana.
        void ShowFromPin()
        {
            if (_raise && IsVisible) { LowerPanel(); return; }
            if (!IsVisible) { Show(); AttachToDesktop(); Reposition(); }
            _raise = true;
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
            Activate();
            _fgBaseline = IntPtr.Zero;
            _raiseWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, e) => WatchForeground(), Dispatcher);
            _raiseWatch.Start();
        }

        DispatcherTimer _raiseWatch;
        IntPtr _fgBaseline;

        void LowerPanel()
        {
            _raise = false;
            _raiseWatch?.Stop();
            SendToBottom();
        }

        void WatchForeground()
        {
            if (!_raise) { _raiseWatch.Stop(); return; }
            var fg = Native.GetForegroundWindow();
            if (_fgBaseline == IntPtr.Zero) { _fgBaseline = fg == IntPtr.Zero ? new IntPtr(-1) : fg; return; }
            if (fg == _fgBaseline || fg == IntPtr.Zero || IsOwnWindow(fg) || IsTaskbar(fg)) return;
            LowerPanel(); // el usuario pasó a otra ventana
        }

        static bool IsOwnWindow(IntPtr h)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        static bool IsTaskbar(IntPtr h)
        {
            var sb = new StringBuilder(64);
            Native.GetClassName(h, sb, sb.Capacity);
            var c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c == "XamlExplorerHostIslandWindow";
        }

        /// Crea (o actualiza) el acceso directo en el menú Inicio con el icono de la app,
        /// para poder buscar "Notas Escritorio" y anclarla a la barra de tareas.
        static void EnsureStartMenuShortcut()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                var lnk = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Notas Escritorio.lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = exe;
                sc.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
                sc.IconLocation = exe + ",0";
                sc.Description = "Notas Escritorio";
                sc.Save();
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(shell);
            }
            catch { }
        }

        public MainWindow()
        {
            _cfg = Config.Load();
            ListenForShowRequests();
            new Thread(EnsureStartMenuShortcut) { IsBackground = true, ApartmentState = ApartmentState.STA }.Start();

            Title = "Notas Escritorio";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = WantsLayered;   // ventana sin transparencia = texto más nítido (ClearType)
            Background = AllowsTransparency ? Brushes.Transparent : B(Opaque(T.Panel));
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            ShowInTaskbar = false;
            Topmost = false;
            ShowActivated = false;
            FontFamily = UiFont;
            WindowStartupLocation = WindowStartupLocation.Manual;

            try
            {
                using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico");
                if (s != null) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(s, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            catch { }

            if (!_cfg.FirstRunDone)
            {
                SetAutostart(true);
                _cfg.FirstRunDone = true;
                _cfg.MigratedV2 = true;
                CreateWelcome();
            }
            else
            {
                if (IsAutostart()) SetAutostart(true); // actualiza la ruta si el .exe se movió
                if (!_cfg.MigratedV2) MigrateFromV1();
            }
            _cfg.Save();

            _notes = _cache.LoadAll(_cfg.CacheOrder);
            SaveOrder();

            BuildUi();
            Reposition();
            Rebuild();
            SetupTray();
            foreach (var fn in _notes.Where(x => x.IsFloating).ToList()) OpenFloat(fn);

            // Abierta por la persona (icono anclado, Inicio, doble clic): se muestra delante.
            // Al iniciar Windows (--inicio) se queda en el escritorio, detrás de todo.
            bool atLogin = Environment.GetCommandLineArgs().Any(a => a.Equals("--inicio", StringComparison.OrdinalIgnoreCase));
            if (!atLogin) ContentRendered += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowFromPin));

            PreviewKeyDown += OnShortcut;
            SystemParameters.StaticPropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(new Action(Reposition));
            };
            SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Reposition));
        }

        // ---------- Primera ejecución / versión anterior

        void CreateWelcome()
        {
            var lista = new Note { Kind = "lista", Title = "Pendientes" };
            lista.Items.Add(new TodoItem { Text = "Probar mi nueva app de notas", Done = true });
            lista.Items.Add(new TodoItem { Text = "Guardar una nota como .txt" });
            lista.Items.Add(new TodoItem { Text = "Abrir un .txt que ya tenga" });
            _cache.Save(lista);

            var n = new Note
            {
                Title = "Bienvenida",
                Text = "Lo que escribes se queda como borrador en el panel aunque apagues el PC.\r\n\r\n" +
                       "- Pulsa **Guardar** (o Ctrl+S) para elegir dónde guardar el .txt\r\n" +
                       "- Pulsa **Abrir** para traer un .txt que ya tengas\r\n" +
                       "- La barra de formato añade títulos, listas, **negrita**, *cursiva*, ~~tachado~~, enlaces y tablas",
                Modified = DateTime.Now.AddSeconds(1),
            };
            _cache.Save(n);
        }

        void MigrateFromV1()
        {
            // La versión 1 guardaba cada nota en Documentos\Notas Escritorio: se traen al panel enlazadas a su archivo.
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notas Escritorio");
                if (Directory.Exists(dir))
                {
                    var order = _cfg.Order ?? new List<string>();
                    var files = Directory.GetFiles(dir, "*.txt")
                        .OrderBy(f => { var i = order.FindIndex(o => string.Equals(o, Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; })
                        .ToList();
                    var t = DateTime.Now;
                    foreach (var f in files)
                    {
                        var fn = Path.GetFileName(f);
                        var content = TextFiles.Read(f);
                        var n = new Note
                        {
                            Title = Path.GetFileNameWithoutExtension(f),
                            FilePath = f,
                            SavedContent = content,
                            Modified = t,
                        };
                        t = t.AddSeconds(-1);
                        n.Kind = _cfg.Types != null && _cfg.Types.TryGetValue(fn, out var k) ? k : (Note.LooksLikeList(content) ? "lista" : "nota");
                        if (_cfg.Colors != null && _cfg.Colors.TryGetValue(fn, out var c)) n.Color = c;
                        n.Parse(content);
                        n.SavedContent = n.Kind == "lista" ? n.Serialize() : content;
                        _cache.Save(n);
                    }
                    _cfg.LastDir = dir;
                }
            }
            catch { }
            _cfg.Colors = null; _cfg.Types = null; _cfg.Order = null;
            _cfg.MigratedV2 = true;
        }

        void SaveOrder()
        {
            _cfg.CacheOrder = _notes.Select(n => n.Id).ToList();
            _cfg.Save();
        }

        // ---------- Escritorio: debajo de todo y sin pasar la barra de tareas

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            try { int v = Native.DWMWCP_DONOTROUND; Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); } catch { }
            try { int none = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(int)); } catch { }
            AttachToDesktop();
        }

        void AttachToDesktop()
        {
            if (_hwnd == IntPtr.Zero) return;
            var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex = (ex | Native.WS_EX_TOOLWINDOW) & ~(long)Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            var progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero) Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, progman);
            SendToBottom();
        }

        void SendToBottom()
        {
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if ((wp.flags & Native.SWP_NOZORDER) == 0 && !_raise)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            else if (msg == (int)_taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                Dispatcher.BeginInvoke(new Action(() => { AttachToDesktop(); Reposition(); }));
            }
            else if (msg == Native.WM_DISPLAYCHANGE || msg == Native.WM_SETTINGCHANGE || msg == Native.WM_DPICHANGED)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));
            }
            return IntPtr.Zero;
        }

        void Reposition()
        {
            var wa = SystemParameters.WorkArea; // área sin la barra de tareas
            double w = Math.Max(MinPanelWidth, Math.Min(_cfg.Width, MaxPanelWidth()));
            double left = _cfg.Side == "izquierda" ? wa.Left : wa.Right - w;
            
            // Tamaño y posición en un solo paso (si no, la ventana se ve cortada o a medio camino al encoger)
            var src = _hwnd != IntPtr.Zero ? PresentationSource.FromVisual(this) : null;
            if (src?.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformToDevice;
                Native.SetWindowPos(_hwnd, IntPtr.Zero,
                    (int)Math.Round(left * m.M11), (int)Math.Round(wa.Top * m.M22),
                    (int)Math.Round(w * m.M11), (int)Math.Round(wa.Height * m.M22),
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            Width = w;
            Height = wa.Height;
            Top = wa.Top;
            Left = left;
            RefreshDesktopBehind();
            if (_rootBorder != null)
                _rootBorder.BorderThickness = _cfg.Side == "izquierda" ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
            if (_resizeGrip != null)
                _resizeGrip.HorizontalAlignment = _cfg.Side == "izquierda" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            ApplyBarOnlyLayout();
        }

        /// Al encoger el panel, el escritorio de detrás podía quedarse con un trozo del panel viejo: se repinta.
        void RefreshDesktopBehind()
        {
            try
            {
                Native.RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ERASE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    Native.RedrawWindow(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.RDW_INVALIDATE | Native.RDW_ALLCHILDREN | Native.RDW_UPDATENOW)));
            }
            catch { }
        }

        void ApplyBarOnlyLayout()
        {
            if (_contentGrid == null) return;
            bool bar = Width <= BarOnlyWidth + 1;
            _contentGrid.Visibility = bar ? Visibility.Collapsed : Visibility.Visible;
            if (_contentColumn != null) _contentColumn.Width = bar ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (_railHolder != null)
            {
                bool railLeft = _cfg.Side != "izquierda";
                _railHolder.Margin = bar ? new Thickness(10, 2, 10, 10) : railLeft ? new Thickness(5, 2, 0, 10) : new Thickness(0, 2, 5, 10);
            }
            if (_captionBar != null)
            {
                _captionBar.HorizontalAlignment = bar ? HorizontalAlignment.Center : HorizontalAlignment.Right;
                foreach (var b in _captionBtns) b.Width = bar ? 22 : 42;
            }
            if (_maxGlyph != null)
            {
                bool full = Width >= MaxPanelWidth() - 1;
                _maxGlyph.Text = full ? "\uE923" : "\uE922";
                ((Border)_maxGlyph.Parent).ToolTip = full ? "Volver al ancho normal" : "Ocupar toda la pantalla";
                if (_minBtn != null) _minBtn.ToolTip = bar ? "Mostrar las notas" : "Dejar solo la barra";
            }
        }

        const double BarOnlyWidth = 72;     // solo la barra lateral
        const double MinContentWidth = 250; // ancho mínimo mostrando las notas
        const double MinPanelWidth = BarOnlyWidth;
        bool IsBarOnly => _cfg.Width <= BarOnlyWidth + 1;
        static double MaxPanelWidth() => Math.Max(MinPanelWidth, SystemParameters.WorkArea.Width); // puede ocupar toda la pantalla
        bool IsFull => _cfg.Width >= MaxPanelWidth() - 1;

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SendToBottom();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting) { e.Cancel = true; return; }
            base.OnClosing(e);
        }

        // ================================================================ Temas

        public record Theme(string Key, string Name, string Panel, string Rail, string Line, string Fg, string SubFg, string Field, string Hover, string Accent)
        {
            public string Frame => Rail;
        }

        static readonly Theme[] Themes =
        {
            new("claro",    "Claro",         "#F5F7F7F8", "#FFEDEEF0", "#14000000", "#1B1B1F", "#6E6E76", "#FFFFFF", "#12000000", "#0F6CBD"),
            new("rosa",     "Rosa pastel",   "#F2F6E4E8", "#FFEFD5DB", "#22000000", "#3A2A2E", "#7A6268", "#FFFFFF", "#18000000", "#D24B6E"),
            new("lavanda",  "Lavanda",       "#F2ECE7F8", "#FFE0D8F3", "#22000000", "#2E2840", "#6C6488", "#FFFFFF", "#18000000", "#7B5CD6"),
            new("menta",    "Menta",         "#F2E2F3EA", "#FFD1EBDD", "#22000000", "#1F3329", "#5C7A6A", "#FFFFFF", "#18000000", "#2E9E6A"),
            new("cielo",    "Cielo",         "#F2E2EDF9", "#FFD2E2F5", "#22000000", "#1E2C3D", "#5B6F86", "#FFFFFF", "#18000000", "#2F7BD8"),
            new("arena",    "Arena",         "#F2F4EDDF", "#FFEADFCB", "#22000000", "#3A3226", "#7E7262", "#FFFFFF", "#18000000", "#B7802E"),
            new("melocoton","Melocotón",     "#F2FBE8DC", "#FFF5D6C4", "#22000000", "#3D2B22", "#86695A", "#FFFFFF", "#18000000", "#E07A4A"),
            new("oscuro",   "Oscuro",        "#F2202020", "#FF2B2B2B", "#40FFFFFF", "#F2F2F2", "#A8A8A8", "#FF2D2D2D", "#22FFFFFF", "#4CC2FF"),
            new("noche",    "Noche azul",    "#F21B2233", "#FF242C40", "#40FFFFFF", "#EEF1F8", "#9AA5BE", "#FF2A3248", "#22FFFFFF", "#7AA2FF"),
            new("vidrio",   "Vidrio",        "#8CFFFFFF", "#A6FFFFFF", "#40FFFFFF", "#1F1F1F", "#4F4F4F", "#E6FFFFFF", "#22000000", "#0F6CBD"),
        };

        Theme T => Themes.FirstOrDefault(t => t.Key == _cfg.Theme) ?? Themes[0];

        /// Hace falta una ventana con transparencia (tema Vidrio o algún opacidad menor de 100 %)
        bool WantsLayered => IsGlass(T) || _cfg.PanelOpacity < 0.999 || _cfg.NoteOpacity < 0.999;
        readonly List<Action> _panelOpacityUpdaters = new();
        readonly Dictionary<Note, Action> _noteOpacityUpdaters = new();

        Brush PanelBrush(string hex)
        {
            var b = new SolidColorBrush(WithAlpha(C(hex), _cfg.PanelOpacity));
            _panelOpacityUpdaters.Add(() => b.Color = WithAlpha(C(hex), _cfg.PanelOpacity));
            return b;
        }

        /// Aplica la transparencia actual a lo que ya está dibujado, sin reconstruir nada
        void ApplyOpacityLive()
        {
            foreach (var u in _panelOpacityUpdaters) u();
            foreach (var u in _noteOpacityUpdaters.Values.ToList()) u();
        }
        SolidColorBrush NoteBrush(string hex) => new SolidColorBrush(WithAlpha(C(hex), _cfg.NoteOpacity));

        void SetTheme(string key)
        {
            bool wasLayered = AllowsTransparency;
            _cfg.Theme = key;
            _cfg.Save();
            if (WantsLayered != wasLayered) { RestartApp(); return; }
            if (!AllowsTransparency) Background = B(Opaque(T.Panel));
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshFloats));
            var q = _search?.Text ?? "";
            BuildUi();
            _search.Text = q;
            Reposition();
            Rebuild();
        }

        void ShowThemeMenu(UIElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = RailMenuPlacement };
            foreach (var th in Themes)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var sw = new Grid { Width = 28, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
                sw.ColumnDefinitions.Add(new ColumnDefinition());
                sw.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
                var a = new Border { Background = B(th.Panel), BorderBrush = B("#40000000"), BorderThickness = new Thickness(1, 1, 0, 1) };
                var c = new Border { Background = B(th.Accent), BorderBrush = B("#40000000"), BorderThickness = new Thickness(0, 1, 1, 1) };
                Grid.SetColumn(c, 1);
                sw.Children.Add(a); sw.Children.Add(c);
                sp.Children.Add(sw);
                sp.Children.Add(new TextBlock { Text = th.Name, VerticalAlignment = VerticalAlignment.Center });
                var mi = new MenuItem { Header = sp, IsCheckable = true, IsChecked = th.Key == T.Key };
                var key = th.Key;
                mi.Click += (s, e) => SetTheme(key);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        // ================================================================ Interfaz

        static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static bool IsGlass(Theme t) => C(t.Panel).A < 0xE0;
        static bool IsDark(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        /// Color sin transparencia (mezclado sobre blanco o negro)
        static string Opaque(string hex)
        {
            var c = C(hex);
            if (c.A == 255) return hex;
            byte bg = IsDark(c) ? (byte)0 : (byte)255;
            double a = c.A / 255.0;
            byte M(byte v) => (byte)Math.Round(v * a + bg * (1 - a));
            return Hex(Color.FromRgb(M(c.R), M(c.G), M(c.B)));
        }
        static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        TextBlock _subtitle;
        Grid _contentGrid;
        ColumnDefinition _contentColumn;
        FrameworkElement _railHolder;
        StackPanel _captionBar;
        readonly List<Border> _captionBtns = new();
        TextBlock _maxGlyph;
        Border _minBtn;
        Border _rootBorder;
        Border _resizeGrip;

        void BuildUi()
        {
            var th = T;
            Brush panelBg;
            _panelOpacityUpdaters.Clear();
            if (IsGlass(th)) panelBg = PanelBrush(th.Panel);
            else
            {
                var top = C(Opaque(th.Panel));
                var bottomC = Mix(top, C(th.Accent), IsDark(top) ? 0.10 : 0.07);
                var grad = new LinearGradientBrush(WithAlpha(top, _cfg.PanelOpacity), WithAlpha(bottomC, _cfg.PanelOpacity), 90);
                _panelOpacityUpdaters.Add(() =>
                {
                    grad.GradientStops[0].Color = WithAlpha(top, _cfg.PanelOpacity);
                    grad.GradientStops[1].Color = WithAlpha(bottomC, _cfg.PanelOpacity);
                });
                panelBg = grad;
            }
            _rootBorder = new Border
            {
                CornerRadius = Square,
                Background = panelBg,
                BorderBrush = B(th.Line),
                BorderThickness = new Thickness(1, 0, 0, 0),
            };

            // Columnas: la barra lateral va siempre en el lado interior del panel
            // (panel a la derecha → barra a la izquierda; panel a la izquierda → barra a la derecha)
            bool railLeft = _cfg.Side != "izquierda";
            var outer = new Grid();
            var colA = new ColumnDefinition { Width = railLeft ? GridLength.Auto : new GridLength(1, GridUnitType.Star) };
            var colB = new ColumnDefinition { Width = railLeft ? new GridLength(1, GridUnitType.Star) : GridLength.Auto };
            outer.ColumnDefinitions.Add(colA);
            outer.ColumnDefinitions.Add(colB);
            _contentColumn = railLeft ? colB : colA;

            // Fila de arriba: minimizar · solo barra / mostrar notas · cerrar (esquina superior derecha)
            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _captionBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _captionBtns.Clear();
            Border CapBtn(string glyph, string tip, Action click, bool danger = false)
            {
                var g = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 10, Foreground = B(th.Fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var b = new Border { Child = g, Width = 42, Height = 28, Background = Brushes.Transparent, ToolTip = tip };
                b.MouseEnter += (o, e) => { b.Background = danger ? B("#C42B1C") : B(th.Hover); if (danger) g.Foreground = Brushes.White; };
                b.MouseLeave += (o, e) => { b.Background = Brushes.Transparent; g.Foreground = B(th.Fg); };
                b.MouseLeftButtonUp += (o, e) => { click(); e.Handled = true; };
                _captionBtns.Add(b);
                _captionBar.Children.Add(b);
                return b;
            }
            _minBtn = CapBtn("\uE921", "Dejar solo la barra", ToggleBarOnly);
            _maxGlyph = (TextBlock)CapBtn("\uE922", "Ocupar toda la pantalla", ToggleFull).Child;
            CapBtn("\uE8BB", "Cerrar Notas Escritorio (tus notas quedan guardadas en el panel)", ExitApp, danger: true);
            shell.Children.Add(_captionBar);
            Grid.SetRow(outer, 1);
            shell.Children.Add(outer);
            _rootBorder.Child = shell;

            var grid = new Grid { Margin = new Thickness(7, 0, 4, 0) };
            _contentGrid = grid;
            Grid.SetColumn(grid, railLeft ? 1 : 0);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.Children.Add(grid);

            var header = new StackPanel { Margin = new Thickness(4, 0, 0, 12) };
            header.Children.Add(new TextBlock
            {
                Text = "Mis notas",
                FontSize = 26,
                FontFamily = DisplayFont,
                FontWeight = FontWeights.Bold,
                Foreground = B(th.Fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _subtitle = new TextBlock { FontSize = 12.5, Foreground = B(th.SubFg), Margin = new Thickness(1, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            header.Children.Add(_subtitle);
            UpdateSubtitle();
            grid.Children.Add(header);

            // Buscador
            _search = new TextBox
            {
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = B(th.Fg),
                CaretBrush = B(th.Fg),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 0, 0),
            };
            _search.TextChanged += (s, e) => ApplyFilter();
            var searchRow = new DockPanel();
            var searchIcon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = B(th.SubFg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
            DockPanel.SetDock(searchIcon, Dock.Left);
            searchRow.Children.Add(searchIcon);
            searchRow.Children.Add(WithPlaceholder(_search, "Buscar…", th.SubFg));
            var searchBox = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = B(th.Field),
                BorderBrush = B(th.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 0, 12, 0),
                Height = 36,
                Margin = new Thickness(0, 0, 4, 16),
                Child = searchRow,
            };
            _search.GotKeyboardFocus += (s, e) => { searchBox.BorderBrush = B(th.Accent); searchBox.BorderThickness = new Thickness(1.5); };
            _search.LostKeyboardFocus += (s, e) => { searchBox.BorderBrush = B(th.Line); searchBox.BorderThickness = new Thickness(1); };
            // (el buscador se quitó del panel; _search queda oculto para no romper el resto del código)

            // Tarjetas
            _list = new StackPanel { Margin = new Thickness(0, 0, 1, 8), AllowDrop = true, Background = Brushes.Transparent };
            _list.DragOver += List_DragOver;
            _list.DragLeave += (s, e) => HideDropIndicator();
            _list.Drop += List_Drop;
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _list,
                AllowDrop = true,
            };
            _scroll.PreviewMouseWheel += (s, e) =>
            {
                // Si el ratón está sobre una nota con alto fijo que todavía puede desplazarse, se desplaza la nota
                var inner = FindInnerScroll(e.OriginalSource as DependencyObject);
                if (inner != null && inner.ScrollableHeight > 0
                    && ((e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight) || (e.Delta > 0 && inner.VerticalOffset > 0)))
                    inner.ScrollToVerticalOffset(inner.VerticalOffset - e.Delta / 3.0);
                else
                    _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            };
            _scroll.DragOver += (s, e) => AutoScrollWhileDragging(e);
            Grid.SetRow(_scroll, 2);
            grid.Children.Add(_scroll);

            // Barra lateral izquierda con todas las opciones
            var rail = BuildRail();
            Grid.SetColumn(rail, railLeft ? 0 : 1);
            outer.Children.Add(rail);

            // Borde para ensanchar el panel
            _resizeGrip = new Border
            {
                Width = 6,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeWE,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Arrastra para cambiar el ancho",
            };
            Grid.SetRowSpan(_resizeGrip, 2);
            _resizeGrip.MouseEnter += (s, e) => _resizeGrip.Background = B(th.Hover);
            _resizeGrip.MouseLeave += (s, e) => { if (!_resizing) _resizeGrip.Background = Brushes.Transparent; };
            _resizeGrip.MouseLeftButtonDown += (s, e) => { _resizing = true; _resizeGrip.CaptureMouse(); e.Handled = true; };
            _resizeGrip.MouseMove += (s, e) => { if (_resizing) ResizeToCursor(); };
            _resizeGrip.MouseLeftButtonUp += (s, e) =>
            {
                if (!_resizing) return;
                _resizing = false;
                _resizeGrip.ReleaseMouseCapture();
                _resizeGrip.Background = Brushes.Transparent;
                _cfg.Save();
            };
            shell.Children.Add(_resizeGrip);

            Content = _rootBorder;
            ApplyBarOnlyLayout();
        }

        bool _resizing;

        void ResizeToCursor()
        {
            if (!Native.GetCursorPos(out var pt)) return;
            var src = PresentationSource.FromVisual(this);
            var dip = src?.CompositionTarget != null
                ? src.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y))
                : new Point(pt.X, pt.Y);
            var wa = SystemParameters.WorkArea;
            var w = _cfg.Side == "izquierda" ? dip.X - wa.Left : wa.Right - dip.X;
            w = Math.Min(w, MaxPanelWidth());
            w = w < (BarOnlyWidth + MinContentWidth) / 2 ? BarOnlyWidth : Math.Max(w, MinContentWidth); // se encaja: solo barra o con notas
            if (Math.Abs(w - _cfg.Width) < 1) return;
            _cfg.Width = Math.Round(w);
            if (!IsBarOnly && !IsFull) _cfg.ExpandedWidth = _cfg.Width;
            Reposition();
        }

        UIElement BuildRail()
        {
            var th = T;
            var top = new StackPanel { Orientation = Orientation.Vertical };
            var bottom = new StackPanel { Orientation = Orientation.Vertical };

            Border RailBtn(UIElement content, string tip, Action<Border> click, bool primary = false)
            {
                var accent = C(th.Accent);
                Brush normal = primary ? B(th.Accent) : Brushes.Transparent;
                Brush over = primary ? new SolidColorBrush(Mix(accent, Colors.Black, 0.15)) : B(th.Hover);
                var b = new Border
                {
                    Child = content,
                    Width = 38,
                    Height = 38,
                    Margin = new Thickness(0, primary ? 0 : 2, 0, primary ? 8 : 2),
                    Background = normal,
                    CornerRadius = new CornerRadius(primary ? 19 : 10),
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                };
                b.MouseEnter += (s, e) => b.Background = over;
                b.MouseLeave += (s, e) => b.Background = normal;
                b.MouseLeftButtonUp += (s, e) => { click(b); e.Handled = true; };
                return b;
            }

            TextBlock Ic(string glyph, double size = 15) => new()
            {
                Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = B(th.Fg), Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            TextBlock Tx(string text, FontWeight? w = null, FontStyle? st = null, TextDecorationCollection deco = null, FontFamily ff = null, double size = 15) => new()
            {
                Text = text, FontSize = size, FontWeight = w ?? FontWeights.Normal, FontStyle = st ?? FontStyles.Normal,
                TextDecorations = deco, FontFamily = ff ?? UiFont, Foreground = B(th.Fg),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            UIElement Sep() => new Border { Height = 1, Margin = new Thickness(9, 8, 9, 8), Background = B(th.Line) };

            // Crear / abrir (la tuerca de opciones va arriba)
            top.Children.Add(RailBtn(Ic(""), "Opciones", b => ShowOptionsMenu(b)));
            top.Children.Add(RailBtn(Ic(""), "Nueva lista de tareas", b => NewNote("lista")));
            top.Children.Add(RailBtn(Ic(""), "Abrir archivos .txt (Ctrl+O)", b => OpenFiles()));
            top.Children.Add(Sep());

            // Formato
            top.Children.Add(RailBtn(Tx("H1", FontWeights.SemiBold, size: 13), "Títulos", b => DropDown(b,
                ("Texto normal", () => SetHeading(0)),
                ("Título 1", () => SetHeading(1)),
                ("Título 2", () => SetHeading(2)),
                ("Título 3", () => SetHeading(3)))));
            top.Children.Add(RailBtn(Ic(""), "Listas", b => DropDown(b,
                ("Viñetas", () => SetList("bullet")),
                ("Lista numerada", () => SetList("number")),
                ("Casillas", () => SetList("check")))));
            top.Children.Add(RailBtn(Tx("B", FontWeights.Bold), "Negrita (Ctrl+B)", b => WrapSelection("**")));
            top.Children.Add(RailBtn(Tx("I", st: FontStyles.Italic, ff: new FontFamily("Georgia")), "Cursiva (Ctrl+I)", b => WrapSelection("*")));
            top.Children.Add(RailBtn(Tx("S", deco: TextDecorations.Strikethrough), "Tachado", b => WrapSelection("~~")));
            top.Children.Add(RailBtn(Ic(""), "Enlace (Ctrl+K)", b => InsertLink()));
            top.Children.Add(RailBtn(Ic("\uE8D2"), "Fuente", b => ShowFontMenu(b)));
            {
                var a = new Grid { Width = 20, Height = 22 };
                a.Children.Add(new TextBlock { Text = "A", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B(th.Fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
                var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
                bar.Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#B3261E"), 0), new GradientStop((Color)ColorConverter.ConvertFromString("#C25E00"), 0.25),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#1E7A46"), 0.5), new GradientStop((Color)ColorConverter.ConvertFromString("#1F4E9E"), 0.75),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#6A3FB5"), 1),
                }, 0);
                a.Children.Add(bar);
                top.Children.Add(RailBtn(a, "Color del texto seleccionado", b => ShowTextColorMenu(b)));
            }
            top.Children.Add(RailBtn(Tx("⊞", ff: new FontFamily("Segoe UI Symbol"), size: 17), "Tabla", b => DropDown(b,
                ("Tabla de 2 × 2", () => InsertTable(2, 2)),
                ("Tabla de 3 × 3", () => InsertTable(3, 3)),
                ("Tabla de 4 × 3", () => InsertTable(4, 3)))));

            // Abajo: tema y opciones
            bottom.Children.Add(RailBtn(Ic(""), "Tema", b => ShowThemeMenu(b)));
            {
                var plus = Ic("\uE710", 16); plus.Foreground = Brushes.White; plus.Opacity = 1; plus.FontWeight = FontWeights.Bold;
                bottom.Children.Add(RailBtn(plus, "Nueva nota", b => NewNote("nota"), primary: true));
            }

            var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 7, 0, 7) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);

            // Cápsula flotante: la sombra va en una capa aparte para que los iconos no se vean borrosos
            bool railLeft = _cfg.Side != "izquierda";
            var margin = railLeft ? new Thickness(5, 4, 0, 10) : new Thickness(0, 4, 5, 10);
            var holder = new Grid { Margin = margin, Width = 52 };
            _railHolder = holder;
            holder.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = PanelBrush(Opaque(th.Rail)),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = IsDark(C(Opaque(th.Rail))) ? 0.35 : 0.10 },
            });
            holder.Children.Add(new Border
            {
                Child = dock,
                CornerRadius = new CornerRadius(16),
                Background = PanelBrush(th.Rail),
                BorderBrush = B(th.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 6, 0),
            });
            return holder;
        }

        // ---------- Arrastrar notas para cambiar el orden

        Border _dropIndicator;
        int _dropIndex = -1;

        void StartCardDrag(Note n, Border card)
        {
            card.Opacity = 0.45;
            var effect = DragDropEffects.None;
            try { effect = DragDrop.DoDragDrop(card, new DataObject(typeof(Note), n), DragDropEffects.Move); }
            finally
            {
                card.Opacity = 1;
                HideDropIndicator();
            }
            // Soltada fuera del panel → pasa a ser una ventana flotante
            if (effect == DragDropEffects.None && Native.GetCursorPos(out var pt))
            {
                var dip = CursorToDip(new Point(pt.X, pt.Y));
                if (!new Rect(Left, Top, ActualWidth, ActualHeight).Contains(dip)) FloatNote(n, dip);
            }
        }

        Point CursorToDip(Point px)
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(px) : px;
        }

        // ---------- Notas flotantes (fuera del panel)

        readonly Dictionary<Note, Window> _floats = new();
        bool _floatClosing;

        void FloatNote(Note n, Point? at)
        {
            double w = 340, h = 260;
            var card = _list.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, n));
            if (card != null) { w = Math.Max(300, Math.Min(card.ActualWidth + 16, 560)); h = Math.Max(200, Math.Min(card.ActualHeight + 26, SystemParameters.WorkArea.Height - 40)); }
            var wa = SystemParameters.WorkArea;
            double x = at.HasValue ? at.Value.X - 40 : Left - w - 20;
            double y = at.HasValue ? at.Value.Y - 14 : wa.Top + 60;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            n.IsFloating = true;
            n.FloatLeft = x; n.FloatTop = y; n.FloatWidth = w; n.FloatHeight = h;
            _cache.Save(n);
            Rebuild();
            OpenFloat(n);
        }

        void DockNote(Note n)
        {
            CloseFloat(n);
            n.IsFloating = false;
            _cache.Save(n);
            Rebuild();
        }

        void CloseFloat(Note n)
        {
            if (!_floats.Remove(n, out var w)) return;
            _footerUpdaters.Remove(n);
            _floatClosing = true;
            try { w.Close(); } finally { _floatClosing = false; }
        }

        void OpenFloat(Note n)
        {
            if (_floats.ContainsKey(n)) return;
            var th = T;
            var wa = SystemParameters.WorkArea;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                ShowInTaskbar = false, Title = string.IsNullOrWhiteSpace(n.Title) ? "Nota" : n.Title,
                Background = Brushes.Transparent, FontFamily = UiFont, Icon = Icon,
                MinWidth = 240, MinHeight = 150, MaxWidth = wa.Width, MaxHeight = wa.Height,
                Width = n.FloatWidth ?? 340, Height = n.FloatHeight ?? 260,
                Left = n.FloatLeft ?? 100, Top = n.FloatTop ?? 100,
                WindowStartupLocation = WindowStartupLocation.Manual,
                UseLayoutRounding = true, SnapsToDevicePixels = true,
            };
            TextOptions.SetTextRenderingMode(w, TextRenderingMode.Grayscale);

            var cardEl = BuildCard(n, true);
            cardEl.Margin = new Thickness(14, 8, 14, 18); // deja sitio a la sombra
            var root = new Grid();
            root.Children.Add(cardEl);

            Border Grip(HorizontalAlignment ha, VerticalAlignment va, double gw, double gh, Cursor cur, bool dx, bool dy)
            {
                var g = new Border { Tag = "grip", Background = B("#01000000"), HorizontalAlignment = ha, VerticalAlignment = va, Cursor = cur };
                if (gw > 0) g.Width = gw;
                if (gh > 0) g.Height = gh;
                double sx = 0, sy = 0, sw = 0, sh = 0; bool on = false;
                g.MouseLeftButtonDown += (o, e) =>
                {
                    on = true; g.CaptureMouse();
                    Native.GetCursorPos(out var p0); sx = p0.X; sy = p0.Y; sw = w.ActualWidth; sh = w.ActualHeight;
                    e.Handled = true;
                };
                g.MouseMove += (o, e) =>
                {
                    if (!on || !Native.GetCursorPos(out var p)) return;
                    var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                    if (dx) w.Width = Math.Max(w.MinWidth, Math.Min(w.MaxWidth, sw + (p.X - sx) * m.M11));
                    if (dy) w.Height = Math.Max(w.MinHeight, Math.Min(w.MaxHeight, sh + (p.Y - sy) * m.M22));
                };
                g.MouseLeftButtonUp += (o, e) => { on = false; g.ReleaseMouseCapture(); e.Handled = true; };
                return g;
            }
            root.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Stretch, 12, 0, Cursors.SizeWE, true, false));
            root.Children.Add(Grip(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, 0, 14, Cursors.SizeNS, false, true));
            root.Children.Add(Grip(HorizontalAlignment.Right, VerticalAlignment.Bottom, 22, 22, Cursors.SizeNWSE, true, true));
            w.Content = root;

            // Se mueve agarrándola por cualquier parte que no sea texto o botón
            w.PreviewMouseLeftButtonDown += (o, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && Equals(fe.Tag, "grip")) return;
                if (IsInteractive(e.OriginalSource as DependencyObject, w)) return;
                try { w.DragMove(); } catch { }
            };

            // Guarda posición y tamaño
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            saveTimer.Tick += (o, e) =>
            {
                saveTimer.Stop();
                if (w.WindowState != WindowState.Normal) return;
                n.FloatLeft = w.Left; n.FloatTop = w.Top; n.FloatWidth = w.Width; n.FloatHeight = w.Height;
                _cache.Save(n);
            };
            w.LocationChanged += (o, e) => { saveTimer.Stop(); saveTimer.Start(); };
            w.SizeChanged += (o, e) => { saveTimer.Stop(); saveTimer.Start(); };
            w.Closing += (o, e) => { if (!_exiting && !_floatClosing) e.Cancel = true; };
            w.Closed += (o, e) => _floats.Remove(n);
            w.GotKeyboardFocus += (o, e) => { _activeNote = n; };
            w.PreviewKeyDown += OnShortcut;

            _floats[n] = w;
            w.Show();
        }

        /// Vuelve a dibujar las ventanas flotantes (cambio de tema, etc.)
        void RefreshFloats()
        {
            foreach (var n in _floats.Keys.ToList())
            {
                var w = _floats[n];
                n.FloatLeft = w.Left; n.FloatTop = w.Top; n.FloatWidth = w.Width; n.FloatHeight = w.Height;
                CloseFloat(n);
                OpenFloat(n);
            }
        }

        List<Border> CardsInList() => _list.Children.OfType<Border>().Where(b => b.Tag is Note && b.Visibility == Visibility.Visible).ToList();

        void List_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Note))) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move;
            var cards = CardsInList();
            int target = cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                var p = e.GetPosition(cards[i]);
                if (p.Y < cards[i].ActualHeight / 2) { target = i; break; }
            }
            ShowDropIndicator(target, cards);
            AutoScrollWhileDragging(e);
            e.Handled = true;
        }

        void ShowDropIndicator(int target, List<Border> cards)
        {
            _dropIndex = target;
            _dropIndicator ??= new Border { Height = 3, Margin = new Thickness(0, -6, 0, 3), Background = B(T.Accent), IsHitTestVisible = false };
            _dropIndicator.Background = B(T.Accent);
            int childIndex = target < cards.Count ? _list.Children.IndexOf(cards[target]) : _list.Children.Count;
            var current = _list.Children.IndexOf(_dropIndicator);
            if (current >= 0)
            {
                if (current == childIndex || current == childIndex - 1) return;
                _list.Children.RemoveAt(current);
                if (current < childIndex) childIndex--;
            }
            _list.Children.Insert(Math.Min(childIndex, _list.Children.Count), _dropIndicator);
        }

        void HideDropIndicator()
        {
            if (_dropIndicator != null && _list.Children.Contains(_dropIndicator)) _list.Children.Remove(_dropIndicator);
        }

        void List_Drop(object sender, DragEventArgs e)
        {
            HideDropIndicator();
            if (e.Data.GetData(typeof(Note)) is not Note n) return;
            var visible = CardsInList().Select(c => (Note)c.Tag).ToList();
            int target = Math.Max(0, Math.Min(_dropIndex, visible.Count));
            // Nota que quedará justo después de la arrastrada
            Note before = target < visible.Count ? visible[target] : null;
            if (before == n) return;
            var others = visible.Where(x => x != n).ToList();
            _notes.Remove(n);
            int idx = before != null ? _notes.IndexOf(before) : (others.Count > 0 ? _notes.IndexOf(others.Last()) + 1 : _notes.Count);
            if (idx < 0) idx = _notes.Count;
            _notes.Insert(idx, n);
            SaveOrder();
            Dispatcher.BeginInvoke(new Action(Rebuild));
            e.Handled = true;
        }

        void AutoScrollWhileDragging(DragEventArgs e)
        {
            var p = e.GetPosition(_scroll);
            const double zone = 40;
            if (p.Y < zone) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 12);
            else if (p.Y > _scroll.ActualHeight - zone) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 12);
        }

        // ---------- Controles pequeños

        Border IconButton(string glyph, string tooltip, Action onClick, double size = 12, string fg = "#5F5F5F", string hover = "#14000000")
        {
            var b = new Border
            {
                Child = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = B(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
            };
            b.MouseEnter += (s, e) => b.Background = B(hover);
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { onClick(); e.Handled = true; };
            return b;
        }

        static Grid WithPlaceholder(TextBox tb, string placeholder, string color = "#9A9A9A")
        {
            var g = new Grid();
            var ph = new TextBlock
            {
                Text = placeholder,
                Foreground = B(color),
                FontSize = tb.FontSize,
                Margin = new Thickness(tb.Padding.Left + 2, tb.Padding.Top, 0, 0),
                IsHitTestVisible = false,
                VerticalAlignment = tb.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
            };
            void Update() => ph.Visibility = string.IsNullOrEmpty(tb.Text) ? Visibility.Visible : Visibility.Collapsed;
            tb.TextChanged += (s, e) => Update();
            Update();
            g.Children.Add(ph);
            g.Children.Add(tb);
            return g;
        }

        // ================================================================ Formato
        // Un .txt no guarda estilos, así que el formato se escribe con marcas de texto (Markdown):
        // # Título, - viñeta, 1. numerada, - [ ] casilla, **negrita**, *cursiva*, ~~tachado~~, [texto](enlace), | tabla |

        void DropDown(UIElement anchor, params (string text, Action action)[] items)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = RailMenuPlacement };
            foreach (var (text, action) in items)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (s, e) => action();
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        // ================================================================ Formato real (RichTextBox)
        // El texto de las notas se edita con formato de verdad. El .txt guarda solo el texto;
        // el formato se conserva en el panel.

        RichTextBox _activeRich;

        bool HasRich()
        {
            if (_activeRich != null && _activeRich.IsLoaded) return true;
            if (_activeBox != null && _activeBox.IsLoaded)
                ShowInfo("El formato se usa en las notas de texto, no en las listas de tareas.");
            else
                ShowInfo("Haz clic dentro de una nota (o selecciona un trozo de texto) y luego usa el botón.");
            return false;
        }

        void ShowInfo(string msg) => ModernDialog("Notas Escritorio", msg, null, ("Entendido", "ok", 1));

        bool Confirm(string title, string msg, string detail, string yesLabel, bool danger = false) =>
            ModernDialog(title, msg, detail, (yesLabel, "yes", danger ? 2 : 1), ("Cancelar", "no", 0)) == "yes";

        /// Ventana de aviso moderna. Estilo de botón: 0 normal, 1 principal (azul), 2 peligro (rojo).
        string ModernDialog(string title, string message, string detail, params (string label, string key, int style)[] buttons)
        {
            string result = null;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = UiFont,
            };
            if (IsVisible) w.Owner = this; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var body = new StackPanel { Margin = new Thickness(26, 24, 26, 20), MaxWidth = 420, MinWidth = 320 };
            body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = B("#1F1F1F") });
            if (!string.IsNullOrEmpty(message))
                body.Children.Add(new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = B("#333333") });
            if (!string.IsNullOrEmpty(detail))
                body.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = B("#6B6B6B") });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 14, 18, 16) };
            foreach (var (label, key, style) in buttons)
            {
                string bg = style == 1 ? "#0F6CBD" : style == 2 ? "#C42B1C" : "#FFFFFF";
                string hov = style == 1 ? "#115EA3" : style == 2 ? "#A82316" : "#F3F3F3";
                var b = new Border
                {
                    Child = new TextBlock { Text = label, FontSize = 13.5, Foreground = style == 0 ? B("#1F1F1F") : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    Background = B(bg), BorderBrush = style == 0 ? B("#26000000") : B(bg), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 7, 16, 8), Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand, MinWidth = 100,
                };
                b.MouseEnter += (o, e) => b.Background = B(hov);
                b.MouseLeave += (o, e) => b.Background = B(bg);
                var k = key;
                // El clic en un botón no debe iniciar el arrastre de la ventana
                b.MouseLeftButtonDown += (o, e) => e.Handled = true;
                b.MouseLeftButtonUp += (o, e) => { result = k; w.Close(); };
                btnRow.Children.Add(b);
            }
            var footer = new Border { Background = B("#F5F5F5"), BorderBrush = B("#12000000"), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 12, 12), Child = btnRow };
            var stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(footer);
            var card = new Border
            {
                Margin = new Thickness(18), Background = Brushes.White, CornerRadius = new CornerRadius(12),
                BorderBrush = B("#1A000000"), BorderThickness = new Thickness(1), Child = stack,
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
            };
            card.MouseLeftButtonDown += (o, e) => { try { w.DragMove(); } catch { } };
            w.Content = card;
            var primary = buttons.FirstOrDefault(x => x.style != 0).key ?? buttons[0].key;
            var cancel = buttons.LastOrDefault(x => x.style == 0).key;
            w.KeyDown += (o, e) =>
            {
                if (e.Key == Key.Escape) { result = cancel; w.Close(); }
                else if (e.Key == Key.Enter) { result = primary; w.Close(); }
            };
            w.ShowDialog();
            return result;
        }

        void ApplyToSelection(DependencyProperty prop, object value)
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            rtb.Focus();
            rtb.Selection.ApplyPropertyValue(prop, value);
        }

        void WrapSelection(string m)
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            rtb.Focus();
            if (m == "**") EditingCommands.ToggleBold.Execute(null, rtb);
            else if (m == "*") EditingCommands.ToggleItalic.Execute(null, rtb);
            else
            {
                var cur = rtb.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
                bool has = cur != null && cur.Any(d => d.Location == TextDecorationLocation.Strikethrough);
                rtb.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
            }
        }

        void InsertLink()
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            var sel = rtb.Selection;
            var text = sel.Text.Trim();
            var url = AskText("Enlace", "Dirección del enlace (https://…):",
                Regex.IsMatch(text, @"^(https?://|www\.)") ? text : "https://");
            if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
            if (!url.StartsWith("http")) url = "https://" + url;
            rtb.Focus();
            try
            {
                if (sel.IsEmpty)
                {
                    var run = new Run(url, sel.Start);
                    new Hyperlink(run.ContentStart, run.ContentEnd) { NavigateUri = new Uri(url) };
                }
                else new Hyperlink(sel.Start, sel.End) { NavigateUri = new Uri(url) };
            }
            catch { ShowInfo("No se pudo crear el enlace en esa posición."); }
        }

        string AskText(string title, string label, string initial)
        {
            var w = new Window
            {
                Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = UiFont, Background = B("#F7F7F7"),
            };
            var tb = new TextBox { Text = initial, Width = 320, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 12) };
            var ok = new Button { Content = "Aceptar", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += (s, e) => w.DialogResult = true;
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = label });
            root.Children.Add(tb);
            root.Children.Add(btns);
            w.Content = root;
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? tb.Text.Trim() : null;
        }

        IEnumerable<Paragraph> SelectedParagraphs(RichTextBox rtb)
        {
            var start = rtb.Selection.Start.Paragraph;
            var end = rtb.Selection.End.Paragraph;
            if (start == null) yield break;
            var all = AllParagraphs(rtb.Document.Blocks).ToList();
            int i = all.IndexOf(start), j = end != null ? all.IndexOf(end) : i;
            if (i < 0) yield break;
            if (j < i) j = i;
            for (int k = i; k <= j; k++) yield return all[k];
        }

        static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
        {
            foreach (var b in blocks)
            {
                if (b is Paragraph p) yield return p;
                else if (b is List l) foreach (var li in l.ListItems) foreach (var q in AllParagraphs(li.Blocks)) yield return q;
                else if (b is Section sec) foreach (var q in AllParagraphs(sec.Blocks)) yield return q;
                else if (b is Table t) foreach (var rg in t.RowGroups) foreach (var r in rg.Rows) foreach (var c in r.Cells) foreach (var q in AllParagraphs(c.Blocks)) yield return q;
            }
        }

        void SetHeading(int level)
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            rtb.Focus();
            double baseSize = _cfg.FontSize;
            foreach (var p in SelectedParagraphs(rtb).ToList())
            {
                var range = new TextRange(p.ContentStart, p.ContentEnd);
                if (level == 0)
                {
                    range.ApplyPropertyValue(TextElement.FontSizeProperty, baseSize);
                    range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                }
                else
                {
                    range.ApplyPropertyValue(TextElement.FontSizeProperty, level == 1 ? baseSize + 9 : level == 2 ? baseSize + 5 : baseSize + 2);
                    range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
                }
            }
        }

        void SetList(string kind)
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            rtb.Focus();
            if (kind == "bullet") { EditingCommands.ToggleBullets.Execute(null, rtb); return; }
            if (kind == "number") { EditingCommands.ToggleNumbering.Execute(null, rtb); return; }
            // Casillas: ☐ al inicio de cada línea (clic sobre ☐ la marca ☑)
            var paras = SelectedParagraphs(rtb).ToList();
            bool allHave = paras.All(p => new TextRange(p.ContentStart, p.ContentEnd).Text.StartsWith("☐") || new TextRange(p.ContentStart, p.ContentEnd).Text.StartsWith("☑"));
            foreach (var p in paras)
            {
                var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
                if (allHave)
                {
                    var start = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                    var end = start.GetPositionAtOffset(text.Length > 1 && text[1] == ' ' ? 2 : 1);
                    if (end != null) new TextRange(start, end).Text = "";
                }
                else if (!text.StartsWith("☐") && !text.StartsWith("☑"))
                    p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).InsertTextInRun("☐ ");
            }
        }

        void InsertTable(int cols, int rows)
        {
            if (!HasRich()) return;
            var rtb = _activeRich;
            rtb.Focus();
            var table = new Table { CellSpacing = 0, BorderBrush = B("#33000000"), BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 4, 0, 4) };
            for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            for (int r = 0; r < rows; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < cols; c++)
                {
                    var cell = new TableCell(new Paragraph(new Run(r == 0 ? "Columna " + (c + 1) : "")))
                    {
                        BorderBrush = B("#33000000"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2, 4, 2),
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    };
                    row.Cells.Add(cell);
                }
                group.Rows.Add(row);
            }
            table.RowGroups.Add(group);
            // Bloque de primer nivel del documento donde está el cursor
            DependencyObject cur = rtb.Selection.End.Paragraph;
            while (cur is TextElement te && te.Parent is not FlowDocument) cur = te.Parent;
            var anchor = cur as Block;
            if (anchor != null && anchor.Parent is FlowDocument) rtb.Document.Blocks.InsertAfter(anchor, table);
            else rtb.Document.Blocks.Add(table);
            rtb.Document.Blocks.InsertAfter(table, new Paragraph());
        }

        /// Texto plano del documento para el .txt (viñetas como "• ", numeradas "1. ", tablas con " | ").
        static string DocToPlain(FlowDocument doc)
        {
            var lines = new List<string>();
            void Blocks(BlockCollection blocks, string prefix)
            {
                foreach (var b in blocks)
                {
                    if (b is Paragraph p) lines.Add(prefix + new TextRange(p.ContentStart, p.ContentEnd).Text);
                    else if (b is List l)
                    {
                        int i = l.StartIndex;
                        foreach (var li in l.ListItems)
                        {
                            var marker = l.MarkerStyle == TextMarkerStyle.Decimal ? (i++) + ". " : "• ";
                            int before = lines.Count;
                            Blocks(li.Blocks, "");
                            if (lines.Count > before) lines[before] = prefix + marker + lines[before];
                            for (int k = before + 1; k < lines.Count; k++) lines[k] = prefix + "   " + lines[k];
                        }
                    }
                    else if (b is Table t)
                        foreach (var rg in t.RowGroups)
                            foreach (var r in rg.Rows)
                                lines.Add(prefix + string.Join(" | ", r.Cells.Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Replace("\r\n", " ").Trim())));
                    else if (b is Section sec) Blocks(sec.Blocks, prefix);
                    else lines.Add(prefix + new TextRange(b.ContentStart, b.ContentEnd).Text);
                }
            }
            Blocks(doc.Blocks, "");
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\r\n", lines);
        }

        static string DocToXaml(FlowDocument doc)
        {
            try
            {
                using var ms = new MemoryStream();
                new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Xaml);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch { return null; }
        }

        /// Quita el color "de toda la nota" que guardaban versiones anteriores, para que el texto quede negro.
        /// Los colores puestos a fragmentos (en trozos de texto) se mantienen.
        static void ClearDocWideColor(FlowDocument doc)
        {
            foreach (var b in doc.Blocks.ToList())
            {
                b.ClearValue(TextElement.ForegroundProperty);
                if (b is Section) b.ClearValue(TextElement.FontFamilyProperty); // letra antigua de toda la nota
                if (b is Section sec) foreach (var x in sec.Blocks) x.ClearValue(TextElement.ForegroundProperty);
            }
            foreach (var p in AllParagraphs(doc.Blocks)) p.ClearValue(TextElement.ForegroundProperty);
        }

        static void LoadDoc(FlowDocument doc, Note n)
        {
            doc.Blocks.Clear();
            if (!string.IsNullOrEmpty(n.RichXaml))
            {
                try
                {
                    using var ms = new MemoryStream(Encoding.UTF8.GetBytes(n.RichXaml));
                    new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.Xaml);
                    ClearDocWideColor(doc);
                    return;
                }
                catch { doc.Blocks.Clear(); }
            }
            foreach (var line in (n.Text ?? "").Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)));
        }

        RichTextBox BuildRichBody(Note n)
        {
            var rtb = new RichTextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(-2, 0, 0, 0),
                MinHeight = 38,
                AcceptsTab = false,
                IsDocumentEnabled = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontSize = _cfg.FontSize,
                FontFamily = NoteFont,
                Foreground = B(FontColorHex),
                CaretBrush = B("#333333"),
            };
            var pStyle = new Style(typeof(Paragraph));
            pStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            rtb.Resources.Add(typeof(Paragraph), pStyle);
            var lStyle = new Style(typeof(List));
            lStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            lStyle.Setters.Add(new Setter(List.PaddingProperty, new Thickness(18, 0, 0, 0)));
            rtb.Resources.Add(typeof(List), lStyle);
            rtb.Document.PagePadding = new Thickness(0);
            rtb.Document.FontFamily = NoteFont;
            rtb.Document.FontSize = _cfg.FontSize;
            rtb.Document.Foreground = B(FontColorHex);
            LoadDoc(rtb.Document, n);

            bool loading = true;
            rtb.TextChanged += (s, e) =>
            {
                if (loading) return;
                n.Text = DocToPlain(rtb.Document);
                n.RichXaml = DocToXaml(rtb.Document);
                n.Markdown = MarkdownIO.FromDoc(rtb.Document, _cfg.FontSize);
                Touch(n);
            };
            rtb.Loaded += (s, e) => loading = false;
            rtb.GotKeyboardFocus += (s, e) => { _activeRich = rtb; _activeBox = null; _activeNote = n; };

            // Enlaces: Ctrl+clic para abrir
            // Clic derecho sobre el texto: copiar/pegar, formato y color del fragmento
            rtb.ContextMenuOpening += (s, e) =>
            {
                _activeRich = rtb; _activeBox = null; _activeNote = n;
                var cm = new ContextMenu();
                void Cmd(string text, ICommand c) { var mi = new MenuItem { Header = text, Command = c, CommandTarget = rtb }; cm.Items.Add(mi); }
                Cmd("Cortar", ApplicationCommands.Cut);
                Cmd("Copiar", ApplicationCommands.Copy);
                Cmd("Pegar", ApplicationCommands.Paste);
                cm.Items.Add(new Separator());
                var bold = new MenuItem { Header = "Negrita" }; bold.Click += (o, a) => WrapSelection("**"); cm.Items.Add(bold);
                var ital = new MenuItem { Header = "Cursiva" }; ital.Click += (o, a) => WrapSelection("*"); cm.Items.Add(ital);
                var strk = new MenuItem { Header = "Tachado" }; strk.Click += (o, a) => WrapSelection("~~"); cm.Items.Add(strk);
                cm.Items.Add(new Separator());
                var col = new MenuItem { Header = "Color del texto" };
                FillTextColorItems(col.Items);
                cm.Items.Add(col);
                rtb.ContextMenu = cm;
            };

            rtb.AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((s, e) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); } catch { }
                e.Handled = true;
            }));

            // Casillas: clic sobre ☐ / ☑ para marcarla
            rtb.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var pos = rtb.GetPositionFromPoint(e.GetPosition(rtb), false);
                if (pos == null) return;
                foreach (var dir in new[] { LogicalDirection.Forward, LogicalDirection.Backward })
                {
                    var t = pos.GetTextInRun(dir);
                    if (string.IsNullOrEmpty(t)) continue;
                    char c = dir == LogicalDirection.Forward ? t[0] : t[^1];
                    if (c != '☐' && c != '☑') continue;
                    var p1 = dir == LogicalDirection.Forward ? pos : pos.GetPositionAtOffset(-1);
                    var p2 = p1?.GetPositionAtOffset(1);
                    if (p1 == null || p2 == null) return;
                    new TextRange(p1, p2).Text = c == '☐' ? "☑" : "☐";
                    break;
                }
            };
            return rtb;
        }

        void OnShortcut(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (!ctrl) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.S when _activeNote != null: SaveNote(_activeNote, shift); e.Handled = true; break;
                case Key.O: OpenFiles(); e.Handled = true; break;
                case Key.K when _activeRich != null && _activeRich.IsKeyboardFocusWithin: InsertLink(); e.Handled = true; break;
            }
        }

        void TrackEditor(TextBox tb, Note n)
        {
            tb.GotKeyboardFocus += (s, e) => { _activeBox = tb; _activeRich = null; _activeNote = n; };
        }

        // ================================================================ Tarjetas

        void UpdateSubtitle()
        {
            if (_subtitle == null) return;
            var es = new System.Globalization.CultureInfo("es-ES");
            var d = DateTime.Now.ToString("dddd d 'de' MMMM", es);
            d = char.ToUpper(d[0]) + d.Substring(1);
            int c = _notes.Count;
            _subtitle.Text = d + "  ·  " + (c == 1 ? "1 nota" : c + " notas");
        }

        void Rebuild()
        {
            UpdateSubtitle();
            var offset = _scroll.VerticalOffset;
            _list.Children.Clear();
            foreach (var k in _footerUpdaters.Keys.Where(k => !k.IsFloating).ToList()) _footerUpdaters.Remove(k);
            _activeBox = null;
            _activeRich = null;
            if (!_notes.Any(x => !x.IsFloating))
            {
                _list.Children.Add(new TextBlock
                {
                    Text = "No hay notas en el panel.\nUsa los botones de la barra derecha\npara crear o abrir una nota.",
                    Foreground = B(T.SubFg),
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 30, 0, 0),
                });
            }
            foreach (var n in _notes.Where(x => !x.IsFloating)) _list.Children.Add(BuildCard(n));
            ApplyFilter();
            _scroll.UpdateLayout();
            _scroll.ScrollToVerticalOffset(offset);
        }

        Border BuildCard(Note n, bool floating = false)
        {
            var th = T;
            // Diseño de la tarjeta: un marco exterior y dentro la hoja donde se escribe
            var card = new Border
            {
                Tag = n,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, floating ? 0 : 16),
            };
            var frameBrush = new SolidColorBrush(WithAlpha(FrameColor(ColorHex(n.Color)), _cfg.NoteOpacity));
            var edgeBrush = new SolidColorBrush(EdgeColor(ColorHex(n.Color)));
            var shadowFx = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.08 };
            var shadow = new Border { Margin = new Thickness(0, 19, 0, 0), CornerRadius = new CornerRadius(0, 14, 14, 14), Background = frameBrush, Effect = shadowFx, ClipToBounds = false };
            Panel cardStack = floating ? new DockPanel() : new StackPanel();
            var cardLayers = new Grid();
            cardLayers.Children.Add(shadow);
            // Con notas transparentes la sombra se sumaba al marco y la pestañita quedaba distinta: sin sombra

            cardLayers.Children.Add(cardStack);
            card.Child = cardLayers;
            card.MouseEnter += (s, e) => { shadowFx.BlurRadius = 24; shadowFx.ShadowDepth = 5; shadowFx.Opacity = 0.16; };
            card.MouseLeave += (s, e) => { shadowFx.BlurRadius = 16; shadowFx.ShadowDepth = 3; shadowFx.Opacity = 0.08; };

            // Pestañita arriba a la izquierda (con el punto de "sin guardar")
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = B("#8C8C8C"),
                Stroke = B("#40000000"),
                StrokeThickness = 0.5,
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tabX = IconButton("\uE711", "Quitar la nota del panel", () => RemoveNote(n), 12, th.SubFg, th.Hover);
            tabX.Width = 30; tabX.Height = 26;
            tabX.HorizontalAlignment = HorizontalAlignment.Right;
            tabX.VerticalAlignment = VerticalAlignment.Center;
            tabX.Margin = new Thickness(0, 0, 0, 0);
            var tabGrid = new Grid();
            tabGrid.Children.Add(dot);
            tabGrid.Children.Add(tabX);
            var band = new Border
            {
                Width = 76,
                Height = 20,
                CornerRadius = new CornerRadius(9, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = frameBrush,
                BorderBrush = edgeBrush,
                BorderThickness = new Thickness(1, 1, 1, 0),
                Margin = new Thickness(0, 0, 0, -1),
                Child = tabGrid,
            };
            Panel.SetZIndex(band, 1);
            cardStack.Children.Add(band);
            if (floating) DockPanel.SetDock(band, Dock.Top);

            // Marco
            var frame = new Border
            {
                CornerRadius = new CornerRadius(0, 14, 14, 14),
                Background = frameBrush,
                BorderBrush = edgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 5, 4, 2),
            };
            cardStack.Children.Add(frame);
            var frameGrid = new Grid();
            frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frameGrid.RowDefinitions.Add(new RowDefinition { Height = floating ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.Child = frameGrid;

            var paper = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = NoteBrush(ColorHex(n.Color)),
                BorderBrush = B("#12000000"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 10, 10),
                MinHeight = (n.CardHeight.HasValue || floating) ? 0 : 90,
            };
            Grid.SetRow(paper, 1);
            frameGrid.Children.Add(paper);
            // Al cambiar el color de la nota, el marco cambia con él
            // Iconos de la tarjeta: su color se elige según el fondo real del marco (claro u oscuro)
            var iconSetters = new List<Action<Brush>>();
            void ApplyLook(string hex)
            {
                bool tr = _cfg.NoteOpacity < 0.999;
                {
                    var fc = FrameColor(hex);
                    var bg = C(Opaque(th.Panel));
                    double a = _cfg.NoteOpacity;
                    var eff = Color.FromRgb((byte)(fc.R * a + bg.R * (1 - a)), (byte)(fc.G * a + bg.G * (1 - a)), (byte)(fc.B * a + bg.B * (1 - a)));
                    Brush ib = IsDark(eff) ? B("#F0F0F0") : B("#3F3F46");
                    foreach (var set in iconSetters) set(ib);
                }
                frameBrush.Color = WithAlpha(FrameColor(hex), _cfg.NoteOpacity);
                edgeBrush.Color = WithAlpha(EdgeColor(hex), _cfg.NoteOpacity);
                paper.Background = NoteBrush(hex);
                // Con transparencia: sin sombra ni bordes, y la pestaña no se monta sobre el marco
                // (si no, donde se superponen se ve una línea más clara)
                shadow.Visibility = tr ? Visibility.Collapsed : Visibility.Visible;
                band.BorderThickness = tr ? new Thickness(0) : new Thickness(1, 1, 1, 0);
                band.Margin = tr ? new Thickness(0) : new Thickness(0, 0, 0, -1);
                frame.BorderThickness = new Thickness(tr ? 0 : 1);
            }
            paper.Tag = new Action<string>(ApplyLook);
            ApplyLook(ColorHex(n.Color));
            _noteOpacityUpdaters[n] = () => ApplyLook(ColorHex(n.Color));

            var stack = new StackPanel();
            var paperScroll = new ScrollViewer
            {
                Tag = "inner",
                Content = stack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = (n.CardHeight.HasValue || floating) ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
            paper.Child = paperScroll;
            if (n.CardHeight.HasValue && !floating) paper.Height = n.CardHeight.Value;

            // Título
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBox
            {
                Text = n.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                FontFamily = DisplayFont,
                Foreground = B(FontColorHex),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 2, 4, 4),
                ToolTip = "Título (es el nombre del archivo al guardar)",
            };
            title.TextChanged += (s, e) => { n.Title = title.Text; Touch(n); };
            title.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; }
            };
            title.GotKeyboardFocus += (s, e) => { _activeNote = n; };
            Grid.SetColumn(title, 1);
            head.Children.Add(title);

            // También se puede agarrar la nota por cualquier parte que no sea texto
            Point? down = null;
            card.PreviewMouseLeftButtonDown += (s, e) => down = IsInteractive(e.OriginalSource as DependencyObject, card) ? null : e.GetPosition(card);
            card.PreviewMouseLeftButtonUp += (s, e) => down = null;
            card.PreviewMouseMove += (s, e) =>
            {
                if (floating || down == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(card);
                if (Math.Abs(p.X - down.Value.X) + Math.Abs(p.Y - down.Value.Y) < 6) return;
                down = null;
                StartCardDrag(n, card);
            };
            stack.Children.Add(head);

            // Barra inferior: guardar · color · (info) · quitar
            var footer = new Grid { Margin = new Thickness(-2, 4, -2, 0) };
            for (int fc = 0; fc < 3; fc++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(footer, 2);
            var saveBtn = IconButton("\uE74E", "Guardar", () => SafeRun(() => SaveNote(n, false)), 15, th.SubFg, th.Hover);
            Border colorBtn = null;
            colorBtn = IconButton("\uE790", "Cambiar el color de la nota", () => ShowColorMenu(n, paper, colorBtn), 15, th.SubFg, th.Hover);
            var floatBtn = IconButton(floating ? "\uE73F" : "\uE8A7",
                floating ? "Devolver la nota al panel" : "Sacar la nota del panel (ventana flotante)",
                () => { if (floating) DockNote(n); else FloatNote(n, null); }, 15, th.SubFg, th.Hover);
            saveBtn.Width = colorBtn.Width = floatBtn.Width = 30;
            saveBtn.Height = colorBtn.Height = floatBtn.Height = 28;
            colorBtn.HorizontalAlignment = HorizontalAlignment.Center;
            floatBtn.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(colorBtn, 1);
            Grid.SetColumn(floatBtn, 2);
            var status = new TextBlock
            {
                FontSize = 10.5,
                Foreground = B(th.SubFg),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 0, 0),
            };
            var leftBox = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            leftBox.Children.Add(saveBtn);
            leftBox.Children.Add(status);
            footer.Children.Add(leftBox);
            footer.Children.Add(colorBtn);
            footer.Children.Add(floatBtn);
            foreach (var ic in new[] { saveBtn, colorBtn, floatBtn, tabX })
            {
                var tb = (TextBlock)ic.Child;
                iconSetters.Add(br => tb.Foreground = br);
            }
            iconSetters.Add(br => status.Foreground = br);
            ApplyLook(ColorHex(n.Color));

            void UpdateFooter()
            {
                var date = n.Modified.ToString("d MMM, HH:mm", new System.Globalization.CultureInfo("es-ES"));
                var count = n.Kind == "lista" ? $"{n.Items.Count(i => i.Done)}/{n.Items.Count} · " : "";
                bool pending = n.IsDirty;
                dot.Visibility = pending ? Visibility.Visible : Visibility.Hidden;
                status.Text = n.Kind == "lista" ? count.TrimEnd(' ', '·') : "";
                status.ToolTip = null;
                band.ToolTip = (pending ? (n.FilePath == null ? "Sin guardar" : "Cambios pendientes por guardar") : "Guardado")
                    + "\n" + (n.FilePath ?? "Todavía no se ha guardado como .txt") + "\n" + date;
                saveBtn.ToolTip = n.FilePath == null
                    ? "Guardar en " + SaveDir
                    : "Guardar cambios en " + n.FilePath;
            }
            _footerUpdaters[n] = UpdateFooter;

            if (n.Kind == "lista") stack.Children.Add(BuildList(n));
            else
            {
                var body = BuildRichBody(n);
                var holder = new Grid();
                var ph = new TextBlock { Text = "Escribe aquí…", Foreground = B("#9A9A9A"), FontSize = _cfg.FontSize, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
                void UpdatePh() => ph.Visibility = string.IsNullOrEmpty(n.Text) ? Visibility.Visible : Visibility.Collapsed;
                body.TextChanged += (s, e) => UpdatePh();
                UpdatePh();
                holder.Children.Add(ph);
                holder.Children.Add(body);
                stack.Children.Add(holder);
            }

            UpdateFooter();
            frameGrid.Children.Add(footer);

            // Borde inferior para ajustar el alto de la tarjeta (doble clic = alto automático)
            frameGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heightGrip = new Border
            {
                Height = 9,
                Margin = new Thickness(0, 0, 0, -3),
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                ToolTip = "Arrastra para cambiar el alto · doble clic para alto automático",
                Child = new Border { Width = 34, Height = 3, CornerRadius = new CornerRadius(2), Background = B(th.SubFg), Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center },
            };
            iconSetters.Add(br => ((Border)heightGrip.Child).Background = br);
            ApplyLook(ColorHex(n.Color));
            Grid.SetRow(heightGrip, 3);
            if (floating) heightGrip.Visibility = Visibility.Collapsed;
            frameGrid.Children.Add(heightGrip);
            double startY = 0, startH = 0;
            bool resizingH = false;
            heightGrip.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    n.CardHeight = null;
                    _cache.Save(n);
                    Rebuild();
                    e.Handled = true;
                    return;
                }
                resizingH = true;
                startY = e.GetPosition(this).Y;
                startH = paper.ActualHeight;
                paper.MinHeight = 0;
                paperScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                heightGrip.CaptureMouse();
                e.Handled = true;
            };
            heightGrip.MouseMove += (s, e) =>
            {
                if (!resizingH) return;
                var h = Math.Max(40, startH + e.GetPosition(this).Y - startY);
                paper.Height = h;
                n.CardHeight = Math.Round(h);
            };
            heightGrip.MouseLeftButtonUp += (s, e) =>
            {
                if (!resizingH) return;
                resizingH = false;
                heightGrip.ReleaseMouseCapture();
                _cache.Save(n);
                e.Handled = true;
            };
            return card;
        }

        UIElement BuildList(Note n)
        {
            var panel = new StackPanel();
            var rows = new StackPanel();
            panel.Children.Add(rows);
            TextBox addBox = null;

            void Render(int focusIndex = -1)
            {
                rows.Children.Clear();
                for (int idx = 0; idx < n.Items.Count; idx++)
                {
                    var item = n.Items[idx];
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var cb = new CheckBox { IsChecked = item.Done, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 6, 0), Cursor = Cursors.Hand };
                    var tb = new TextBox
                    {
                        Text = item.Text,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = _cfg.FontSize, FontFamily = NoteFont,
                        Padding = new Thickness(0),
                        AcceptsReturn = false,
                    };
                    void Style()
                    {
                        tb.Foreground = item.Done ? B("#9A9A9A") : B(FontColorHex);
                        tb.TextDecorations = item.Done ? TextDecorations.Strikethrough : null;
                    }
                    Style();
                    cb.Checked += (s, e) => { item.Done = true; Style(); Touch(n); };
                    cb.Unchecked += (s, e) => { item.Done = false; Style(); Touch(n); };
                    tb.TextChanged += (s, e) => { item.Text = tb.Text; Touch(n); };
                    TrackEditor(tb, n);
                    int myIdx = idx;
                    tb.PreviewKeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            n.Items.Insert(myIdx + 1, new TodoItem());
                            Touch(n);
                            Render(myIdx + 1);
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Back && tb.Text.Length == 0)
                        {
                            n.Items.RemoveAt(myIdx);
                            Touch(n);
                            Render(myIdx - 1);
                            if (myIdx == 0) addBox?.Focus();
                            e.Handled = true;
                        }
                    };

                    var remove = IconButton("", "Quitar tarea", () =>
                    {
                        n.Items.Remove(item);
                        Touch(n);
                        Render();
                    }, 10);
                    remove.Width = 20; remove.Height = 20;
                    remove.Opacity = 0;
                    row.MouseEnter += (s, e) => remove.Opacity = 1;
                    row.MouseLeave += (s, e) => remove.Opacity = 0;

                    Grid.SetColumn(cb, 0); Grid.SetColumn(tb, 1); Grid.SetColumn(remove, 2);
                    row.Children.Add(cb); row.Children.Add(tb); row.Children.Add(remove);
                    rows.Children.Add(row);

                    if (idx == focusIndex)
                        tb.Loaded += (s, e) => { tb.Focus(); tb.CaretIndex = tb.Text.Length; };
                }
            }

            addBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = _cfg.FontSize, FontFamily = NoteFont,
                Padding = new Thickness(0),
                Foreground = B(FontColorHex),
            };
            addBox.GotKeyboardFocus += (s, e) => { _activeNote = n; };
            addBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && addBox.Text.Trim().Length > 0)
                {
                    n.Items.Add(new TodoItem { Text = addBox.Text.Trim() });
                    addBox.Text = "";
                    Touch(n);
                    Render();
                    e.Handled = true;
                }
            };
            var addRow = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            var plus = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 11, Foreground = B("#9A9A9A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            DockPanel.SetDock(plus, Dock.Left);
            addRow.Children.Add(plus);
            addRow.Children.Add(WithPlaceholder(addBox, "Añadir tarea y pulsar Enter"));
            panel.Children.Add(addRow);

            Render();
            return panel;
        }

        /// Marco (A) = color de la nota un poco más oscuro; borde del marco = más oscuro aún
        static Color FrameColor(string noteHex) => Mix(C(noteHex), Colors.Black, 0.08);
        static Color WithAlpha(Color c, double factor) => Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(factor, 0, 1)), c.R, c.G, c.B);
        static Color EdgeColor(string noteHex) => Mix(C(noteHex), Colors.Black, 0.22);

        static string ColorHex(string key) =>
            key != null && key.StartsWith("#") ? key : (Palette.FirstOrDefault(p => p.key == key).hex ?? "#FFFFFF");

        void ShowColorMenu(Note n, Border card, UIElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            foreach (var p in Palette)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 14, Height = 14, Background = B(p.hex), BorderBrush = B("#40000000"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = p.name });
                var mi = new MenuItem { Header = sp, IsCheckable = true, IsChecked = n.Color == p.key };
                var key = p.key; var hex = p.hex;
                mi.Click += (s, e) => { n.Color = key; card.Background = NoteBrush(hex); (card.Tag as Action<string>)?.Invoke(hex); _cache.Save(n); };
                menu.Items.Add(mi);
            }

            // Color personalizado
            menu.Items.Add(new Separator());
            bool isCustom = n.Color != null && n.Color.StartsWith("#");
            var csp = new StackPanel { Orientation = Orientation.Horizontal };
            csp.Children.Add(new Border
            {
                Width = 14, Height = 14, BorderBrush = B("#40000000"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0),
                Background = isCustom ? B(n.Color) : new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25), new GradientStop(Colors.Lime, 0.5),
                    new GradientStop(Colors.Cyan, 0.75), new GradientStop(Colors.Magenta, 1),
                }, 0),
            });
            csp.Children.Add(new TextBlock { Text = "Personalizado…" });
            var custom = new MenuItem { Header = csp, IsCheckable = true, IsChecked = isCustom };
            custom.Click += (s, e) =>
            {
                var before = n.Color;
                var picker = new ColorPickerWindow(ColorHex(n.Color), hex => { card.Background = NoteBrush(hex); (card.Tag as Action<string>)?.Invoke(hex); }) { Owner = this };
                if (picker.ShowDialog() == true)
                {
                    n.Color = picker.SelectedHex;
                    { card.Background = NoteBrush(n.Color); (card.Tag as Action<string>)?.Invoke(ColorHex(n.Color)); }
                    _cache.Save(n);
                }
                else { card.Background = NoteBrush(ColorHex(before)); (card.Tag as Action<string>)?.Invoke(ColorHex(before)); }
            };
            menu.Items.Add(custom);
            menu.IsOpen = true;
        }

        void ShowNoteMenu(Note n, UIElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            void Add(string text, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = text, IsEnabled = enabled };
                mi.Click += (s, e) => a();
                menu.Items.Add(mi);
            }
            Add("Guardar", () => SaveNote(n, false));
            Add("Guardar como…", () => SaveNote(n, true));
            Add("Volver a cargar desde el archivo", () => ReloadFromFile(n), n.FilePath != null);
            Add("Mostrar el archivo en la carpeta", () => RevealFile(n), n.FilePath != null);
            menu.Items.Add(new Separator());
            Add("Subir al principio", () => MoveToTop(n));
            Add("Quitar del panel", () => RemoveNote(n));
            menu.IsOpen = true;
        }

        // ================================================================ Acciones

        /// Cada cambio se guarda en la caché (borrador), nunca en el .txt.
        void Touch(Note n)
        {
            n.Modified = DateTime.Now;
            if (!_cacheTimers.TryGetValue(n, out var t))
            {
                t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t.Tick += (s, e) => { t.Stop(); _cache.Save(n); };
                _cacheTimers[n] = t;
            }
            t.Stop(); t.Start();
            if (_footerUpdaters.TryGetValue(n, out var u)) u();
        }

        void FlushCache()
        {
            foreach (var kv in _cacheTimers)
                if (kv.Value.IsEnabled) { kv.Value.Stop(); _cache.Save(kv.Key); }
        }

        void SaveNote(Note n, bool askAlways)
        {
            var path = n.FilePath;
            bool formatted = n.Kind != "lista" && !string.IsNullOrEmpty(n.Markdown) && n.Markdown != (n.Text ?? "");
            string ext;

            if (askAlways)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Guardar nota como",
                    Filter = "Markdown, conserva el formato (*.md)|*.md|Archivo de texto sin formato (*.txt)|*.txt",
                    FilterIndex = formatted || n.IsMarkdownFile ? 1 : 2,
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = SafeName(string.IsNullOrWhiteSpace(n.Title) ? "Nota" : n.Title),
                    InitialDirectory = Directory.Exists(n.FilePath != null ? Path.GetDirectoryName(n.FilePath) : SaveDir)
                        ? (n.FilePath != null ? Path.GetDirectoryName(n.FilePath) : SaveDir)
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
                ext = Path.GetExtension(path).ToLowerInvariant() == ".md" ? ".md" : ".txt";
            }
            else
            {
                if (n.IsMarkdownFile) ext = ".md";
                else if (formatted)
                {
                    // Igual que el Bloc de notas: ofrecer Markdown para conservar el formato
                    var choice = AskMarkdownOrText();
                    if (choice == null) return;
                    ext = choice;
                }
                else ext = ".txt";

                if (path == null)
                {
                    try { Directory.CreateDirectory(SaveDir); }
                    catch (Exception ex) { ShowInfo("No se pudo usar la carpeta de guardado:\n" + ex.Message); return; }
                    var baseName = SafeName(string.IsNullOrWhiteSpace(n.Title) ? "Nota" : n.Title);
                    path = Path.Combine(SaveDir, baseName + ext);
                    for (int k = 2; File.Exists(path); k++) path = Path.Combine(SaveDir, baseName + " (" + k + ")" + ext);
                }
                else if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(path);
                    var baseName = Path.GetFileNameWithoutExtension(path);
                    var np = Path.Combine(dir, baseName + ext);
                    for (int k = 2; File.Exists(np); k++) np = Path.Combine(dir, baseName + " (" + k + ")" + ext);
                    path = np;
                }
            }

            var content = n.Kind == "lista"
                ? string.Join("\r\n", n.Items.Select(i => (i.Done ? "[x] " : "[ ] ") + i.Text))
                : ext == ".md" ? (n.Markdown ?? n.Text ?? "") : (n.Text ?? "");
            try { TextFiles.Write(path, content); }
            catch (Exception ex)
            {
                ShowInfo("No se pudo guardar:\n" + ex.Message);
                return;
            }
            n.FilePath = path;
            n.SavedContent = content;
            _cfg.Save();
            _cache.Save(n);
            if (_footerUpdaters.TryGetValue(n, out var u)) u();
        }

        /// Devuelve ".md", ".txt" o null (cancelar).
        string AskMarkdownOrText()
        {
            string result = null;
            var w = new Window
            {
                Owner = this, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = UiFont,
            };
            var body = new StackPanel { Margin = new Thickness(28, 26, 28, 22), MaxWidth = 470 };
            body.Children.Add(new TextBlock { Text = "Guardar como Markdown para conservar el formato", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = B("#1F1F1F") });
            body.Children.Add(new TextBlock
            {
                Text = "Esta nota tiene formato (negrita, títulos, listas…). Para mantenerlo, guárdala como un archivo Markdown (.md).",
                FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = B("#333333"),
            });
            body.Children.Add(new TextBlock
            {
                Text = "Si la guardas como archivo de texto sin formato, se perderá todo el formato.",
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = B("#1F1F1F"),
            });
            body.Children.Add(new TextBlock
            {
                Text = "(Los colores y tipos de letra se quedan solo en el panel: Markdown no los guarda.)",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = B("#6B6B6B"),
            });

            Border Btn(string text, bool primary, Action click)
            {
                var b = new Border
                {
                    Child = new TextBlock { Text = text, FontSize = 13.5, Foreground = primary ? Brushes.White : B("#1F1F1F"), HorizontalAlignment = HorizontalAlignment.Center },
                    Background = primary ? B("#0F6CBD") : B("#FFFFFF"),
                    BorderBrush = primary ? B("#0F6CBD") : B("#26000000"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(16, 7, 16, 8), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, MinWidth = 120,
                };
                var normal = b.Background;
                b.MouseEnter += (s, e) => b.Background = primary ? B("#115EA3") : B("#F3F3F3");
                b.MouseLeave += (s, e) => b.Background = normal;
                b.MouseLeftButtonUp += (s, e) => click();
                return b;
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 16, 20, 18) };
            buttons.Children.Add(Btn("Guardar como .md", true, () => { result = ".md"; w.Close(); }));
            buttons.Children.Add(Btn("Guardar como .txt", false, () => { result = ".txt"; w.Close(); }));
            buttons.Children.Add(Btn("Cancelar", false, () => { result = null; w.Close(); }));

            var footer = new Border { Background = B("#F3F3F3"), BorderBrush = B("#14000000"), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 10, 10), Child = buttons };
            var stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(footer);
            w.Content = new Border
            {
                Margin = new Thickness(16), Background = Brushes.White, CornerRadius = new CornerRadius(10),
                BorderBrush = B("#1A000000"), BorderThickness = new Thickness(1), Child = stack,
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
            };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) { result = null; w.Close(); } else if (e.Key == Key.Enter) { result = ".md"; w.Close(); } };
            w.ShowDialog();
            return result;
        }

        void LoadMarkdownInto(Note n, string md)
        {
            var doc = new FlowDocument { FontSize = _cfg.FontSize };
            MarkdownIO.ToDoc(doc, md, _cfg.FontSize);
            n.RichXaml = DocToXaml(doc);
            n.Text = DocToPlain(doc);
            n.Markdown = MarkdownIO.FromDoc(doc, _cfg.FontSize);
        }

        static string SafeName(string title)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string(title.Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? "Nota" : s;
        }

        void OpenFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Abrir archivos .txt",
                Filter = "Notas (*.txt;*.md)|*.txt;*.md|Archivo de texto (*.txt)|*.txt|Markdown (*.md)|*.md|Todos los archivos (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = Directory.Exists(_cfg.LastDir) ? _cfg.LastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;

            foreach (var path in dlg.FileNames)
            {
                var existing = _notes.FirstOrDefault(x => x.FilePath != null && string.Equals(Path.GetFullPath(x.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    _notes.Remove(existing);
                    _notes.Add(existing);
                    continue;
                }
                string content;
                try { content = TextFiles.Read(path); }
                catch (Exception ex)
                {
                    ShowInfo("No se pudo abrir " + Path.GetFileName(path) + ":\n" + ex.Message);
                    continue;
                }
                var n = new Note
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    FilePath = path,
                    Kind = Note.LooksLikeList(content) ? "lista" : "nota",
                };
                if (n.IsMarkdownFile) { n.Kind = "nota"; LoadMarkdownInto(n, content); }
                else n.Parse(content);
                n.SavedContent = n.Kind == "lista" ? n.Serialize() : n.IsMarkdownFile ? n.Markdown : content;
                _cache.Save(n);
                _notes.Add(n);
            }
            if (dlg.FileNames.Length > 0) _cfg.LastDir = Path.GetDirectoryName(dlg.FileNames[0]);
            SaveOrder();
            _search.Text = "";
            Rebuild();
            _scroll.UpdateLayout();
            _scroll.ScrollToBottom();
        }

        void ReloadFromFile(Note n)
        {
            if (n.FilePath == null) return;
            if (n.IsDirty && !Confirm("¿Volver a cargar el archivo?", "Se perderán los cambios sin guardar de esta nota.", null, "Volver a cargar", true))
                return;
            try
            {
                var content = TextFiles.Read(n.FilePath);
                if (n.IsMarkdownFile) LoadMarkdownInto(n, content);
                else { n.Parse(content); n.RichXaml = null; n.Markdown = null; }
                n.SavedContent = n.Kind == "lista" ? n.Serialize() : n.IsMarkdownFile ? n.Markdown : content;
                n.Modified = DateTime.Now;
                _cache.Save(n);
                Rebuild();
            }
            catch (Exception ex)
            {
                ShowInfo("No se pudo leer el archivo:\n" + ex.Message);
            }
        }

        void RevealFile(Note n)
        {
            if (n.FilePath == null) return;
            try
            {
                if (File.Exists(n.FilePath)) Process.Start("explorer.exe", "/select,\"" + n.FilePath + "\"");
                else ShowInfo("El archivo ya no existe:\n" + n.FilePath);
            }
            catch { }
        }

        void MoveToTop(Note n)
        {
            _notes.Remove(n);
            _notes.Insert(0, n);
            SaveOrder();
            Rebuild();
            _scroll.ScrollToTop();
        }

        void NewNote(string kind)
        {
            var n = new Note { Kind = kind, Title = kind == "lista" ? "Lista nueva" : "Nota nueva" };
            _cache.Save(n);
            _notes.Add(n);
            SaveOrder();
            _search.Text = "";
            Rebuild();
            _scroll.UpdateLayout();
            _scroll.ScrollToBottom();
            if (_list.Children.Count > 0 && _list.Children[_list.Children.Count - 1] is Border card)
            {
                var title = FindFirst<TextBox>(card);
                if (title != null) { Activate(); title.Focus(); title.SelectAll(); }
            }
        }

        static bool IsInteractive(DependencyObject d, DependencyObject stop)
        {
            while (d != null && d != stop)
            {
                if (d is TextBoxBase || d is CheckBox || d is ButtonBase || d is ScrollBar) return true;
                if (d is FrameworkElement fe && (fe.Cursor == Cursors.Hand || fe.Cursor == Cursors.SizeNS)) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        static ScrollViewer FindInnerScroll(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ScrollViewer sv && (sv.Tag as string) == "inner") return sv;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        static T FindFirst<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T t) return t;
            if (root is Border b && b.Child != null) return FindFirst<T>(b.Child);
            if (root is ContentControl cc && cc.Content is DependencyObject inner) return FindFirst<T>(inner);
            if (root is Panel p) foreach (UIElement c in p.Children) { var r = FindFirst<T>(c); if (r != null) return r; }
            return null;
        }

        void RemoveNote(Note n)
        {
            if (n.IsDirty)
            {
                var name = string.IsNullOrWhiteSpace(n.Title) ? "esta nota" : "«" + n.Title.Trim() + "»";
                bool ok = n.FilePath == null
                    ? Confirm("¿Quitar " + name + " del panel?", "Esta nota todavía no se ha guardado como archivo, así que se perderá su contenido.", "Si quieres conservarla, pulsa Cancelar y guárdala primero.", "Quitar nota", true)
                    : Confirm("¿Quitar " + name + " del panel?", "Tiene cambios sin guardar que se perderán.", "El archivo " + Path.GetFileName(n.FilePath) + " no se borra: queda tal como se guardó la última vez.", "Quitar nota", true);
                if (!ok) return;
            }
            if (_cacheTimers.TryGetValue(n, out var t)) { t.Stop(); _cacheTimers.Remove(n); }
            CloseFloat(n);
            _cache.Remove(n);
            _notes.Remove(n);
            SaveOrder();
            Rebuild();
        }

        void ApplyFilter()
        {
            var q = (_search?.Text ?? "").Trim();
            foreach (var child in _list.Children)
            {
                if (child is Border card && card.Tag is Note n)
                {
                    bool match = q.Length == 0
                        || (n.Title ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || n.Serialize().Contains(q, StringComparison.CurrentCultureIgnoreCase);
                    card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        // ================================================================ Menú de opciones y bandeja

        void ShowOptionsMenu(UIElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = RailMenuPlacement };
            void Add(string text, Action a, bool? check = null)
            {
                var mi = new MenuItem { Header = text };
                if (check.HasValue) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
                mi.Click += (s, e) => a();
                menu.Items.Add(mi);
            }
            menu.Items.Add(new MenuItem { Header = "Carpeta de guardado:\n" + SaveDir, IsEnabled = false, Icon = new TextBlock { Text = "\uE74E", FontFamily = IconFont, FontSize = 14 } });
            Add("Cambiar carpeta de guardado…", ChangeSaveDir);
            Add("Abrir carpeta de guardado", () => { try { Directory.CreateDirectory(SaveDir); Process.Start(new ProcessStartInfo { FileName = SaveDir, UseShellExecute = true }); } catch { } });
            menu.Items.Add(new Separator());
            Add("Abrir archivos .txt…", OpenFiles);
            menu.Items.Add(new Separator());
            Add("Panel a la izquierda", () => SetSide("izquierda"), _cfg.Side == "izquierda");
            Add("Panel a la derecha", () => SetSide("derecha"), _cfg.Side != "izquierda");
            menu.Items.Add(new Separator());
            Add("Transparencia del panel y las notas…", () => ShowOpacityDialog());
            Add("Restablecer el ancho", () => SetWidth(360));
            menu.Items.Add(new Separator());
            Add("Iniciar con Windows", () => SetAutostart(!IsAutostart()), IsAutostart());
            Add("Ocultar panel", ToggleVisible);
            Add("Salir", ExitApp);
            menu.IsOpen = true;
        }

        string SaveDir => !string.IsNullOrWhiteSpace(_cfg.SaveDir)
            ? _cfg.SaveDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notas Escritorio");

        void ChangeSaveDir()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Elegir la carpeta donde se guardarán las notas",
                InitialDirectory = Directory.Exists(SaveDir) ? SaveDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            _cfg.SaveDir = dlg.FolderName;
            _cfg.Save();
            foreach (var u in _footerUpdaters.Values) u();
            ModernDialog("Carpeta de guardado cambiada", "Las notas nuevas se guardarán en:\n" + dlg.FolderName, "Las que ya estaban guardadas siguen en su archivo.", ("Entendido", "ok", 1));
        }

        // Letra de las notas: siempre la moderna de Windows 11 (las fuentes se cambian por fragmento)
        static readonly FontFamily NoteFontFixed = new("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
        FontFamily NoteFont => NoteFontFixed;
        string FontColorHex => "#000000"; // negro por defecto en todas las notas

        static readonly string[] FontChoices =
        {
            "Segoe UI", "Calibri", "Arial", "Verdana", "Trebuchet MS", "Georgia",
            "Times New Roman", "Cambria", "Consolas", "Cascadia Code", "Comic Sans MS", "Segoe Print", "Ink Free",
        };

        string SelFontName() => _activeRich?.Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f ? f.Source : null;
        double SelFontSize() => _activeRich?.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d ? d : -1;
        string SelColorHex() => _activeRich?.Selection.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : null;

        static readonly (string label, string hex)[] TextColors =
        {
            ("Negro (normal)", "#000000"), ("Gris", "#6B6B6B"), ("Azul", "#1F4E9E"), ("Celeste", "#0F7FB8"),
            ("Rojo", "#B3261E"), ("Naranjo", "#C25E00"), ("Verde", "#1E7A46"), ("Morado", "#6A3FB5"),
            ("Rosa", "#C2185B"), ("Café", "#6D4C33"),
        };

        void FillTextColorItems(ItemCollection items)
        {
            var sel = SelColorHex();
            bool known = false;
            foreach (var (label, hex) in TextColors)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = B(hex), BorderBrush = B("#40000000"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = label, Foreground = B(hex) });
                bool on = string.Equals(sel, hex, StringComparison.OrdinalIgnoreCase);
                known |= on;
                var mi = new MenuItem { Header = sp, IsCheckable = true, IsChecked = on };
                var h = hex;
                mi.Click += (s, e) => ApplyToSelection(TextElement.ForegroundProperty, B(h));
                items.Add(mi);
            }
            items.Add(new Separator());
            var custom = new MenuItem { Header = "Personalizado…", IsCheckable = true, IsChecked = !known && sel != null };
            custom.Click += (s, e) =>
            {
                if (!HasRich()) return;
                var rtb = _activeRich;
                var picker = new ColorPickerWindow(SelColorHex() ?? FontColorHex, null) { Owner = this };
                if (picker.ShowDialog() == true) { _activeRich = rtb; ApplyToSelection(TextElement.ForegroundProperty, B(picker.SelectedHex)); }
            };
            items.Add(custom);
        }

        void ShowTextColorMenu(UIElement anchor)
        {
            if (!HasRich()) return;
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = RailMenuPlacement };
            FillTextColorItems(menu.Items);
            menu.IsOpen = true;
        }

        void ShowFontMenu(UIElement anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = RailMenuPlacement };
            var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
            foreach (var f in FontChoices.Where(f => installed.Contains(f)))
            {
                var mi = new MenuItem
                {
                    Header = new TextBlock { Text = f, FontFamily = new FontFamily(f), FontSize = 14 },
                    IsCheckable = true,
                    IsChecked = string.Equals(SelFontName(), f, StringComparison.OrdinalIgnoreCase),
                };
                var name = f;
                mi.Click += (s, e) => ApplyToSelection(TextElement.FontFamilyProperty, new FontFamily(name));
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            foreach (var (label, size) in new[] { ("Tamaño pequeño", 12.0), ("Tamaño normal", 13.0), ("Tamaño grande", 15.0), ("Tamaño muy grande", 17.0) })
            {
                var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(SelFontSize() - size) < 0.1 };
                var sz = size;
                mi.Click += (s, e) => ApplyToSelection(TextElement.FontSizeProperty, sz);
                menu.Items.Add(mi);
            }

            // Color de la fuente
            menu.Items.Add(new Separator());
            var colorMenu = new MenuItem { Header = "Color del texto seleccionado" };
            FillTextColorItems(colorMenu.Items);
            menu.Items.Add(colorMenu);
            menu.IsOpen = true;
        }

        PlacementMode RailMenuPlacement => _cfg.Side != "izquierda" ? PlacementMode.Right : PlacementMode.Left;

        void SetSide(string side)
        {
            _cfg.Side = side;
            _cfg.Save();
            var q = _search?.Text ?? "";
            BuildUi();
            _search.Text = q;
            Reposition();
            Rebuild();
        }
        void SetWidth(double w)
        {
            _cfg.Width = w;
            if (!IsBarOnly && !IsFull) _cfg.ExpandedWidth = w;
            _cfg.Save();
            Reposition();
        }

        double NormalWidth => Math.Min(MaxPanelWidth() - 2, Math.Max(MinContentWidth, _cfg.ExpandedWidth > BarOnlyWidth + 1 ? _cfg.ExpandedWidth : 360));

        /// "−": deja solo la barra (si ya está así, vuelve al ancho normal)
        void ToggleBarOnly() => SetWidth(IsBarOnly ? NormalWidth : BarOnlyWidth);

        /// "▢": ocupa toda la pantalla (si ya está así, vuelve al ancho normal)
        void ToggleFull() => SetWidth(IsFull ? NormalWidth : MaxPanelWidth());

        void ToggleVisible()
        {
            if (IsVisible) { FlushCache(); Hide(); }
            else { Show(); AttachToDesktop(); Reposition(); }
        }

        void SetupTray()
        {
            System.Drawing.Icon icon = null;
            try
            {
                using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico");
                if (s != null) icon = new System.Drawing.Icon(s);
            }
            catch { }

            _tray = new WF.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "Notas Escritorio",
                Visible = true,
            };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Mostrar / ocultar notas", null, (s, e) => ToggleVisible());
            menu.Items.Add("Nueva nota", null, (s, e) => { if (!IsVisible) ToggleVisible(); NewNote("nota"); });
            menu.Items.Add("Nueva lista", null, (s, e) => { if (!IsVisible) ToggleVisible(); NewNote("lista"); });
            menu.Items.Add("Abrir archivos .txt…", null, (s, e) => { if (!IsVisible) ToggleVisible(); OpenFiles(); });
            menu.Items.Add(new WF.ToolStripSeparator());
            var auto = new WF.ToolStripMenuItem("Iniciar con Windows");
            auto.Click += (s, e) => SetAutostart(!IsAutostart());
            menu.Items.Add(auto);
            menu.Items.Add("Salir", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => auto.Checked = IsAutostart();
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleVisible(); };
        }

        bool IsAutostart()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        void SetAutostart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(RunName, "\"" + Environment.ProcessPath + "\" --inicio");
                else k.DeleteValue(RunName, false);
            }
            catch { }
        }

        /// Vuelve a dibujar el panel y las notas con los ajustes actuales
        /// Ejecuta una acción; si falla, muestra el motivo en vez de dejar la app en un estado raro
        void SafeRun(Action a)
        {
            try { a(); }
            catch (Exception ex) { ShowInfo("Ocurrió un problema:\n" + ex.Message); }
        }

        void RefreshLook()
        {
            BuildUi();
            Reposition();
            Rebuild();
            RefreshFloats();
        }

        void ShowOpacityDialog()
        {
            double p0 = _cfg.PanelOpacity, n0 = _cfg.NoteOpacity;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = UiFont, Owner = this,
            };
            var body = new StackPanel { Margin = new Thickness(26, 22, 26, 14), Width = 340 };
            body.Children.Add(new TextBlock { Text = "Transparencia", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = B("#1F1F1F") });
            body.Children.Add(new TextBlock { Text = "100 % es opaco y el texto se ve más nítido.", FontSize = 12, Foreground = B("#6B6B6B"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

            var pLbl = new TextBlock { FontSize = 13.5, Foreground = B("#333333"), Margin = new Thickness(0, 16, 0, 4) };
            var pSl = new Slider { Minimum = 0.3, Maximum = 1, Value = p0, IsSnapToTickEnabled = false };
            var nLbl = new TextBlock { FontSize = 13.5, Foreground = B("#333333"), Margin = new Thickness(0, 14, 0, 4) };
            var nSl = new Slider { Minimum = 0.3, Maximum = 1, Value = n0 };
            void Labels()
            {
                pLbl.Text = "Panel: " + (int)Math.Round(pSl.Value * 100) + " %";
                nLbl.Text = "Notas: " + (int)Math.Round(nSl.Value * 100) + " %";
            }
            Labels();
            void Preview()
            {
                // Si cambia el tipo de ventana hay que reiniciar: se hace al aceptar
                if (AllowsTransparency) SafeRun(ApplyOpacityLive);
            }
            pSl.ValueChanged += (s, e) => { _cfg.PanelOpacity = pSl.Value; Labels(); Preview(); };
            nSl.ValueChanged += (s, e) => { _cfg.NoteOpacity = nSl.Value; Labels(); Preview(); };
            body.Children.Add(pLbl); body.Children.Add(pSl);
            body.Children.Add(nLbl); body.Children.Add(nSl);

            Border Btn(string text, bool primary)
            {
                var b = new Border
                {
                    Child = new TextBlock { Text = text, FontSize = 13.5, Foreground = primary ? Brushes.White : B("#1F1F1F"), HorizontalAlignment = HorizontalAlignment.Center },
                    Background = primary ? B("#0F6CBD") : B("#FFFFFF"), BorderBrush = primary ? B("#0F6CBD") : B("#26000000"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 7, 16, 8), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, MinWidth = 100,
                };
                return b;
            }
            var ok = Btn("Aceptar", true);
            var cancel = Btn("Cancelar", false);
            void Accept()
            {
                _cfg.Save();
                w.Close();
                if (WantsLayered != AllowsTransparency) RestartApp(); else SafeRun(ApplyOpacityLive);
            }
            void Cancel()
            {
                _cfg.PanelOpacity = p0; _cfg.NoteOpacity = n0;
                w.Close();
                SafeRun(ApplyOpacityLive);
            }
            // Se usa "al presionar": la tarjeta del diálogo se arrastra y se tragaba el "al soltar"
            ok.MouseLeftButtonDown += (o, e) => { e.Handled = true; Accept(); };
            cancel.MouseLeftButtonDown += (o, e) => { e.Handled = true; Cancel(); };
            if (!AllowsTransparency)
                body.Children.Add(new TextBlock { Text = "El cambio se verá al pulsar Aceptar (la app se reinicia sola).", FontSize = 12, Foreground = B("#6B6B6B"), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            row.Children.Add(cancel); row.Children.Add(ok);
            body.Children.Add(row);

            var card = new Border
            {
                Margin = new Thickness(18), Background = Brushes.White, CornerRadius = new CornerRadius(12),
                BorderBrush = B("#1A000000"), BorderThickness = new Thickness(1), Child = body,
                Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
            };
            card.MouseLeftButtonDown += (o, e) => { try { w.DragMove(); } catch { } };
            w.Content = card;
            w.KeyDown += (o, e) => { if (e.Key == Key.Escape) Cancel(); else if (e.Key == Key.Enter) Accept(); };
            w.ShowDialog();
        }

        /// Reinicia la app (necesario para pasar de/a un tema transparente)
        void RestartApp()
        {
            FlushCache();
            SaveOrder();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            try { Program.InstanceMutex?.ReleaseMutex(); Program.InstanceMutex?.Dispose(); Program.InstanceMutex = null; } catch { }
            try { _showEvent?.Dispose(); _showEvent = null; } catch { }
            try
            {
                // Espera 2 s a que se cierre esta copia y luego abre la nueva (si no, la nueva ve el mutex ocupado y se cierra)
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + Environment.ProcessPath + "\"")
                    { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }

        void ExitApp()
        {
            FlushCache();
            SaveOrder();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }
    }

    // ================================================================ Selector de color personalizado

    public class ColorPickerWindow : Window
    {
        public string SelectedHex { get; private set; }
        double _h, _s, _v; // tono 0-360, saturación y brillo 0-1
        readonly Action<string> _preview;
        readonly System.Windows.Shapes.Rectangle _hueFill;
        readonly System.Windows.Shapes.Ellipse _svMarker, _hueMarker;
        readonly Border _swatch;
        readonly TextBox _hexBox;
        bool _updatingHex;
        const double SV = 220, HueW = 18, HueH = 220;

        public ColorPickerWindow(string initialHex, Action<string> preview)
        {
            _preview = preview;
            Title = "Color personalizado";
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            var c = (Color)ColorConverter.ConvertFromString(initialHex);
            RgbToHsv(c, out _h, out _s, out _v);

            // Barra de tono (vertical, rojo abajo → magenta arriba)
            var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
            for (int i = 0; i <= 6; i++) hueBrush.GradientStops.Add(new GradientStop(HsvToRgb(i * 60 % 360 == 0 && i == 6 ? 360 : i * 60, 1, 1), i / 6.0));
            var hueBar = new Border { Width = HueW, Height = HueH, CornerRadius = new CornerRadius(9), Background = hueBrush };
            _hueMarker = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Stroke = Brushes.Black, StrokeThickness = 1.5, IsHitTestVisible = false };
            var hueCanvas = new Canvas { Width = HueW + 4, Height = HueH, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 14, 0), Cursor = Cursors.Hand, ClipToBounds = false };
            Canvas.SetLeft(hueBar, 2);
            hueCanvas.Children.Add(hueBar);
            hueCanvas.Children.Add(_hueMarker);

            // Cuadro de saturación / brillo
            _hueFill = new System.Windows.Shapes.Rectangle { Width = SV, Height = SV };
            var white = new System.Windows.Shapes.Rectangle { Width = SV, Height = SV, Fill = new LinearGradientBrush(Colors.Transparent, Colors.White, 0) };
            var black = new System.Windows.Shapes.Rectangle { Width = SV, Height = SV, Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) };
            _svMarker = new System.Windows.Shapes.Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 } };
            var svCanvas = new Canvas { Width = SV, Height = SV, Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = false };
            svCanvas.Children.Add(_hueFill);
            svCanvas.Children.Add(white);
            svCanvas.Children.Add(black);
            svCanvas.Children.Add(_svMarker);
            var svBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1), Child = svCanvas };

            void HueAt(Point p) { _h = Math.Clamp(1 - p.Y / HueH, 0, 1) * 360; Update(); }
            void SvAt(Point p) { _s = Math.Clamp(1 - p.X / SV, 0, 1); _v = Math.Clamp(1 - p.Y / SV, 0, 1); Update(); }
            hueCanvas.MouseLeftButtonDown += (o, e) => { hueCanvas.CaptureMouse(); HueAt(e.GetPosition(hueCanvas)); };
            hueCanvas.MouseMove += (o, e) => { if (hueCanvas.IsMouseCaptured) HueAt(e.GetPosition(hueCanvas)); };
            hueCanvas.MouseLeftButtonUp += (o, e) => hueCanvas.ReleaseMouseCapture();
            svCanvas.MouseLeftButtonDown += (o, e) => { svCanvas.CaptureMouse(); SvAt(e.GetPosition(svCanvas)); };
            svCanvas.MouseMove += (o, e) => { if (svCanvas.IsMouseCaptured) SvAt(e.GetPosition(svCanvas)); };
            svCanvas.MouseLeftButtonUp += (o, e) => svCanvas.ReleaseMouseCapture();

            var pickers = new StackPanel { Orientation = Orientation.Horizontal };
            pickers.Children.Add(hueCanvas);
            pickers.Children.Add(svBorder);

            // Vista previa + código + botones
            _swatch = new Border { Width = 40, Height = 26, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0) };
            _hexBox = new TextBox { Width = 80, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2), FontFamily = new FontFamily("Consolas") };
            _hexBox.TextChanged += (o, e) =>
            {
                if (_updatingHex) return;
                var t = _hexBox.Text.Trim();
                if (!t.StartsWith("#")) t = "#" + t;
                if (Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$"))
                {
                    RgbToHsv((Color)ColorConverter.ConvertFromString(t), out _h, out _s, out _v);
                    Update(fromHex: true);
                }
            };
            var ok = new Button { Content = "Aceptar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            ok.Click += (o, e) => { SelectedHex = CurrentHex(); DialogResult = true; };
            var cancel = new Button { Content = "Cancelar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

            var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(ok); right.Children.Add(cancel);
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_swatch); left.Children.Add(_hexBox);
            bottom.Children.Add(left);

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(pickers);
            root.Children.Add(bottom);
            Content = root;
            Update();
        }

        string CurrentHex()
        {
            var c = HsvToRgb(_h, _s, _v);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        void Update(bool fromHex = false)
        {
            _hueFill.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            Canvas.SetLeft(_hueMarker, 1);
            Canvas.SetTop(_hueMarker, (1 - _h / 360) * HueH - 10);
            _hueMarker.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            Canvas.SetLeft(_svMarker, (1 - _s) * SV - 8);
            Canvas.SetTop(_svMarker, (1 - _v) * SV - 8);
            var hex = CurrentHex();
            _swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (!fromHex) { _updatingHex = true; _hexBox.Text = hex; _updatingHex = false; }
            _preview?.Invoke(hex);
        }

        static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        static void RgbToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            s = max == 0 ? 0 : d / max;
            v = max;
        }
    }
}
