using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// A rendered picture of a document, as SOLIDWORKS draws it.
///
/// Rendering needs the document in an active window, so the tool activates it, turns to each
/// requested standard view, zooms to fit, writes a bitmap, and then puts back what it changed:
/// the display mode, the view orientation, pan and zoom, and which document was active.
/// Several views come back as one contact sheet, labelled, so one picture shows the whole shape.
static class ViewTool
{
    public sealed class Picture
    {
        public Obj Facts;
        public string PngBase64;
    }

    static readonly (string name, int id)[] Views =
    {
        ("front", 1), ("back", 2), ("left", 3), ("right", 4), ("top", 5), ("bottom", 6),
        ("isometric", 7), ("trimetric", 8), ("dimetric", 9),
    };

    const int MaxViews = 6;

    public static Picture Render(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var type = Docs.Type(doc);
        var views = Json.List(a, "views").ConvertAll(v => v.Trim().ToLowerInvariant());
        if (views.Count == 0) views.Add((Json.Str(a, "view", "isometric") ?? "isometric").ToLowerInvariant());
        if (views.Count > MaxViews) throw new BridgeException("BAD_REQUEST", $"at most {MaxViews} views in one picture");
        foreach (var v in views)
            if (Array.Find(Views, x => x.name == v).id == 0 && !(v == "current" && views.Count == 1))
                throw new BridgeException("BAD_REQUEST", $"unknown view {v}");
        var displayName = Json.Str(a, "display");
        int? display = null;
        if (displayName != null)
            display = K.DisplayModes.TryGetValue(displayName, out var dm) ? dm
                : throw new BridgeException("BAD_REQUEST", $"display must be one of {string.Join(", ", K.DisplayModes.Keys)}");
        var width = Clamp(Json.Int(a, "width", 960));
        var height = Clamp(Json.Int(a, "height", 720));

        var previous = Get(app, "ActiveDoc");
        SessionTools.Activate(app, doc);
        var modelView = Get(doc, "ActiveView");
        object orientation = null, translation = null;
        double? scale = null;
        int? displayWas = null;
        if (modelView != null && type != K.DocDrawing)
        {
            orientation = Get(modelView, "Orientation3");
            translation = Get(modelView, "Translation3");
            scale = Convert.ToDouble(Get(modelView, "Scale2"));
            if (display.HasValue)
            {
                displayWas = Convert.ToInt32(Get(modelView, "DisplayMode"));
                Set(modelView, "DisplayMode", display.Value);
            }
        }

        try
        {
            string png;
            if (type == K.DocDrawing)
            {
                Call(doc, "ViewZoomtofit2");
                png = Png(Shot(doc, width, height));
            }
            else if (views.Count == 1)
            {
                if (views[0] != "current") Turn(doc, views[0]);
                png = Png(Shot(doc, width, height));
            }
            else png = Sheet(doc, views, width, height);
            return new Picture
            {
                PngBase64 = png,
                Facts = new Obj().Add("doc", Docs.Label(doc))
                    .Add("views", type == K.DocDrawing ? new List<string> { "sheet" } : views)
                    .Opt("display", type == K.DocDrawing ? null : displayName)
                    .Add("width", width).Add("height", height),
            };
        }
        finally
        {
            if (orientation != null)
            {
                try
                {
                    if (displayWas.HasValue) Set(modelView, "DisplayMode", displayWas.Value);
                    SetRef(modelView, "Orientation3", orientation);
                    SetRef(modelView, "Translation3", translation);
                    Set(modelView, "Scale2", scale.Value);
                    Call(doc, "GraphicsRedraw2");
                }
                catch (BridgeException) { /* best effort: the picture is still right */ }
            }
            if (previous != null && !ReferenceEquals(previous, doc))
            {
                try { SessionTools.Activate(app, previous); } catch (BridgeException) { /* closed meanwhile */ }
            }
        }
    }

