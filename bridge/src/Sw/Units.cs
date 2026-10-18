using System;
using System.Collections.Generic;

namespace SwBridge.Sw;

/// The API speaks metres, radians and kilograms. The tools speak millimetres, degrees and
/// kilograms, and say so in every field name, so neither the model nor the user converts.
static class Units
{
    public static double Mm(double metres) => Round(metres * 1000.0);

    public static double Round(double v) => Math.Round(v, 6);

    public static double FromSi(double si, int dimensionKind) => dimensionKind switch
    {
        K.DimLinear => Round(si * 1000.0),
        K.DimAngular => Round(si * 180.0 / Math.PI),
        _ => Round(si),
    };

    public static double ToSi(double value, int dimensionKind) => dimensionKind switch
    {
        K.DimLinear => value / 1000.0,
        K.DimAngular => value * Math.PI / 180.0,
        _ => value,
    };

    public static string Label(int dimensionKind) => dimensionKind switch
    {
        K.DimLinear => "mm",
        K.DimAngular => "deg",
        _ => "",
    };
}

/// swFeatureError_e names, from the 2016 SP1 interop.
static class FeatureErrors
{
    static readonly Dictionary<int, string> names = new Dictionary<int, string>
    {
        [1] = "Unknown", [10] = "FilletNoLoop", [11] = "FilletNoFace", [12] = "FilletInvalidRadius",
        [13] = "FilletNoEdge", [14] = "FilletModelGeometry", [15] = "FilletRadiusTooSmall",
        [16] = "FilletCannotExtend", [17] = "FilletRadiusEliminateElement", [18] = "FilletRadiusTooBig",
        [19] = "FilletRadiusTooBig2", [30] = "ExtrusionDisjoint", [31] = "ExtrusionNoEndFound",
        [32] = "ExtrusionBadGeometricConditions", [33] = "ExtrusionCutContourOpenAndClosed",
        [34] = "ExtrusionCutContourInvalid", [35] = "ExtrusionOpenCutContourInvalid",
        [36] = "ExtrusionBossContourOpenAndClosed", [37] = "ExtrusionBossContourInvalid",
        [38] = "MateInvalidEdge", [39] = "MateInvalidFace", [40] = "MateFailedCreatingSurface",
        [41] = "MateInvalidEntity", [42] = "MateUnknownTangent", [43] = "MateDanglingGeometry",
        [44] = "MateEntityNotLinear", [45] = "MateEntityFailed", [46] = "MateOverdefined",
        [47] = "MateIlldefined", [48] = "MateBroken", [49] = "FeatureDeprecated", [50] = "FeatureObsolete",
        [51] = "SketchExternalReferenceFailed",
    };

    public static string Name(int code) => names.TryGetValue(code, out var n) ? n : $"error {code}";
}
