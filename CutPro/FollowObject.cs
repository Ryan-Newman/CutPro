using System.Windows;
using System.Windows.Controls;

namespace CutPro;

/// <summary>A clip attached to a tracked object (<see cref="Editor.AttachToObject"/>): which object mask it follows, whether its size follows the
/// object's, and the offset kept from the object's centre. The motion itself is baked into the clip's Position (and Scale) keyframes — the
/// keyframe path preview and export already share — and "Update" bakes it again after the track changes.</summary>
public sealed class Follow
{
    public string MaskId { get; set; } = "";
    public bool Scale { get; set; }
    public double OffX { get; set; }                                         // logical px from the object's centre
    public double OffY { get; set; }
    public double Area0 { get; set; }                                        // the object's size when attached (scale reference)
    public double Scale0 { get; set; } = 100;
}

public partial class Editor
{
    /// <summary>Object masks the clip could follow: tracked masks on other video / picture clips that overlap it in time.</summary>
    public List<(Clip c, ObjectMask m)> Followable(Clip f) =>
        Clips.Where(c => c.Id != f.Id && c.Masks.Count > 0 && c.Start < f.End && c.End > f.Start)
             .SelectMany(c => c.Masks.Where(m => MaskTracks.ContainsKey(m.Id)).Select(m => (c, m)))
             .GroupBy(x => x.m.Id).Select(g => g.First()).ToList();

    /// <summary>The object's centre (logical px of the sequence frame) and area (px²) at timeline time t: from the clip carrying the mask that is
    /// on screen at t (cut pieces share it), or the nearest one in time (held at its ends). Null when there is no outline.</summary>
    public (double x, double y, double area)? ObjectAt(string maskId, double t)
    {
        var carriers = Clips.Where(c => c.Masks.Any(m => m.Id == maskId)).ToList();
        if (carriers.Count == 0 || !MaskTracks.TryGetValue(maskId, out var tr)) return null;
        var c = carriers.FirstOrDefault(x => t >= x.Start - 1e-9 && t < x.End - 1e-9)
                ?? carriers.OrderBy(x => t < x.Start ? x.Start - t : t - x.End).First();
        double tt = Math.Clamp(t, c.Start, Math.Max(c.Start, c.End - 1e-6));
        var polys = tr.At(SourceFrame(c, tt));
        if (polys == null || polys.Count == 0) return null;
        var (cx, cy) = MaskGeometry.Centroid(polys);
        if (c.FlipH) cx = 1 - cx;
        if (c.FlipV) cy = 1 - cy;
        double area = Math.Abs(polys.Sum(MaskGeometry.SignedArea));
        var (x, y, w, h, rot) = Placement(MediaOf(c), c, tt);
        double px = x + cx * w, py = y + cy * h;
        if (Math.Abs(rot) > 1e-9)
        {   // about the clip's centre
            double a = rot * Math.PI / 180, ox = x + w / 2, oy = y + h / 2, dx = px - ox, dy = py - oy;
            px = ox + dx * Math.Cos(a) - dy * Math.Sin(a); py = oy + dx * Math.Sin(a) + dy * Math.Cos(a);
        }
        return (px, py, area * w * h);
    }

    /// <summary>Attaches clip f to an object: its Position (and with <paramref name="scale"/> its Scale) becomes keyframes that follow the object's
    /// centre (and size) frame by frame from f's start to its end, keeping the offset it has from the object at <paramref name="refT"/>. Keys
    /// that a straight line between their neighbours already gives are left out. One undo step. False when the object has no outline.</summary>
    public bool AttachToObject(Clip f, string maskId, bool scale, double refT)
    {
        refT = Math.Clamp(refT, f.Start, Math.Max(f.Start, f.End - OneFrame));
        if (ObjectAt(maskId, refT) is not { } o0) return false;
        bool ok = false;
        Edit(() =>
        {
            double cx0 = W / 2.0 + Anim.Value(f, AnimProp.X, refT) * W / 100, cy0 = H / 2.0 + Anim.Value(f, AnimProp.Y, refT) * H / 100;
            var fo = new Follow { MaskId = maskId, Scale = scale, OffX = cx0 - o0.x, OffY = cy0 - o0.y, Area0 = Math.Max(1e-6, o0.area), Scale0 = Anim.Value(f, AnimProp.Scale, refT) };
            f.Follow = fo;
            ok = Bake(f);
        }, "Attach to Object");
        return ok;
    }

    /// <summary>Bakes a clip's <see cref="Clip.Follow"/> into its keys again (after the object's track or the clip's length changed).</summary>
    public bool UpdateFollow(Clip f)
    {
        if (f.Follow == null) return false;
        bool ok = false;
        Edit(() => ok = Bake(f), "Update Object Follow");
        return ok;
    }

