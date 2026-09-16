using System.Globalization;
using System.Text;

namespace AngleSharp.Renderer.Benchmarks;

/// <summary>
/// Generates a large, realistic HTML+CSS document for the rendering benchmarks: a mix of flow
/// modes (block, inline, float, flexbox, CSS grid, table) and paint features (linear/radial/conic
/// gradients, box-shadow, border-radius, text-overflow ellipsis, ::after generated content, 2D
/// transform, filter, opacity, position: sticky) that a real-world page commonly combines - the
/// goal is a benchmark that exercises the same mixed code paths an actual page render would hit,
/// not a synthetic worst-case for any single feature. Deliberately avoids `<img>`/network image
/// loading (kept deterministic and dependency-free) and `border-image` (a confirmed AngleSharp.Css
/// bug as of the version this benchmark was written against - see AGENTS.md - would crash the
/// render instead of measuring it).
/// </summary>
internal static class BenchmarkFixture
{
    private static readonly String[] Words =
    {
        "quantum", "lattice", "harbor", "velvet", "cascade", "ember", "granite", "orbit", "cobalt",
        "meridian", "thicket", "lumen", "onyx", "tidal", "vellum", "cinder", "arbor", "solstice",
        "quartz", "drift", "canyon", "ripple", "amber", "frost", "willow", "spire", "hollow",
        "beacon", "mosaic", "current", "fable", "ridge", "cove", "prism", "grove", "summit",
    };

    private static readonly String[] Tags =
    {
        "design", "engineering", "research", "growth", "ops", "product", "data", "platform",
        "mobile", "security",
    };

    /// <summary>
    /// Builds the page and returns both the HTML and the approximate element count generated (for
    /// reporting in the benchmark output - not itself part of what is timed).
    /// </summary>
    public static (String Html, Int32 ElementCount) BuildLargePageHtml(Int32 cardCount = 160, Int32 tableRowCount = 40, Int32 gridItemCount = 16)
    {
        var random = new Random(42);
        var sb = new StringBuilder(64 * 1024);
        var elementCount = 0;

        sb.Append("<html><head><style>").Append(Css).Append("</style></head><body>");

        elementCount += AppendHeader(sb);
        sb.Append("<div class=\"container\">");
        elementCount++;

        elementCount += AppendGridSection(sb, gridItemCount, random);
        elementCount += AppendSidebar(sb);

        sb.Append("<div class=\"main\">");
        elementCount++;

        elementCount += AppendCardsSection(sb, cardCount, random);
        elementCount += AppendTableSection(sb, tableRowCount, random);
        elementCount += AppendFormSection(sb);

        sb.Append("</div>"); // .main
        sb.Append("</div>"); // .container

        elementCount += AppendFooter(sb);
        sb.Append("</body></html>");

        return (sb.ToString(), elementCount);
    }

    private static Int32 AppendHeader(StringBuilder sb)
    {
        sb.Append("""<header class="header"><h1>Benchmark Dashboard</h1><nav class="nav">""");
        var count = 2; // header, h1
        foreach (var label in new[] { "Overview", "Reports", "Team", "Settings", "Help" })
        {
            sb.Append("<a href=\"#\">").Append(label).Append("</a>");
            count++;
        }
        sb.Append("</nav></header>");
        return count + 1; // + nav
    }

    private static Int32 AppendGridSection(StringBuilder sb, Int32 gridItemCount, Random random)
    {
        sb.Append("<section class=\"grid\">");
        var count = 1;
        for (var i = 0; i < gridItemCount; i++)
        {
            sb.Append("""<div class="grid-item"><strong>""")
              .Append(CapitalizeFirst(Words[i % Words.Length]))
              .Append("</strong><span class=\"metric\">")
              .Append((random.Next(10, 999)).ToString(CultureInfo.InvariantCulture))
              .Append("</span></div>");
            count += 3; // grid-item, strong, span
        }
        sb.Append("</section>");
        return count;
    }

