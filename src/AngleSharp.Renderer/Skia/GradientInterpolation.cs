using System;
using System.Collections.Generic;
using AngleSharp.Renderer.Rendering;
using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Interpolates the colors of a gradient where Skia's own (premultiplied sRGB, linear) mixing
/// does not: across a color hint, which moves the midpoint of a transition along an exponential
/// curve, and in another color space (`in oklch`, `in hsl longer hue`) (CSS Images 4 §3.5.3,
/// CSS Color 4 §12). Each such transition is sampled into enough stops for Skia to draw it.
/// </summary>
internal static class GradientInterpolation
{
    private const int Samples = 24;

    /// <summary>
    /// Builds the colors and positions of the gradient from its color stops (in order, with their
    /// positions resolved) and the hint between each stop and the next (NaN where there is none).
    /// </summary>
    public static (SKColor[] Colors, float[] Positions) Build(IReadOnlyList<(RenderColor Color, float Position)> stops, IReadOnlyList<float> hints, string? colorSpace, string? hueMethod)
    {
        var colors = new List<SKColor>(stops.Count * Samples);
        var positions = new List<float>(stops.Count * Samples);
        var space = Normalize(colorSpace);
        var method = hueMethod?.Trim().ToLowerInvariant() ?? "shorter";

        for (var i = 0; i < stops.Count; i++)
        {
            var (color, position) = stops[i];

            if (i == stops.Count - 1)
            {
                colors.Add(ToSk(color));
                positions.Add(position);
                break;
            }

            var (next, nextPosition) = stops[i + 1];
            var hint = hints[i];
            var length = nextPosition - position;

            if (length <= 0f)
            {
                // a hard stop
                colors.Add(ToSk(color));
                positions.Add(position);
                continue;
            }

            var exponent = float.NaN;
            var jump = float.NaN;

            if (!float.IsNaN(hint))
            {
                var h = (hint - position) / length;

                if (h <= 0f)
                {
                    jump = 0f;
                }
                else if (h >= 1f)
                {
                    jump = 1f;
                }
                else
                {
                    exponent = (float)(Math.Log(0.5) / Math.Log(h));
                }
            }

            var from = ToSpace(color, space);
            var to = ToSpace(next, space);

            if (IsPolar(space))
            {
                FixHues(ref from, ref to, method);
            }

            for (var k = 0; k < Samples; k++)
            {
                var t = k / (float)Samples;
                var weight = !float.IsNaN(jump) ? (t > jump ? 1f : 0f)
                    : !float.IsNaN(exponent) ? (float)Math.Pow(t, exponent)
                    : t;
                colors.Add(ToSk(FromSpace(Mix(from, to, weight, space), space)));
                positions.Add(position + (t * length));
            }
        }

        return (colors.ToArray(), positions.ToArray());
    }

    private static string Normalize(string? colorSpace) => colorSpace?.Trim().ToLowerInvariant() switch
    {
        null or "" => "srgb",
        "xyz" => "xyz-d65",
        var other => other,
    };

    private static bool IsPolar(string space) => space is "hsl" or "hwb" or "lch" or "oklch";

    private static SKColor ToSk(RenderColor color) => new(color.R, color.G, color.B, color.A);

    // the components of a color in a space, with its alpha; the hue (polar spaces) is the third
    // component, NaN where it is powerless (an achromatic color)
    private readonly record struct Components(double C0, double C1, double C2, double Alpha);

    private static Components Mix(Components from, Components to, float weight, string space)
    {
        var alpha = from.Alpha + ((to.Alpha - from.Alpha) * weight);
        var polar = IsPolar(space);

        // premultiplied, except for the hue
        double Premultiplied(double value, double a) => value * a;
        double Lerp(double a, double b) => a + ((b - a) * weight);

        var c0 = Lerp(Premultiplied(from.C0, from.Alpha), Premultiplied(to.C0, to.Alpha));
        var c1 = Lerp(Premultiplied(from.C1, from.Alpha), Premultiplied(to.C1, to.Alpha));
        var c2 = polar ? Lerp(from.C2, to.C2) : Lerp(Premultiplied(from.C2, from.Alpha), Premultiplied(to.C2, to.Alpha));

        if (alpha > 0)
        {
            c0 /= alpha;
            c1 /= alpha;

            if (!polar)
            {
                c2 /= alpha;
            }
        }

        return new Components(c0, c1, c2, alpha);
    }

    private static void FixHues(ref Components start, ref Components end, string method)
    {
        var h1 = start.C2;
        var h2 = end.C2;

        // a powerless hue takes the other one
        if (double.IsNaN(h1) && double.IsNaN(h2))
        {
            h1 = h2 = 0;
        }
        else if (double.IsNaN(h1))
        {
            h1 = h2;
        }
        else if (double.IsNaN(h2))
        {
            h2 = h1;
        }

        var difference = h2 - h1;

        switch (method)
        {
            case "longer":
                if (difference > 0 && difference < 180)
                {
                    h1 += 360;
                }
                else if (difference > -180 && difference <= 0)
                {
                    h2 += 360;
                }

                break;
            case "increasing":
                if (difference < 0)
                {
                    h2 += 360;
                }

                break;
            case "decreasing":
                if (difference > 0)
                {
                    h1 += 360;
                }

                break;
            default:
                if (difference > 180)
                {
                    h1 += 360;
                }
                else if (difference < -180)
                {
                    h2 += 360;
                }

                break;
        }

        start = start with { C2 = h1 };
        end = end with { C2 = h2 };
    }

