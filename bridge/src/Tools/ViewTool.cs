using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// A rendered picture of a document, as SOLIDWORKS draws it.
///
/// Rendering needs the document in an active window, so the tool activates it, turns to the
/// requested standard view, zooms to fit, writes a bitmap, and then puts back what it changed:
/// the view orientation, pan and zoom, and which document was active.
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

    public static Picture Render(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var type = Docs.Type(doc);
        var view = (Json.Str(a, "view", "isometric") ?? "isometric").ToLowerInvariant();
        var width = Clamp(Json.Int(a, "width", 960));
        var height = Clamp(Json.Int(a, "height", 720));

        var previous = Get(app, "ActiveDoc");
        SessionTools.Activate(app, doc);
        var modelView = Get(doc, "ActiveView");
        object orientation = null, translation = null;
        double? scale = null;
        if (modelView != null && type != K.DocDrawing)
        {
            orientation = Get(modelView, "Orientation3");
            translation = Get(modelView, "Translation3");
            scale = Convert.ToDouble(Get(modelView, "Scale2"));
        }

        var bmp = Path.Combine(Path.GetTempPath(), $"swbridge-{Guid.NewGuid():N}.bmp");
        try
        {
            if (type != K.DocDrawing && view != "current")
            {
                var id = Array.Find(Views, v => v.name == view).id;
                if (id == 0) throw new BridgeException("BAD_REQUEST", $"unknown view {view}");
                Call(doc, "ShowNamedView2", "*" + char.ToUpperInvariant(view[0]) + view.Substring(1), id);
                Call(doc, "ViewZoomtofit2");
            }
            else if (type == K.DocDrawing)
            {
                Call(doc, "ViewZoomtofit2");
            }
            Call(doc, "GraphicsRedraw2");
            if (!Bool(Call(doc, "SaveBMP", bmp, width, height)) || !File.Exists(bmp))
                throw new BridgeException("SAVE_FAILED", "SOLIDWORKS did not render the view; is its window minimized or hidden?");

            string png;
            using (var image = new Bitmap(bmp))
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                png = Convert.ToBase64String(ms.ToArray());
            }
            return new Picture
            {
                PngBase64 = png,
                Facts = new Obj().Add("doc", Docs.Label(doc)).Add("view", type == K.DocDrawing ? "sheet" : view)
                    .Add("width", width).Add("height", height),
            };
        }
        finally
        {
            try { File.Delete(bmp); } catch (Exception) { /* temp file */ }
            if (orientation != null)
            {
                try
                {
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

    static int Clamp(int v) => v < 200 ? 200 : v > 2000 ? 2000 : v;
}
