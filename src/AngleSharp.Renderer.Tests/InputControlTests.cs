namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Controls;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The controls of the exotic inputs (`color`, `date`, `time`, ...) and the pickers they open.
/// </summary>
public sealed class InputControlTests
{
    private static readonly DateTime Now = new(2024, 5, 10, 9, 30, 0);
    private static readonly InputPickerOptions Options = new() { Now = Now };

    private static async Task<(IDocument Document, IHtmlInputElement Input, List<string> Events)> OpenAsync(string input, Func<IConfiguration, IConfiguration>? configure = null)
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(device);
        var context = BrowsingContext.New(configure?.Invoke(configuration) ?? configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>body { margin: 0 } input { position: absolute; left: 20px; top: 30px; font: 14px sans-serif }</style></head>
            <body>{{input}}</body></html>
            """));
        context.GetDomHarness();
        var element = (IHtmlInputElement)document.QuerySelector("input")!;
        var events = new List<string>();
        element.AddEventListener("input", (_, _) => events.Add("input:" + element.Value));
        element.AddEventListener("change", (_, _) => events.Add("change:" + element.Value));
        return (document, element, events);
    }

    private static (double X, double Y) Center(RenderRect rect) => (rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));

    private static InputPickerResult Press(IInputPicker picker, RenderRect rect)
    {
        var (x, y) = Center(rect);
        return picker.PointerDown(x, y);
    }

    [Theory]
    [InlineData("color", typeof(ColorPicker))]
    [InlineData("date", typeof(CalendarPicker))]
    [InlineData("week", typeof(CalendarPicker))]
    [InlineData("month", typeof(CalendarPicker))]
    [InlineData("datetime-local", typeof(CalendarPicker))]
    [InlineData("time", typeof(TimePicker))]
    public async Task Picker_IsOpenedBelowTheInput(string type, Type pickerType)
    {
        var (_, input, _) = await OpenAsync($"<input type={type}>");

        var picker = input.OpenInputPicker(Options)!;

        Assert.IsType(pickerType, picker);
        Assert.True(picker.IsOpen);
        Assert.Equal(20d, picker.Bounds.X, 1);
        Assert.Equal(input.GetBoundingClientRect().Bottom, (double)picker.Bounds.Y, 1);
        Assert.NotEmpty(picker.Render().Commands);
    }

    [Theory]
    [InlineData("<input type=text>")]
    [InlineData("<input type=range>")]
    [InlineData("<input type=date disabled>")]
    [InlineData("<input type=date readonly>")]
    public async Task Picker_IsNotOpenedForOtherOrInactiveInputs(string markup)
    {
        var (_, input, _) = await OpenAsync(markup);

        Assert.False(input.HasInputPicker());
        Assert.Null(input.OpenInputPicker(Options));
    }

    [Fact]
    public async Task DatePicker_ShowsTheMonthOfTheValueAndChoosesADay()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-03-15>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.Equal(new DateOnly(2024, 3, 1), picker.Shown);
        Assert.Equal(new DateOnly(2024, 3, 15), picker.Selected);
        Assert.Contains(picker.Render().Commands.OfType<DrawTextCommand>(), text => text.Text == "March 2024");

        var result = Press(picker, picker.GetCellBounds(new DateOnly(2024, 3, 21))!.Value);

        Assert.Equal(InputPickerResult.Close, result);
        Assert.False(picker.IsOpen);
        Assert.Equal("2024-03-21", input.Value);
        Assert.Equal(["input:2024-03-21", "change:2024-03-21"], events);
    }

    [Fact]
    public async Task DatePicker_WithoutAValueShowsTodayAndMovesBetweenMonths()
    {
        var (_, input, _) = await OpenAsync("<input type=date>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;
        Assert.Equal(new DateOnly(2024, 5, 1), picker.Shown);
        Assert.Null(picker.Selected);

        var next = picker.GetPartAt(0, 0, out _);
        Assert.Equal(CalendarPickerPart.None, next);

        // the buttons are at the top right
        var (x, y) = FindPart(picker, CalendarPickerPart.Next);
        Assert.Equal(InputPickerResult.Repaint, picker.PointerDown(x, y));
        Assert.Equal(new DateOnly(2024, 6, 1), picker.Shown);

        (x, y) = FindPart(picker, CalendarPickerPart.Previous);
        picker.PointerDown(x, y);
        picker.PointerDown(x, y);
        Assert.Equal(new DateOnly(2024, 4, 1), picker.Shown);

        (x, y) = FindPart(picker, CalendarPickerPart.Today);
        Assert.Equal(InputPickerResult.Close, picker.PointerDown(x, y));
        Assert.Equal("2024-05-10", input.Value);
    }

    [Fact]
    public async Task DatePicker_ClearsTheValue()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-03-15>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        var (x, y) = FindPart(picker, CalendarPickerPart.Clear);

        Assert.Equal(InputPickerResult.Close, picker.PointerDown(x, y));
        Assert.Equal(string.Empty, input.Value);
        Assert.Equal(["input:", "change:"], events);
    }

    [Fact]
    public async Task DatePicker_DoesNotChooseDaysOutsideOfMinAndMax()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-03-15 min=2024-03-10 max=2024-03-20>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.True(picker.IsDisabled(new DateOnly(2024, 3, 9)));
        Assert.False(picker.IsDisabled(new DateOnly(2024, 3, 10)));
        Assert.Equal(InputPickerResult.None, Press(picker, picker.GetCellBounds(new DateOnly(2024, 3, 25))!.Value));
        Assert.True(picker.IsOpen);
        Assert.Equal("2024-03-15", input.Value);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DatePicker_IsUsedWithTheKeyboard()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-01-31>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        picker.KeyDown("ArrowRight");
        Assert.Equal(new DateOnly(2024, 2, 1), picker.Focus);
        Assert.Equal(new DateOnly(2024, 2, 1), picker.Shown);
        picker.KeyDown("ArrowDown");
        picker.KeyDown("PageDown");

        Assert.Equal(InputPickerResult.Close, picker.KeyDown("Enter"));
        Assert.Equal("2024-03-08", input.Value);
        Assert.Equal(["input:2024-03-08", "change:2024-03-08"], events);
    }

    [Fact]
    public async Task DatePicker_EscapeBringsBackTheValue()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-01-31>");
        var picker = input.OpenInputPicker(Options)!;

        picker.KeyDown("ArrowRight");

        Assert.Equal(InputPickerResult.Close, picker.KeyDown("Escape"));
        Assert.Equal("2024-01-31", input.Value);
        Assert.Empty(events);
    }

    [Fact]
    public async Task WeekPicker_ChoosesTheWeekOfADay()
    {
        var (_, input, events) = await OpenAsync("<input type=week value=2024-W18>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        // week 18 of 2024 starts on Monday, April 29
        Assert.Equal(new DateOnly(2024, 4, 29), picker.Selected);
        Assert.Equal(new DateOnly(2024, 4, 1), picker.Shown);

        Press(picker, picker.GetCellBounds(new DateOnly(2024, 4, 18))!.Value);

        Assert.Equal("2024-W16", input.Value);
        Assert.Equal(["input:2024-W16", "change:2024-W16"], events);
    }

    [Fact]
    public async Task MonthPicker_ChoosesAMonthOfTheYear()
    {
        var (_, input, _) = await OpenAsync("<input type=month value=2023-11>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.Equal(new DateOnly(2023, 1, 1), picker.Shown);
        var (x, y) = FindPart(picker, CalendarPickerPart.Next);
        picker.PointerDown(x, y);
        Assert.Equal(new DateOnly(2024, 1, 1), picker.Shown);

        Press(picker, picker.GetCellBounds(new DateOnly(2024, 8, 1))!.Value);

        Assert.Equal("2024-08", input.Value);
    }

    [Fact]
    public async Task DateTimePicker_KeepsTheTimeAndStaysOpen()
    {
        var (_, input, events) = await OpenAsync("<input type=datetime-local value=2024-05-03T13:05>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;
        Assert.Equal(new TimeOnly(13, 5), picker.Time);

        var result = Press(picker, picker.GetCellBounds(new DateOnly(2024, 5, 17))!.Value);

        Assert.Equal(InputPickerResult.Repaint, result);
        Assert.True(picker.IsOpen);
        Assert.Equal("2024-05-17T13:05", input.Value);

        picker.Close();
        Assert.Equal(["input:2024-05-17T13:05", "change:2024-05-17T13:05"], events);
    }

    [Fact]
    public async Task TimePicker_ChoosesHoursMinutesAndMeridiem()
    {
        var (_, input, events) = await OpenAsync("<input type=time value=13:05>");
        var picker = (TimePicker)input.OpenInputPicker(Options)!;
        Assert.Equal(new TimeOnly(13, 5), picker.Time);

        Assert.Equal(InputPickerResult.Repaint, PressRow(picker, TimePickerColumn.Hour, 3));
        Assert.Equal("15:05", input.Value);
        PressRow(picker, TimePickerColumn.Minute, 7);
        Assert.Equal("15:07", input.Value);
        PressRow(picker, TimePickerColumn.Meridiem, 0);
        Assert.Equal("03:07", input.Value);
        Assert.True(picker.IsOpen);

        Assert.Equal(InputPickerResult.Close, picker.KeyDown("Enter"));
        Assert.Equal(["input:15:05", "input:15:07", "input:03:07", "change:03:07"], events);
    }

    [Fact]
    public async Task TimePicker_ScrollsAColumnWithTheWheel()
    {
        var (_, input, _) = await OpenAsync("<input type=time value=00:00>");
        var picker = (TimePicker)input.OpenInputPicker(Options)!;
        var minutes = picker.GetColumnBounds(TimePickerColumn.Minute)!.Value;
        var (x, y) = Center(minutes);
        picker.TryGetRowAt(x, minutes.Y + 1, out _, out var first);
        Assert.Equal(0, first);

        Assert.Equal(InputPickerResult.Repaint, picker.Wheel(x, y, 3 * picker.RowHeight));
        picker.TryGetRowAt(x, minutes.Y + 1, out _, out first);
        Assert.Equal(3, first);
    }

    [Fact]
    public async Task TimePicker_IsUsedWithTheKeyboard()
    {
        var (_, input, _) = await OpenAsync("<input type=time value=11:59>");
        var picker = input.OpenInputPicker(Options)!;

        picker.KeyDown("ArrowDown");
        Assert.Equal("00:59", input.Value); // 12 AM
        picker.KeyDown("ArrowRight");
        picker.KeyDown("ArrowDown");
        Assert.Equal("00:00", input.Value);
        picker.KeyDown("ArrowRight");
        picker.KeyDown("ArrowUp");
        Assert.Equal("12:00", input.Value);
    }

    [Fact]
    public async Task ColorPicker_FollowsThePointerAndFiresChangeOnClosing()
    {
        var (_, input, events) = await OpenAsync("<input type=color value=#ff0000>");
        var picker = (ColorPicker)input.OpenInputPicker(Options)!;
        Assert.Equal(new RenderColor(255, 0, 0), picker.Color);

        // the bottom of the area is black, its top left corner white
        picker.PointerDown(picker.Area.X + 5, picker.Area.Y + picker.Area.Height - 1);
        picker.PointerMove(picker.Area.X - 50, picker.Area.Y + picker.Area.Height + 50);
        Assert.Equal("#000000", input.Value);
        picker.PointerMove(picker.Area.X - 50, picker.Area.Y - 50);
        Assert.Equal("#ffffff", input.Value);
        picker.PointerUp(picker.Area.X + picker.Area.Width + 50, picker.Area.Y - 50);
        Assert.Equal("#ff0000", input.Value);
        Assert.DoesNotContain(events, e => e.StartsWith("change", StringComparison.Ordinal));

        // the hue slider keeps saturation and brightness
        picker.PointerDown(picker.HueBar.X + (picker.HueBar.Width / 3f), picker.HueBar.Y + 2);
        picker.PointerUp(picker.HueBar.X + (picker.HueBar.Width / 3f), picker.HueBar.Y + 2);
        Assert.Equal("#00ff00", input.Value);

        picker.Close();
        Assert.Equal("change:#00ff00", events[^1]);
        Assert.Single(events, e => e.StartsWith("change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ColorPicker_ShowsTheSuggestionsOfItsList()
    {
        var (_, input, _) = await OpenAsync("""
            <input type=color list=colors value=#000000>
            <datalist id=colors><option value=#112233><option value=nonsense><option value=#445566></datalist>
            """);
        var picker = (ColorPicker)input.OpenInputPicker(Options)!;

        Assert.Equal([new RenderColor(0x11, 0x22, 0x33), new RenderColor(0x44, 0x55, 0x66)], picker.Swatches);

        Press(picker, picker.SwatchBounds[1]);

        Assert.Equal("#445566", input.Value);
        Assert.True(picker.IsOpen);
    }

    [Fact]
    public async Task Picker_UsesTheDarkSchemeAndTheAccentColor()
    {
        var (_, input, _) = await OpenAsync("<input type=date style='color-scheme: dark; accent-color: rgb(200, 0, 0)' value=2024-05-03>");
        var picker = (InputPicker)input.OpenInputPicker(Options)!;

        Assert.Equal(InputPickerPalette.Dark.Background, picker.Context.Palette.Background);
        Assert.Equal(new RenderColor(200, 0, 0), picker.Context.Palette.Accent);
        Assert.Contains(picker.Render().Commands.OfType<FillRectCommand>(), fill => fill.Paint is RenderColorPaint { Color: { R: 200, G: 0, B: 0 } });
    }

    [Fact]
    public async Task Picker_OpensAboveAnInputAtTheBottom()
    {
        var (_, input, _) = await OpenAsync("<input type=date style='top: 560px'>");
        var picker = input.OpenInputPicker(Options)!;

        Assert.Equal(560d, picker.Bounds.Y + picker.Bounds.Height, 1);
    }

    [Fact]
    public async Task CustomControl_PaintsTheInputAndOpensItsPicker()
    {
        var control = new SwatchControl();
        var (document, input, _) = await OpenAsync("<input type=color value=#123456>", configuration => configuration.WithInputControl("color", control));

        var list = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600 });

        Assert.Contains(list.Commands.OfType<FillRectCommand>(), fill => fill.Paint is RenderColorPaint { Color: var color } && color == SwatchControl.Marker);
        Assert.DoesNotContain(list.Commands.OfType<FillRectCommand>(), fill => fill.Paint is RenderColorPaint { Color: { R: 0x12, G: 0x34, B: 0x56 } });
        Assert.Equal("#123456", control.PaintedValue);
        Assert.IsType<SwatchPicker>(input.OpenInputPicker(Options));
    }

    [Fact]
    public async Task CustomControl_LeavesTheOtherTypesToTheBuiltInControls()
    {
        var (_, input, _) = await OpenAsync("<input type=date>", configuration => configuration.WithInputControl("color", new SwatchControl()));

        Assert.IsType<CalendarPicker>(input.OpenInputPicker(Options));
    }

    [Fact]
    public async Task CustomControl_CanShowOtherInputTypes()
    {
        var control = new SwatchControl();
        var (document, input, _) = await OpenAsync("<input type=range value=12>", configuration => configuration.WithInputControl("range", control));

        new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600 });

        Assert.Equal("12", control.PaintedValue);
        Assert.True(input.HasInputPicker());
    }

    [Fact]
    public void DefaultRegistry_CannotBeChanged()
    {
        Assert.Throws<InvalidOperationException>(() => InputControlRegistry.Default.Register("color", new SwatchControl()));
        Assert.Equal(["color", "date", "datetime-local", "month", "time", "week"], InputControlRegistry.Default.Types.Order());
    }

    [Theory]
    [InlineData("2024-W01", 2024, 1, 1)]
    [InlineData("2020-W53", 2020, 12, 28)]
    [InlineData("2021-W53", 0, 0, 0)]
    [InlineData("2024-W1", 0, 0, 0)]
    public void InputValueFormat_ParsesIsoWeeks(string value, int year, int month, int day)
    {
        var valid = InputValueFormat.TryParseWeek(value, out var monday);

        Assert.Equal(year != 0, valid);

        if (valid)
        {
            Assert.Equal(new DateOnly(year, month, day), monday);
            Assert.Equal(value, InputValueFormat.FormatWeek(monday.AddDays(6)));
        }
    }

    [Fact]
    public async Task CustomControl_SetsTheContentSizeOfTheInput()
    {
        var control = new SwatchControl { Size = new InputControlSize(120f, 40f) };
        var (_, input, _) = await OpenAsync("<input type=color style='padding: 2px; border: 3px solid'>", configuration => configuration.WithInputControl("color", control));

        var rect = input.GetBoundingClientRect();

        Assert.Equal(130d, rect.Width, 1);
        Assert.Equal(50d, rect.Height, 1);
    }

    [Fact]
    public async Task CustomControl_SetsTheBorderBoxSizeOfTheInput()
    {
        var control = new SwatchControl { Size = new InputControlSize(120f, 40f, InputControlSizeBox.BorderBox) };
        var (_, input, _) = await OpenAsync("<input type=color style='padding: 2px; border: 3px solid'>", configuration => configuration.WithInputControl("color", control));

        var rect = input.GetBoundingClientRect();

        Assert.Equal(120d, rect.Width, 1);
        Assert.Equal(40d, rect.Height, 1);
    }

    [Fact]
    public async Task CustomControl_LeavesASideToTheBuiltInSizeAndTheAuthor()
    {
        var control = new SwatchControl { Size = new InputControlSize(120f, null) };
        var (document, input, _) = await OpenAsync("""
            <input type=color>
            <input type=color id=authored style='width: 33px; height: 44px; box-sizing: border-box'>
            """, configuration => configuration.WithInputControl("color", control));

        var rect = input.GetBoundingClientRect();
        var authored = document.GetElementById("authored")!.GetBoundingClientRect();

        // the built-in height of a color input is 27 pixels as a whole
        Assert.Equal(27d, rect.Height, 1);
        Assert.True(rect.Width > 120d, $"The width is {rect.Width}.");
        Assert.Equal(33d, authored.Width, 1);
        Assert.Equal(44d, authored.Height, 1);
    }

    [Fact]
    public async Task CustomControl_SizesOtherInputTypes()
    {
        var control = new SwatchControl { Size = new InputControlSize(200f, 8f) };
        var (_, input, _) = await OpenAsync("<input type=range style='padding: 0; border: 0; margin: 0'>", configuration => configuration.WithInputControl("range", control));

        var rect = input.GetBoundingClientRect();

        Assert.Equal(200d, rect.Width, 1);
        Assert.Equal(8d, rect.Height, 1);
    }

    [Fact]
    public async Task DateTimeControl_IsAsWideAsTheValueItFormats()
    {
        var (_, plain, _) = await OpenAsync("<input type=date value=2024-05-03>");
        var (_, long_, _) = await OpenAsync("<input type=date value=2024-05-03>", configuration => configuration.WithInputControl("date", new LongDateControl()));

        Assert.True(long_.GetBoundingClientRect().Width > plain.GetBoundingClientRect().Width + 50d);
    }

    [Fact]
    public async Task DatePicker_DisablesDaysOffTheStep()
    {
        var (_, input, events) = await OpenAsync("<input type=date value=2024-05-08 min=2024-05-01 step=7>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.False(picker.IsDisabled(new DateOnly(2024, 5, 15)));
        Assert.True(picker.IsDisabled(new DateOnly(2024, 5, 16)));
        Assert.True(picker.IsDisabled(new DateOnly(2024, 4, 24))); // before min
        Assert.Equal(InputPickerResult.None, Press(picker, picker.GetCellBounds(new DateOnly(2024, 5, 16))!.Value));

        Press(picker, picker.GetCellBounds(new DateOnly(2024, 5, 22))!.Value);

        Assert.Equal("2024-05-22", input.Value);
        Assert.Equal(["input:2024-05-22", "change:2024-05-22"], events);
    }

    [Fact]
    public async Task DatePicker_StepsFromTheValueWithoutMin()
    {
        var (_, input, _) = await OpenAsync("<input type=date value=2024-05-03 step=2>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.False(picker.IsDisabled(new DateOnly(2024, 5, 5)));
        Assert.True(picker.IsDisabled(new DateOnly(2024, 5, 4)));
    }

    [Fact]
    public async Task DatePicker_AllowsEveryDayWithStepAny()
    {
        var (_, input, _) = await OpenAsync("<input type=date step=any>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.False(picker.IsDisabled(new DateOnly(2024, 5, 4)));
        Assert.False(picker.Constraints.HasStep);
    }

    [Fact]
    public async Task WeekAndMonthPickers_DisableWhatIsOffTheStep()
    {
        var (_, week, _) = await OpenAsync("<input type=week min=2024-W02 step=2>");
        var weeks = (CalendarPicker)week.OpenInputPicker(Options)!;
        var (_, month, _) = await OpenAsync("<input type=month min=2024-01 step=3>");
        var months = (CalendarPicker)month.OpenInputPicker(Options)!;

        // weeks 2, 4, 6, ... of 2024; week 20 starts on Monday, May 13
        Assert.False(weeks.IsDisabled(new DateOnly(2024, 5, 13)));
        Assert.True(weeks.IsDisabled(new DateOnly(2024, 5, 20)));
        Assert.False(months.IsDisabled(new DateOnly(2024, 4, 1)));
        Assert.True(months.IsDisabled(new DateOnly(2024, 5, 1)));
        Assert.Equal(InputPickerResult.None, Press(months, months.GetCellBounds(new DateOnly(2024, 5, 1))!.Value));
    }

    [Fact]
    public async Task TimePicker_ListsTheMinutesOfTheStep()
    {
        var (_, input, events) = await OpenAsync("<input type=time step=900>");
        var picker = (TimePicker)input.OpenInputPicker(Options)!;

        Assert.Equal([TimePickerColumn.Hour, TimePickerColumn.Minute, TimePickerColumn.Meridiem], picker.Columns);
        Assert.Equal([0, 15, 30, 45], ShownValues(picker, TimePickerColumn.Minute));

        PressRow(picker, TimePickerColumn.Hour, 2);
        PressRow(picker, TimePickerColumn.Minute, 45);

        Assert.Equal("02:45", input.Value);
        Assert.Equal(["input:02:00", "input:02:45"], events);
    }

    [Fact]
    public async Task TimePicker_ShowsSecondsForAStepOfSeconds()
    {
        var (_, input, _) = await OpenAsync("<input type=time step=20 value=10:00>");
        var picker = (TimePicker)input.OpenInputPicker(Options)!;

        Assert.Equal([TimePickerColumn.Hour, TimePickerColumn.Minute, TimePickerColumn.Second, TimePickerColumn.Meridiem], picker.Columns);
        Assert.Equal([0, 20, 40], ShownValues(picker, TimePickerColumn.Second));

        PressRow(picker, TimePickerColumn.Second, 40);

        Assert.Equal("10:00:40", input.Value);
    }

    [Fact]
    public async Task TimePicker_KeepsToMinMaxAndTheirStep()
    {
        var (_, input, _) = await OpenAsync("<input type=time min=08:30 max=11:30 step=3600>");
        var picker = (TimePicker)input.OpenInputPicker(Options)!;

        Assert.Equal([8, 9, 10, 11], ShownValues(picker, TimePickerColumn.Hour));
        Assert.Equal([30], ShownValues(picker, TimePickerColumn.Minute));
        Assert.Equal([0], ShownValues(picker, TimePickerColumn.Meridiem));

        // a chosen hour gets the minutes the step allows
        PressRow(picker, TimePickerColumn.Hour, 9);
        Assert.Equal("09:30", input.Value);

        picker.KeyDown("ArrowUp");
        Assert.Equal("08:30", input.Value);
        picker.KeyDown("ArrowUp");
        Assert.Equal("11:30", input.Value);
    }

    [Fact]
    public async Task DateTimePicker_MovesTheTimeOntoTheStep()
    {
        var (_, input, _) = await OpenAsync("<input type=datetime-local min=2024-05-01T09:00 step=86400>");
        var picker = (CalendarPicker)input.OpenInputPicker(Options)!;

        Assert.True(picker.IsDisabled(new DateOnly(2024, 4, 30)));
        Assert.False(picker.IsDisabled(new DateOnly(2024, 5, 20)));

        Press(picker, picker.GetCellBounds(new DateOnly(2024, 5, 20))!.Value);

        Assert.Equal("2024-05-20T09:00", input.Value);
        Assert.Equal(new TimeOnly(9, 0), picker.Time);
    }

    [Theory]
    [InlineData("time", "08:00", "900", "08:20", "08:15")]
    [InlineData("time", "08:00", "900", "08:23", "08:30")]
    [InlineData("time", "08:00", "any", "08:23", "08:23")]
    public async Task Constraints_SnapTimesToTheNearestStep(string type, string min, string step, string time, string expected)
    {
        var (_, input, _) = await OpenAsync($"<input type={type} min={min} step={step}>");
        var constraints = InputValueConstraints.For(input);

        Assert.Equal(TimeOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), constraints.Snap(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Constraints_AllowTimesOverMidnightForAReversedRange()
    {
        var (_, input, _) = await OpenAsync("<input type=time min=22:00 max=02:00>");
        var constraints = InputValueConstraints.For(input);

        Assert.True(constraints.Allows(new TimeOnly(23, 0)));
        Assert.True(constraints.Allows(new TimeOnly(1, 0)));
        Assert.False(constraints.Allows(new TimeOnly(12, 0)));
    }

    private static List<int> ShownValues(TimePicker picker, TimePickerColumn column)
    {
        var bounds = picker.GetColumnBounds(column)!.Value;
        var values = new List<int>();

        for (var y = bounds.Y + (picker.RowHeight / 2f); y < bounds.Y + bounds.Height; y += picker.RowHeight)
        {
            if (picker.TryGetRowAt(bounds.X + 5, y, out _, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static InputPickerResult PressRow(TimePicker picker, TimePickerColumn column, int value)
    {
        var bounds = picker.GetColumnBounds(column)!.Value;

        for (var y = bounds.Y + 1; y < bounds.Y + bounds.Height; y += picker.RowHeight)
        {
            if (picker.TryGetRowAt(bounds.X + 5, y, out _, out var found) && found == value)
            {
                return picker.PointerDown(bounds.X + 5, y);
            }
        }

        throw new InvalidOperationException($"The row {value} of column {column} is not shown.");
    }

    private static (double X, double Y) FindPart(CalendarPicker picker, CalendarPickerPart part)
    {
        var bounds = picker.Bounds;

        for (var y = bounds.Y + 1; y < bounds.Y + bounds.Height; y += 2)
        {
            for (var x = bounds.X + 1; x < bounds.X + bounds.Width; x += 2)
            {
                if (picker.GetPartAt(x, y, out _) == part)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException($"There is no {part}.");
    }

    private sealed class LongDateControl : DateTimeInputControl
    {
        public LongDateControl()
            : base("date")
        {
        }

        public override string FormatValue(string value) => "Friday, the third of May in the year 2024";
    }

    private sealed class SwatchControl : IInputControl
    {
        public static readonly RenderColor Marker = new(1, 2, 3);

        public InputControlSize? Size { get; init; }

        public InputControlSize? GetIntrinsicSize(InputControlSizeContext context) => Size;

        public string? PaintedValue { get; private set; }

        public void Paint(InputControlPaintContext context)
        {
            PaintedValue = context.Value;
            context.DisplayList.FillRect(context.ContentBox, Marker);
        }

        public IInputPicker? CreatePicker(InputPickerContext context) => new SwatchPicker(context);
    }

    private sealed class SwatchPicker : InputPicker
    {
        public SwatchPicker(InputPickerContext context)
            : base(context)
        {
            Bounds = context.Place(40, 40);
        }

        protected override void Paint(DisplayList displayList) => displayList.FillRect(Bounds, SwatchControl.Marker);
    }
}