    public void DetachFromObject(Clip f)
    {
        if (f.Follow == null) return;
        Edit(() =>
        {
            foreach (var p in f.Follow.Scale ? new[] { AnimProp.X, AnimProp.Y, AnimProp.Scale } : new[] { AnimProp.X, AnimProp.Y }) f.Anim?.Remove(p);
            Anim.Prune(f);
            f.Follow = null;
        }, "Detach from Object");
    }

    bool Bake(Clip f)
    {
        var fo = f.Follow!;
        int n = Math.Max(1, (int)Math.Round(f.Dur * TimeUtil.Fps));
        var xs = new List<(double t, double v)>(); var ys = new List<(double t, double v)>(); var ss = new List<(double t, double v)>();
        for (int k = 0; k <= n; k++)
        {
            double t = Math.Min(f.Start + k / TimeUtil.Fps, f.End - 1e-6);
            if (ObjectAt(fo.MaskId, t) is not { } o) continue;
            double src = Anim.SrcTime(f, t);
            xs.Add((src, (o.x + fo.OffX - W / 2.0) / W * 100));
            ys.Add((src, (o.y + fo.OffY - H / 2.0) / H * 100));
            if (fo.Scale) ss.Add((src, fo.Scale0 * Math.Sqrt(Math.Max(0, o.area) / fo.Area0)));
        }
        if (xs.Count == 0) return false;
        void Keys(AnimProp p, List<(double t, double v)> pts, double tol)
        {
            f.Anim?.Remove(p);
            foreach (var (t, v) in Simplify(pts, tol)) { var key = Anim.SetKey(f, p, t, v); key.In = key.Out = KeyType.Linear; }
        }
        Keys(AnimProp.X, xs, 0.02); Keys(AnimProp.Y, ys, 0.02 * W / H);
        if (fo.Scale) Keys(AnimProp.Scale, ss, 0.05);
        return true;
    }

    /// <summary>Douglas–Peucker on a sampled curve: the points a straight line between the kept ones misses by more than tol.</summary>
    static List<(double t, double v)> Simplify(List<(double t, double v)> p, double tol)
    {
        if (p.Count <= 2) return p.ToList();
        var keep = new bool[p.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int a, int b)>(); stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            int best = -1; double far = tol;
            for (int i = a + 1; i < b; i++)
            {
                double u = (p[i].t - p[a].t) / Math.Max(1e-12, p[b].t - p[a].t), line = p[a].v + (p[b].v - p[a].v) * u, d = Math.Abs(p[i].v - line);
                if (d > far) { far = d; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true; stack.Push((a, best)); stack.Push((best, b));
        }
        return p.Where((_, i) => keep[i]).ToList();
    }
}

public partial class MainWindow
{
    /// <summary>Effect Controls ▸ Motion: attach the clip to a tracked object (pick the object), update after re-tracking, detach.</summary>
    void BuildFollowRow(Clip c)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        if (c.Follow is { } fo)
        {
            var name = ed.Clips.SelectMany(x => x.Masks).FirstOrDefault(m => m.Id == fo.MaskId)?.Name ?? "a deleted object";
            row.Children.Add(new TextBlock { Text = $"Follows “{name}”" + (fo.Scale ? " (size too)" : ""), Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var up = new Button { Content = "Update", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Follow the object's track again (after tracking it again)" };
            up.Click += (_, _) => { if (!ed.UpdateFollow(c)) SetTask("The object has no outline any more", 2500); BuildInspector(); };
            var off = new Button { Content = "Detach", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Remove the position keys it got from the object" };
            off.Click += (_, _) => { ed.DetachFromObject(c); BuildInspector(); };
            row.Children.Add(up); row.Children.Add(off);
        }
        else
        {
            var att = new Button { Content = "Attach to object…", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Make this clip follow a tracked object (an object mask on another clip): its position becomes keyframes" };
            att.Click += (_, _) => AttachMenu(c, att);
            row.Children.Add(att);
        }
        Inspector.Children.Add(row);
    }

    void AttachMenu(Clip c, FrameworkElement target)
    {
        var list = ed.Followable(c);
        var cm = new ContextMenu { PlacementTarget = target };
        if (list.Count == 0)
            cm.Items.Add(new MenuItem { Header = "No tracked objects under this clip — select an object on a video clip (Effect Controls ▸ Object masks) and track it first", IsEnabled = false });
        foreach (var (oc, m) in list)
            foreach (bool scale in new[] { false, true })
            {
                var mi = new MenuItem { Header = $"{m.Name} on {ed.MediaOf(oc)?.Name ?? "a clip"}" + (scale ? " — position and size" : "") };
                mi.Click += (_, _) =>
                {
                    if (ed.AttachToObject(c, m.Id, scale, ed.Time)) SetTask($"Attached to “{m.Name}” — it follows the object; Update after re-tracking", 3500);
                    else Alert.Show(this, "The object has no outline at this clip's time. Track it first.", "Attach to object");
                    BuildInspector();
                };
                cm.Items.Add(mi);
            }
        cm.IsOpen = true;
        lastMenu = cm;
    }
}
