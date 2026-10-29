using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CutPro;

/// <summary>
/// Motion graphics through the real window: --graphicsui out.txt (and the .graphics / .shapes --uishot shots). The Titles view's Graphics gallery
/// (a click adds the template at the playhead on V2, selected, Effect Controls ▸ Shapes and stickers lists its layers), the program monitor
/// shows it, a sticker added through the section's menu (one undo step), a layer moved with its X slider, Save as Graphic Template… (auto-
/// answered: listed as “(mine)”), a graphic from the command palette, and UI frame gaps while animated graphics play.
/// </summary>
public partial class MainWindow
{
    public async void GraphicsScenario(string outFile)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.Add($"{(ok ? "PASS" : "FAIL")} {what}"); }
        Alert.Auto = (title, msg, b) => { log.Add($"  ALERT {title}: {msg.Replace('\n', ' ')}"); return 0; };
        async Task Settle(int ms = 300) { UpdateLayout(); await Task.Delay(ms); UpdateLayout(); }
        try
        {
            var d = Path.Combine(Path.GetTempPath(), "cutpro_test");
            await ImportAsync(new[] { Path.Combine(d, "a.mp4"), Path.Combine(d, "b.mp4") });
            ed.AddMedia(ed.Media[0], null, 0); ed.AddMedia(ed.Media[1], null, ed.End);
            ed.Seek(1.0); ShowSideView("Titles"); await Settle(1200);

            // ---------- the gallery ----------
            var cards = StyleGallery.Items.OfType<FrameworkElement>().Where(x => x.Tag is GraphicTemplate).ToList();
            Check(cards.Count == GraphicLibrary.All.Count() && cards.All(x => x is StackPanel { Children.Count: 2 } sp && sp.Children[0] is Border { Child: Image { Source: not null } }),
                  $"the Titles view lists every graphic template with a picture ({cards.Count})");
            var sub = cards.First(x => ((GraphicTemplate)x.Tag).Name == "Subscribe button");
            StyleGallery.SelectedItem = sub; await Settle(600);
            var g = ed.Selected;
            Check(g is { Kind: ClipKind.Text, Shapes.Count: 2 } && g.Track == Tracks.V(2) && Math.Abs(g.Start - 1.0) < 1e-6 && g.Text == "SUBSCRIBE" && ed.UndoLabels.First() == "Add Graphic" && StyleGallery.SelectedIndex == -1,
                  "a click on “Subscribe button”: a new graphic at the playhead on V2, selected (one undo step)");
            var heads = FindVisualChildren<TextBlock>(Inspector).Select(t => t.Text).ToList();
            var layerList = FindVisualChildren<ListBox>(Inspector).FirstOrDefault(l => l.Items.Count == 2 && l.Items[0] is string s0 && s0.StartsWith("Rectangle"));
            Check(heads.Contains("SHAPES AND STICKERS") && layerList != null && (string)layerList.Items[1] == "Sticker: Bell  · over the text",
                  "Effect Controls ▸ Shapes and stickers lists its layers (Rectangle, Sticker: Bell over the text)");

            // ---------- the program monitor shows it ----------
            ed.Seek(g!.Start + 1.2); await Settle(500);
            int red = RedPixels(Pv);
            Check(red > 3000, $"the program monitor shows the red button ({red} red pixels)");

            // ---------- a sticker through the section's menu, undo ----------
            var stickBtn = FindVisualChildren<Button>(Inspector).First(b => b.Content as string == "☺ Sticker ▾");
            stickBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle(400);
            var heart = (stickBtn.ContextMenu ?? lastStickerMenu)?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Heart");
            heart ??= FindOpenMenuItem("Heart");
            heart?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle(700);
            Check(heart != null && g.Shapes!.Count == 3 && g.Shapes[2] is { Kind: ShapeKind.Sticker, Sticker: "Heart" }, "☺ Sticker ▸ Heart adds a heart layer (selected in the list)");
            ed.Undo(); await Settle(400);
            g = ed.Clips.First(c => c.Id == g.Id);
            Check(g.Shapes!.Count == 2, "undo: the sticker goes");

            // ---------- a layer's X slider moves it ----------
            ed.Select(g); shapeSel = 0; BuildInspector(); await Settle(300);
            double x0 = g.Shapes[0].X;
            var shapeSec = FindVisualChildren<Expander>(Inspector).FirstOrDefault(x => x.Header is TextBlock { Text: "SHAPES AND STICKERS" });
            var xSlider = shapeSec?.Content is Panel sp2 ? sp2.Children.OfType<Grid>().Where(gr => gr.Children.Count == 3 && gr.Children[0] is TextBlock { Text: "X" } && gr.Children[1] is Slider).Select(gr => (Slider)gr.Children[1]).FirstOrDefault() : null;
            if (xSlider != null) xSlider.Value = x0 + 40;
            await Settle(700);
            Check(xSlider != null && Math.Abs(ed.Clips.First(c => c.Id == g.Id).Shapes![0].X - (x0 + 40)) < 1e-6, "the layer's X slider moves it 40 px");

            // ---------- the monitor's handles: drag the layer to move it, a corner to resize it ----------
            ed.Seek(g.Start + 1.2); shapeSel = 0; BuildInspector(); await Settle(400);
            var gc = ed.Clips.First(c => c.Id == g.Id); var lay = gc.Shapes![0];
            var mx = shapeOverlay!.ToScreen(gc, lay); var box = TitleVisual.LayerBox(lay);
            var centre = mx.Transform(new Point(box.X + box.Width / 2, box.Y + box.Height / 2));
            bool hits = shapeOverlay.Hit(centre) == 4 && shapeOverlay.Hit(mx.Transform(box.BottomRight)) == 2 && shapeOverlay.Hit(new Point(3, 3)) == -1;
            double xb = lay.X, yb = lay.Y; int nU = ed.UndoLabels.Count();
            shapeOverlay.Begin(centre); shapeOverlay.MoveTo(centre + new Vector(30, -12), false); shapeOverlay.MoveTo(centre + new Vector(60, -24), false); shapeOverlay.End();
            await Settle(300);
            var moved = ed.Clips.First(c => c.Id == g.Id).Shapes![0];
            Check(hits && moved.X == xb + 60 && moved.Y == yb - 24 && ed.UndoLabels.Count() == nU + 1 && ed.UndoLabels.First() == "Move Shape Layer",
                  $"on the monitor: the layer's box takes the mouse (inside / corner / not outside); a drag moves it 60, −24 px in one undo step");
            gc = ed.Clips.First(c => c.Id == g.Id); lay = gc.Shapes![0];
            mx = shapeOverlay.ToScreen(gc, lay); box = TitleVisual.LayerBox(lay);
            double w0 = lay.W, h0 = lay.H;
            shapeOverlay.Begin(mx.Transform(box.BottomRight)); shapeOverlay.MoveTo(mx.Transform(new Point(box.Right * 1.5, box.Bottom * 1.5)), false); shapeOverlay.End();
            await Settle(300);
            var grown = ed.Clips.First(c => c.Id == g.Id).Shapes![0];
            Check(Math.Abs(grown.W - Math.Round(w0 * 1.5)) <= 1 && Math.Abs(grown.H - Math.Round(h0 * 1.5)) <= 1 && grown.X == moved.X,
                  $"dragging a corner resizes it about its centre ({w0:0}×{h0:0} → {grown.W:0}×{grown.H:0})");
            ed.Undo(); ed.Undo(); await Settle(200);
            Check(ed.Clips.First(c => c.Id == g.Id).Shapes![0] is { } back0 && back0.X == xb && back0.W == w0, "two undos: back where it was, its size too");

            // ---------- save as a template, the palette ----------
            SaveGraphicTemplate(ed.Clips.First(c => c.Id == g.Id)); await Settle(600);
            Check(GraphicLibrary.User.Any(t => t.Name == "SUBSCRIBE") && StyleGallery.Items.OfType<StackPanel>().Any(sp => sp.Children.OfType<TextBlock>().Any(t => t.Text == "SUBSCRIBE  (mine)")),
                  "Save as Graphic Template… (the name asked, auto-answered): the gallery lists it as “(mine)”");
            GraphicLibrary.Delete("SUBSCRIBE");
            ed.Seek(8);
            var cmd = Commands().FirstOrDefault(c => c.Name == "Graphic: Heart pop");
            cmd?.Run(); await Settle(500);
            Check(cmd != null && ed.Selected is { Shapes: [{ Sticker: "Heart" }] }, "the command palette's “Graphic: Heart pop” adds one");

            // ---------- frame gaps while animated graphics play ----------
            ed.Seek(0.5); ed.Time = 0.5;
            var like = GraphicLibrary.BuiltIn.First(t => t.Name == "Like · comment · share");
            ed.AddGraphic(like); ed.Seek(0.5); await Settle(800);
            var gaps = new List<double>(); long last = 0; bool on = false;
            void Tick(object? s, EventArgs e) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); if (on && last != 0) gaps.Add((now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); last = now; }
            CompositionTarget.Rendering += Tick;
            Play();
            for (int i = 0; i < 150 && !clockRunning; i++) await Task.Delay(20);
            on = true; await Task.Delay(2500); on = false;
            CompositionTarget.Rendering -= Tick; Pause();
            gaps.Sort();
            double p95 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.95)] : 999, worst = gaps.Count > 0 ? gaps[^1] : 999;
            Check(gaps.Count > 60 && p95 < 25 && worst < 120, $"playing over two animated graphics: UI frame gaps 95 % ≤ {p95:0.0} ms, worst {worst:0.0} ms");
        }
        catch (Exception ex) { Check(false, "exception: " + ex); }
        log.Add(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllLines(outFile, log);
        Application.Current.Shutdown(fails == 0 ? 0 : 1);
    }

    /// <summary>Pixels of the program monitor that are the subscribe button's red.</summary>
    static int RedPixels(FrameworkElement el)
    {
        int w = (int)el.ActualWidth, h = (int)el.ActualHeight;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rtb.Render(el);
        var px = new byte[w * h * 4]; rtb.CopyPixels(px, w * 4, 0);
        int n = 0; for (int i = 0; i < px.Length; i += 4) if (px[i + 2] > 200 && px[i + 1] < 80 && px[i] < 80) n++;
        return n;
    }
    /// <summary>A menu item of an open popup menu (the sticker menu) by its header.</summary>
    static MenuItem? FindOpenMenuItem(string header) =>
        PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Select(s => s.RootVisual).Where(v => v != null)
            .SelectMany(v => FindVisualChildren<MenuItem>(v!)).FirstOrDefault(i => i.Header as string == header);

    /// <summary>--uishot .graphics (the Titles view's Graphics gallery) and .shapes (a graphic on the monitor, Effect Controls ▸ Shapes and stickers).</summary>
    async Task GraphicsShots(string path, Action<string> shot)
    {
        var keepT = ed.Time;
        ShowSideView("Titles"); await Task.Delay(700);
        shot(Path.ChangeExtension(path, ".graphics.png"));
        ed.Seek(0.4);
        var c = ed.AddGraphic(GraphicLibrary.BuiltIn.First(t => t.Name == "Lower third — bar"));
        if (c != null)
        {
            c.Text = "Ryan Example"; c.Shapes![2].Text = "Video editor"; shapeSel = 0; shapeClip = c.Id;
            sectionOpen["Shapes and stickers"] = true; sectionOpen["Text"] = false; sectionOpen["Font"] = false; sectionOpen["Fill"] = false;
            ShowDock("Inspector"); ed.Select(c); BuildInspector();
            ed.Seek(c.Start + 1.5); await Task.Delay(900);
            FindVisualChildren<TextBlock>(Inspector).FirstOrDefault(t => t.Text == "SHAPES AND STICKERS")?.BringIntoView(new Rect(0, 0, 10, 500));
            await Task.Delay(500);
            shot(Path.ChangeExtension(path, ".shapes.png"));
            sectionOpen["Text"] = true; sectionOpen["Font"] = true; sectionOpen["Fill"] = true;
            ed.Undo();
        }
        ShowSideView("Project"); ed.Seek(keepT);
    }
}