    private static Int32 AppendSidebar(StringBuilder sb)
    {
        sb.Append("""<aside class="sidebar"><h2>Categories</h2><ul>""");
        var count = 2; // aside, h2
        foreach (var tag in Tags)
        {
            sb.Append("<li>").Append(CapitalizeFirst(tag)).Append("</li>");
            count++;
        }
        sb.Append("</ul></aside>");
        return count + 1; // + ul
    }

    private static Int32 AppendCardsSection(StringBuilder sb, Int32 cardCount, Random random)
    {
        sb.Append("<section class=\"cards\">");
        var count = 1;

        for (var i = 0; i < cardCount; i++)
        {
            var extraClass = (i % 7) switch
            {
                0 => " blurred",
                1 => " rotated",
                2 => " faded",
                _ => "",
            };

            sb.Append("<article class=\"card").Append(extraClass).Append("\">");
            sb.Append("<div class=\"card-media\"></div>");
            sb.Append("<div class=\"card-body\">");
            sb.Append("<h3 class=\"card-title\">").Append(BuildSentence(random, 3, 7)).Append("</h3>");
            sb.Append("<p class=\"card-text\">").Append(BuildSentence(random, 12, 22)).Append("</p>");
            sb.Append("<div class=\"card-tags\">");

            var tagCount = 2 + (i % 3);
            for (var t = 0; t < tagCount; t++)
            {
                sb.Append("<span class=\"tag\">").Append(Tags[(i + t) % Tags.Length]).Append("</span>");
            }

            sb.Append("</div>"); // card-tags
            sb.Append("</div>"); // card-body
            sb.Append("</article>");

            // article, card-media, card-body, h3, p, card-tags, tag spans
            count += 6 + tagCount;
        }

        sb.Append("</section>");
        return count;
    }

    private static Int32 AppendTableSection(StringBuilder sb, Int32 rowCount, Random random)
    {
        sb.Append("<section><h2>Recent Activity</h2><table><thead><tr>");
        var count = 3; // section, h2, table (thead/tbody/tr counted below)
        var headers = new[] { "Item", "Owner", "Status", "Updated", "Score" };

        sb.Append("<tr>"); count++;
        foreach (var header in headers)
        {
            sb.Append("<th>").Append(header).Append("</th>");
            count++;
        }
        sb.Append("</tr></thead><tbody>");
        count += 2; // thead, tbody (the extra </tr> above closes the opened <tr>)

        var statuses = new[] { ("ok", "Healthy"), ("warn", "At risk"), ("err", "Blocked") };

        for (var row = 0; row < rowCount; row++)
        {
            var (statusClass, statusLabel) = statuses[row % statuses.Length];
            sb.Append("<tr>");
            sb.Append("<td>").Append(CapitalizeFirst(Words[row % Words.Length])).Append(' ').Append(row).Append("</td>");
            sb.Append("<td>").Append("user").Append(row % 23).Append("</td>");
            sb.Append("<td><span class=\"badge ").Append(statusClass).Append("\">").Append(statusLabel).Append("</span></td>");
            sb.Append("<td>").Append(row % 30 + 1).Append(" days ago</td>");
            sb.Append("<td>").Append(random.Next(0, 100)).Append("%</td>");
            sb.Append("</tr>");

            count += 7; // tr + 5 td + 1 badge span
        }

        sb.Append("</tbody></table></section>");
        return count;
    }

    private static Int32 AppendFormSection(StringBuilder sb)
    {
        sb.Append("<section><h2>Quick Filter</h2>");
        var count = 2; // section, h2

        sb.Append("""<div class="form-row"><input type="text" placeholder="Search..." /><select><option>All</option><option>Active</option><option>Archived</option></select><input type="checkbox" /><button class="btn" type="button">Apply</button></div>""");
        count += 5; // form-row, input, select, checkbox, button (options intentionally not laid out - see AGENTS.md)

        sb.Append("</section>");
        return count;
    }

