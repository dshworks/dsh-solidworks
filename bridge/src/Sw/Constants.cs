using System.Collections.Generic;

namespace SwBridge.Sw;

/// The SOLIDWORKS enum values the bridge uses, copied from SolidWorks.Interop.swconst.dll of
/// SOLIDWORKS 2016 SP1 (24.1.0.45), the oldest release we support. Values are stable across
/// releases; new releases only add members.
static class K
{
    // swDocumentTypes_e
    public const int DocPart = 1, DocAssembly = 2, DocDrawing = 3;

    // swOpenDocOptions_e
    public const int OpenSilent = 1, OpenReadOnly = 2;

    // swSaveAsOptions_e
    public const int SaveSilent = 1, SaveCopy = 2;

    // swSaveAsVersion_e
    public const int SaveAsCurrentVersion = 0;

    // swInConfigurationOpts_e
    public const int ThisConfiguration = 1;

    // swSetValueInConfiguration_e
    public const int SetInThisConfiguration = 1, SetInAllConfigurations = 2;

    // swDimensionParamType_e
    public const int DimLinear = 0, DimAngular = 1, DimInteger = 2;

    // swDimensionDrivenState_e
    public const int DimDriven = 1;

    // swCustomInfoType_e, swCustomPropertyAddOption_e
    public const int CustomInfoText = 30, CustomPropertyReplaceValue = 2;

    // swRebuildOnActivation_e
    public const int DontRebuildActiveDoc = 1;

    // swExportDataFileType_e, swExportDataSheetsToExport_e
    public const int ExportPdfData = 1, ExportAllSheets = 1, ExportCurrentSheet = 2;

    // swFeatureSuppressionAction_e
    public const int Suppress = 0, Unsuppress = 1;

    // swComponentSuppressionState_e
    public static readonly Dictionary<int, string> ComponentState = new Dictionary<int, string>
    {
        [0] = "suppressed", [1] = "lightweight", [2] = "resolved", [3] = "resolved", [4] = "lightweight",
    };

    // swLengthUnit_e
    public static readonly string[] LengthUnits =
        { "mm", "cm", "m", "in", "ft", "ft-in", "angstrom", "nm", "micron", "mil", "uin" };

    public static string DocType(int t) => t switch
    {
        DocPart => "part", DocAssembly => "assembly", DocDrawing => "drawing", _ => "other",
    };

    // swFileLoadError_e. 2016 still lists the load warnings in this enum; the bridge reports
    // only the error bits, and warnings come from the separate warnings argument.
    public static readonly Dictionary<int, string> LoadErrors = new Dictionary<int, string>
    {
        [1] = "generic error", [2] = "file not found", [1024] = "invalid file type",
        [8192] = "saved by a newer SOLIDWORKS", [65536] = "a file with the same name is already open",
        [131072] = "protected document", [262144] = "low resources", [524288] = "no display data",
    };

    // swFileLoadWarning_e
    public static readonly Dictionary<int, string> LoadWarnings = new Dictionary<int, string>
    {
        [1] = "id mismatch", [2] = "opened read-only", [4] = "file is in use by another user",
        [8] = "drawing ANSI update", [16] = "sheet scale update", [32] = "needs rebuild",
        [64] = "base part not loaded", [128] = "already open", [256] = "drawing opened as detailing only",
        [512] = "view-only restrictions", [1024] = "a view references a missing configuration",
        [2048] = "drawing surface-finish symbols converted", [4096] = "revolve dimension tolerance",
        [8192] = "model out of date", [16384] = "dimensions referenced incorrectly to models",
        [32768] = "component references a missing configuration",
        [65536] = "linked design table failed to update", [131072] = "design table missing",
    };

    // swFileSaveError_e
    public static readonly Dictionary<int, string> SaveErrors = new Dictionary<int, string>
    {
        [1] = "generic error", [2] = "document is read-only", [4] = "file name empty",
        [8] = "file name contains @", [16] = "file is locked", [32] = "format not available for this document",
        [64] = "document has rebuild errors", [128] = "will not overwrite", [256] = "invalid file extension",
        [512] = "nothing selected", [1024] = "bad eDrawings version", [2048] = "path too long",
        [4096] = "save as not supported", [8192] = "references must be saved first",
    };

    // swFileSaveWarning_e
    public static readonly Dictionary<int, string> SaveWarnings = new Dictionary<int, string>
    {
        [1] = "rebuild error", [2] = "needs rebuild", [4] = "views need update",
        [8] = "animator needs to solve", [16] = "animator feature edits", [32] = "eDrawings bad selection",
        [64] = "animator light edits", [128] = "animator camera views", [256] = "animator section views",
        [512] = "missing OLE objects", [1024] = "opened view-only",
    };

    public static List<string> Bits(int value, Dictionary<int, string> names)
    {
        var r = new List<string>();
        if (value == 0) return r;
        foreach (var kv in names)
            if ((value & kv.Key) != 0) r.Add(kv.Value);
        var known = 0;
        foreach (var k in names.Keys) known |= k;
        if ((value & ~known) != 0) r.Add($"code {value & ~known}");
        return r;
    }

    /// FeatureManager entries that are folders and scene scaffolding rather than modeling.
    /// Reference geometry (planes, axes, the origin) is kept: users build on it.
    public static readonly HashSet<string> FolderFeatureTypes = new HashSet<string>
    {
        "CommentsFolder", "FavoriteFolder", "HistoryFolder", "SelectionSetFolder", "SensorFolder",
        "DocsFolder", "DetailCabinet", "SurfaceBodyFolder", "SolidBodyFolder", "EnvFolder",
        "InkMarkupFolder", "EqnFolder", "MaterialFolder", "BlockFolder", "LiveSectionFolder",
        "MBD3DFolder", "MarkupCommentFolder", "CameraFolder", "PosGroupFolder", "TableFolder",
        "AmbientLight", "DirectionLight", "SpotLight", "PointLight",
    };
}