    private static Components ToSpace(RenderColor color, string space)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var alpha = color.A / 255.0;

        switch (space)
        {
            case "hsl":
            {
                var (h, s, l) = RgbToHsl(r, g, b);
                return new Components(s, l, h, alpha);
            }
            case "hwb":
            {
                var (h, _, _) = RgbToHsl(r, g, b);
                var white = Math.Min(r, Math.Min(g, b));
                var black = 1 - Math.Max(r, Math.Max(g, b));
                return new Components(white, black, white + black >= 1 ? double.NaN : h, alpha);
            }
        }

        var (lr, lg, lb) = (ToLinear(r), ToLinear(g), ToLinear(b));

        switch (space)
        {
            case "srgb-linear":
                return new Components(lr, lg, lb, alpha);
            case "oklab":
            {
                var (l, a, bb) = LinearToOklab(lr, lg, lb);
                return new Components(l, a, bb, alpha);
            }
            case "oklch":
            {
                var (l, a, bb) = LinearToOklab(lr, lg, lb);
                return ToPolar(l, a, bb, alpha, 0.0002);
            }
            case "xyz-d65":
            {
                var (x, y, z) = LinearToXyz(lr, lg, lb);
                return new Components(x, y, z, alpha);
            }
            case "xyz-d50":
            {
                var (x, y, z) = D65ToD50(LinearToXyz(lr, lg, lb));
                return new Components(x, y, z, alpha);
            }
            case "lab":
            {
                var (l, a, bb) = XyzD50ToLab(D65ToD50(LinearToXyz(lr, lg, lb)));
                return new Components(l, a, bb, alpha);
            }
            case "lch":
            {
                var (l, a, bb) = XyzD50ToLab(D65ToD50(LinearToXyz(lr, lg, lb)));
                return ToPolar(l, a, bb, alpha, 0.0015 * 100);
            }
            default:
                // srgb (and the wide gamut RGB spaces, which sRGB colors are drawn in anyway)
                return new Components(r, g, b, alpha);
        }
    }

    private static RenderColor FromSpace(Components c, string space)
    {
        double r, g, b;

        switch (space)
        {
            case "hsl":
                (r, g, b) = HslToRgb(c.C2, c.C0, c.C1);
                break;
            case "hwb":
            {
                var white = c.C0;
                var black = c.C1;

                if (white + black >= 1)
                {
                    var grey = white / (white + black);
                    (r, g, b) = (grey, grey, grey);
                }
                else
                {
                    (r, g, b) = HslToRgb(c.C2, 1, 0.5);
                    r = (r * (1 - white - black)) + white;
                    g = (g * (1 - white - black)) + white;
                    b = (b * (1 - white - black)) + white;
                }

                break;
            }
            case "srgb-linear":
                (r, g, b) = (FromLinear(c.C0), FromLinear(c.C1), FromLinear(c.C2));
                break;
            case "oklab":
                (r, g, b) = FromLinear(OklabToLinear(c.C0, c.C1, c.C2));
                break;
            case "oklch":
            {
                var (a, bb) = FromPolar(c.C1, c.C2);
                (r, g, b) = FromLinear(OklabToLinear(c.C0, a, bb));
                break;
            }
            case "xyz-d65":
                (r, g, b) = FromLinear(XyzToLinear((c.C0, c.C1, c.C2)));
                break;
            case "xyz-d50":
                (r, g, b) = FromLinear(XyzToLinear(D50ToD65((c.C0, c.C1, c.C2))));
                break;
            case "lab":
                (r, g, b) = FromLinear(XyzToLinear(D50ToD65(LabToXyzD50(c.C0, c.C1, c.C2))));
                break;
            case "lch":
            {
                var (a, bb) = FromPolar(c.C1, c.C2);
                (r, g, b) = FromLinear(XyzToLinear(D50ToD65(LabToXyzD50(c.C0, a, bb))));
                break;
            }
            default:
                (r, g, b) = (c.C0, c.C1, c.C2);
                break;
        }

        return new RenderColor(ToByte(r), ToByte(g), ToByte(b), ToByte(c.Alpha));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static Components ToPolar(double l, double a, double b, double alpha, double achromatic)
    {
        var chroma = Math.Sqrt((a * a) + (b * b));
        var hue = chroma <= achromatic ? double.NaN : ((Math.Atan2(b, a) * 180 / Math.PI) + 360) % 360;
        return new Components(l, chroma, hue, alpha);
    }

    private static (double A, double B) FromPolar(double chroma, double hue)
    {
        var radians = (double.IsNaN(hue) ? 0 : hue) * Math.PI / 180;
        return (chroma * Math.Cos(radians), chroma * Math.Sin(radians));
    }

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double FromLinear(double c) => c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(Math.Max(0, c), 1 / 2.4)) - 0.055;

    private static (double R, double G, double B) FromLinear((double R, double G, double B) c) => (FromLinear(c.R), FromLinear(c.G), FromLinear(c.B));

    private static (double L, double A, double B) LinearToOklab(double r, double g, double b)
    {
        var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return (
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    private static (double R, double G, double B) OklabToLinear(double l, double a, double b)
    {
        var l1 = l + (0.3963377774 * a) + (0.2158037573 * b);
        var m1 = l - (0.1055613458 * a) - (0.0638541728 * b);
        var s1 = l - (0.0894841775 * a) - (1.2914855480 * b);
        var (l3, m3, s3) = (l1 * l1 * l1, m1 * m1 * m1, s1 * s1 * s1);
        return (
            (4.0767416621 * l3) - (3.3077115913 * m3) + (0.2309699292 * s3),
            (-1.2684380046 * l3) + (2.6097574011 * m3) - (0.3413193965 * s3),
            (-0.0041960863 * l3) - (0.7034186147 * m3) + (1.7076147010 * s3));
    }

    private static (double X, double Y, double Z) LinearToXyz(double r, double g, double b) => (
        (0.41239079926595934 * r) + (0.357584339383878 * g) + (0.1804807884018343 * b),
        (0.21263900587151027 * r) + (0.715168678767756 * g) + (0.07219231536073371 * b),
        (0.01933081871559182 * r) + (0.11919477979462598 * g) + (0.9505321522496607 * b));

    private static (double R, double G, double B) XyzToLinear((double X, double Y, double Z) c) => (
        (3.2409699419045226 * c.X) - (1.537383177570094 * c.Y) - (0.4986107602930034 * c.Z),
        (-0.9692436362808796 * c.X) + (1.8759675015077202 * c.Y) + (0.04155505740717559 * c.Z),
        (0.05563007969699366 * c.X) - (0.20397695888897652 * c.Y) + (1.0569715142428786 * c.Z));

    private static (double X, double Y, double Z) D65ToD50((double X, double Y, double Z) c) => (
        (1.0479298208405488 * c.X) + (0.022946793341019088 * c.Y) - (0.05019222954313557 * c.Z),
        (0.029627815688159344 * c.X) + (0.990434484573249 * c.Y) - (0.01707382502938514 * c.Z),
        (-0.009243058152591178 * c.X) + (0.015055144896577895 * c.Y) + (0.7518742899580008 * c.Z));

    private static (double X, double Y, double Z) D50ToD65((double X, double Y, double Z) c) => (
        (0.9554734527042182 * c.X) - (0.023098536874261423 * c.Y) + (0.0632593086610217 * c.Z),
        (-0.028369706963208136 * c.X) + (1.0099954580058226 * c.Y) + (0.021041398966943008 * c.Z),
        (0.012314001688319899 * c.X) - (0.020507696433477912 * c.Y) + (1.3303659366080753 * c.Z));

    private const double LabEpsilon = 216.0 / 24389.0;
    private const double LabKappa = 24389.0 / 27.0;
    private static readonly (double X, double Y, double Z) D50White = (0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585);

    private static (double L, double A, double B) XyzD50ToLab((double X, double Y, double Z) c)
    {
        static double F(double t) => t > LabEpsilon ? Math.Cbrt(t) : ((LabKappa * t) + 16) / 116;
        var fx = F(c.X / D50White.X);
        var fy = F(c.Y / D50White.Y);
        var fz = F(c.Z / D50White.Z);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static (double X, double Y, double Z) LabToXyzD50(double l, double a, double b)
    {
        var fy = (l + 16) / 116;
        var fx = (a / 500) + fy;
        var fz = fy - (b / 200);
        var x = Math.Pow(fx, 3) > LabEpsilon ? Math.Pow(fx, 3) : ((116 * fx) - 16) / LabKappa;
        var y = l > LabKappa * LabEpsilon ? Math.Pow(fy, 3) : l / LabKappa;
        var z = Math.Pow(fz, 3) > LabEpsilon ? Math.Pow(fz, 3) : ((116 * fz) - 16) / LabKappa;
        return (x * D50White.X, y * D50White.Y, z * D50White.Z);
    }

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;

        if (d <= 1e-9)
        {
            return (double.NaN, 0, l);
        }

        var s = l is 0 or 1 ? 0 : (max - l) / Math.Min(l, 1 - l);
        double h;

        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h * 60, s, l);
    }

    private static (double R, double G, double B) HslToRgb(double h, double s, double l)
    {
        h = double.IsNaN(h) ? 0 : ((h % 360) + 360) % 360;

        double F(double n)
        {
            var k = (n + (h / 30)) % 12;
            var a = s * Math.Min(l, 1 - l);
            return l - (a * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1))));
        }

        return (F(0), F(8), F(4));
    }
}
