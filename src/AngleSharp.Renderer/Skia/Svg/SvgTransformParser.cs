namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Text.RegularExpressions;

using SkiaSharp;

/// <summary>
/// Parses an SVG `transform` attribute (a space-separated list of
/// translate/scale/rotate/skewX/skewY/matrix functions, applied left to right) into a single
/// <see cref="SKMatrix"/>.
/// </summary>
internal static partial class SvgTransformParser
{
    public static SKMatrix Parse(string? transform)
    {
        var result = SKMatrix.CreateIdentity();

        if (string.IsNullOrWhiteSpace(transform))
        {
            return result;
        }

        foreach (Match match in FunctionPattern().Matches(transform))
        {
            var name = match.Groups["name"].Value;
            var args = ParseArguments(match.Groups["args"].Value);
            var next = ToMatrix(name, args);

            // SVG applies the rightmost function to the point first: "translate(...) rotate(...)"
            // means M(p) = translate(rotate(p)). Each new function goes innermost (applied to the
            // point before whatever was already accumulated), so it is PostConcat'd in front.
            result = next.PostConcat(result);
        }

        return result;
    }

    private static SKMatrix ToMatrix(string name, float[] args)
    {
        switch (name.ToLowerInvariant())
        {
            case "translate":
                return SKMatrix.CreateTranslation(args.Length > 0 ? args[0] : 0f, args.Length > 1 ? args[1] : 0f);
            case "scale":
                return SKMatrix.CreateScale(args.Length > 0 ? args[0] : 1f, args.Length > 1 ? args[1] : args.Length > 0 ? args[0] : 1f);
            case "rotate" when args.Length >= 3:
                return SKMatrix.CreateRotationDegrees(args[0], args[1], args[2]);
            case "rotate" when args.Length >= 1:
                return SKMatrix.CreateRotationDegrees(args[0]);
            case "skewx" when args.Length >= 1:
                return SKMatrix.CreateSkew((float)Math.Tan(args[0] * Math.PI / 180.0), 0f);
            case "skewy" when args.Length >= 1:
                return SKMatrix.CreateSkew(0f, (float)Math.Tan(args[0] * Math.PI / 180.0));
            case "matrix" when args.Length >= 6:
                return new SKMatrix(args[0], args[2], args[4], args[1], args[3], args[5], 0f, 0f, 1f);
            default:
                return SKMatrix.CreateIdentity();
        }
    }

    private static float[] ParseArguments(string raw)
    {
        var tokens = raw.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var values = new float[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
        }

        return values;
    }

    [GeneratedRegex(@"(?<name>translate|scale|rotate|skewX|skewY|matrix)\s*\((?<args>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionPattern();
}