    private static Int32 AppendFooter(StringBuilder sb)
    {
        sb.Append("<footer class=\"footer\"><p>Generated for local benchmarking purposes only.</p></footer>");
        return 2; // footer, p
    }

    private static String BuildSentence(Random random, Int32 minWords, Int32 maxWords)
    {
        var wordCount = random.Next(minWords, maxWords + 1);
        var sb = new StringBuilder();

        for (var i = 0; i < wordCount; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Words[random.Next(Words.Length)]);
        }

        return CapitalizeFirst(sb.ToString()) + ".";
    }

    private static String CapitalizeFirst(String value) =>
        value.Length == 0 ? value : Char.ToUpperInvariant(value[0]) + value[1..];

    private const String Css = """
        * { box-sizing: border-box; }
        html, body { margin: 0; padding: 0; font-family: sans-serif; background: #f5f5f7; color: #222; }
        .header { position: sticky; top: 0; display: flex; justify-content: space-between; align-items: center;
                   padding: 16px 24px; background: linear-gradient(135deg, #4e54c8, #8f94fb); color: white;
                   box-shadow: 0 2px 6px rgba(0,0,0,0.25); }
        .header h1 { margin: 0; font-size: 22px; }
        .nav { display: flex; gap: 16px; }
        .nav a { color: white; text-decoration: none; font-size: 14px; }
        .container { width: 1200px; margin: 0 auto; padding: 20px; }
        .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin-bottom: 32px; }
        .grid-item { background: radial-gradient(circle at top left, #ffecd2, #fcb69f); border-radius: 6px;
                     padding: 12px; box-shadow: 0 1px 3px rgba(0,0,0,0.15); display: flex; justify-content: space-between; }
        .sidebar { float: left; width: 220px; padding: 12px 16px; background: white; border-radius: 8px;
                   box-shadow: 0 1px 3px rgba(0,0,0,0.1); margin-right: 16px; }
        .sidebar ul { list-style: square; padding-left: 20px; margin: 8px 0 0 0; }
        .main { overflow: hidden; }
        .cards { display: flex; flex-wrap: wrap; gap: 16px; margin-bottom: 32px; }
        .card { width: 220px; background: white; border-radius: 8px; box-shadow: 0 1px 4px rgba(0,0,0,0.1);
                overflow: hidden; }
        .card-media { height: 90px; background: conic-gradient(from 90deg, #ff9a9e, #fad0c4, #fbc2eb, #ff9a9e); }
        .card-body { padding: 12px; }
        .card-title { font-size: 15px; font-weight: bold; margin: 0 0 6px 0; white-space: nowrap; overflow: hidden;
                      text-overflow: ellipsis; }
        .card-text { font-size: 13px; color: #555; line-height: 1.4; }
        .card-tags { display: flex; gap: 6px; margin-top: 8px; flex-wrap: wrap; }
        .tag { background: #eef; color: #446; border-radius: 10px; padding: 2px 8px; font-size: 11px; }
        .card::after { content: " \2605"; color: #f5a623; }
        .blurred { filter: blur(1px) grayscale(30%); }
        .rotated { transform: rotate(3deg) scale(0.98); }
        .faded { opacity: 0.85; }
        table { border-collapse: collapse; width: 100%; margin: 8px 0 24px 0; background: white; }
        table th, table td { border: 1px solid #ddd; padding: 8px; text-align: left; font-size: 13px; }
        table th { background: #333; color: white; }
        .badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 11px; color: white; }
        .badge.ok { background: #2ecc71; }
        .badge.warn { background: #f39c12; }
        .badge.err { background: #e74c3c; }
        .form-row { display: flex; gap: 12px; align-items: center; }
        .btn { padding: 8px 16px; border-radius: 4px; border: none; background: linear-gradient(90deg,#00b09b,#96c93d); color: white; font-size: 14px; }
        .footer { text-align: center; padding: 24px; color: #888; font-size: 12px; }
        """;
}