    static void Turn(object doc, string view)
    {
        var id = Array.Find(Views, v => v.name == view).id;
        Call(doc, "ShowNamedView2", "*" + char.ToUpperInvariant(view[0]) + view.Substring(1), id);
        Call(doc, "ViewZoomtofit2");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    /// One picture of the document's window, fitted into width x height.
    ///
    /// SaveBMP keeps the window's framing and crops to the bitmap's shape (measured: a sheet
    /// zoomed to fit in a wide window lost its right side at 4:3). So the bitmap is taken at the
    /// window's own aspect ratio and letterboxed, and everything zoom-to-fit framed stays in.
    static Bitmap Shot(object doc, int width, int height)
    {
        var (w, h) = (width, height);
        var hwnd = Window(Get(doc, "ActiveView"));
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var r))
        {
            var cw = r.Right - r.Left;
            var ch = r.Bottom - r.Top;
            if (cw > 0 && ch > 0) (w, h) = Letterbox(width, height, (double)cw / ch);
        }
        var bmp = Path.Combine(Path.GetTempPath(), $"swbridge-{Guid.NewGuid():N}.bmp");
        try
        {
            Call(doc, "GraphicsRedraw2");
            if (!Bool(Call(doc, "SaveBMP", bmp, w, h)) || !File.Exists(bmp))
                throw new BridgeException("SAVE_FAILED", "SOLIDWORKS did not render the view; is its window minimized or hidden?");
            using var file = new Bitmap(bmp);
            var canvas = new Bitmap(width, height);
            using (var g = Graphics.FromImage(canvas))
            {
                var x = (width - w) / 2;
                var y = (height - h) / 2;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(file, x, y, w, h);
                // Continue the picture's edge pixels into the bands, so SOLIDWORKS' background
                // gradient runs on instead of ending in a seam. (Stretching a 1-pixel row with
                // DrawImage blends its edges, so the bands are painted pixel line by line.)
                for (var i = 0; i < width && y > 0; i++)
                {
                    var sx = Math.Min(file.Width - 1, Math.Max(0, (i - x) * file.Width / Math.Max(1, w)));
                    using (var top = new SolidBrush(file.GetPixel(sx, 0))) g.FillRectangle(top, i, 0, 1, y);
                    using (var bottom = new SolidBrush(file.GetPixel(sx, file.Height - 1))) g.FillRectangle(bottom, i, y + h, 1, height - y - h);
                }
                for (var j = 0; j < height && x > 0; j++)
                {
                    var sy = Math.Min(file.Height - 1, Math.Max(0, (j - y) * file.Height / Math.Max(1, h)));
                    using (var left = new SolidBrush(file.GetPixel(0, sy))) g.FillRectangle(left, 0, j, x, 1);
                    using (var right = new SolidBrush(file.GetPixel(file.Width - 1, sy))) g.FillRectangle(right, x + w, j, width - x - w, 1);
                }
            }
            return canvas;
        }
        finally
        {
            try { File.Delete(bmp); } catch (Exception) { /* temp file */ }
        }
    }

    /// The view's window handle. Window handles fit in 32 bits on 64-bit Windows, and
    /// GetViewHWndx64 failed late-bound on 2019 ("write-only property"), so the 32-bit one is used.
    static IntPtr Window(object view)
    {
        if (view == null) return IntPtr.Zero;
        try { return new IntPtr(Convert.ToInt64(Call(view, "GetViewHWnd")) & 0xFFFFFFFFL); }
        catch (BridgeException) { return IntPtr.Zero; } // render without letterboxing
    }

    /// The largest size with the window's aspect ratio that fits in width x height.
    public static (int w, int h) Letterbox(int width, int height, double aspect)
    {
        if (aspect >= (double)width / height) return (width, Math.Max(1, (int)Math.Round(width / aspect)));
        return (Math.Max(1, (int)Math.Round(height * aspect)), height);
    }

    /// Several views on one labelled grid: 2 or 3 in a row, 4 as 2 x 2, 5 or 6 as 3 x 2.
    static string Sheet(object doc, List<string> views, int width, int height)
    {
        var cols = views.Count <= 3 ? views.Count : views.Count == 4 ? 2 : 3;
        var rows = (views.Count + cols - 1) / cols;
        var cw = width / cols;
        var ch = height / rows;
        using var canvas = new Bitmap(width, height);
        using (var g = Graphics.FromImage(canvas))
        using (var font = new Font(FontFamily.GenericSansSerif, Math.Max(9, Math.Min(cw, ch) / 24f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var pen = new Pen(Color.FromArgb(160, 128, 128, 128)))
        {
            g.Clear(Color.White);
            for (var i = 0; i < views.Count; i++)
            {
                Turn(doc, views[i]);
                var x = i % cols * cw;
                var y = i / cols * ch;
                using (var shot = Shot(doc, cw, ch)) g.DrawImage(shot, x, y, cw, ch);
                g.DrawRectangle(pen, x, y, cw - 1, ch - 1);
                g.DrawString(views[i], font, Brushes.Black, x + 6, y + 4);
            }
        }
        return Png(canvas);
    }

    static string Png(Bitmap image)
    {
        using (image)
        using (var ms = new MemoryStream())
        {
            image.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
    }

    static int Clamp(int v) => v < 200 ? 200 : v > 2000 ? 2000 : v;
}
