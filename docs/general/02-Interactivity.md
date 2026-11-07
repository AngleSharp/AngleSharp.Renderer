---
title: "Interactivity, Transitions & Animations"
section: "AngleSharp.Renderer"
---
# Interactivity, Transitions & Animations

`HtmlRenderer.RenderToPng`/`BuildDisplayList` render one static snapshot of a document by
themselves. To simulate mouse hover, drive CSS `transition`s and `animation`s, or read/set scroll
position, a document needs an **interactive DOM harness** - a small piece of state, bound to the
document's browsing context, that tracks things a real browser would (cursor position, scroll
offsets, a running clock) and feeds them back into rendering.

## Getting A Harness

A harness needs an `IRenderDevice` registered on the browsing context, then is created (or
retrieved, if one already exists) via `GetDomHarness()`:

```cs
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer;

var renderDevice = new DefaultRenderDevice { ViewPortWidth = 320, ViewPortHeight = 200 };
var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(renderDevice));
var document = await context.OpenAsync(request => request.Content(html));

var harness = document.Context.GetDomHarness();
```

Every document not wired up this way renders exactly as before - the interactive machinery is a
no-op until a harness exists for that browsing context.

## Simulating `:hover`

Set `MousePosition` in viewport coordinates. The harness hit-tests the layout, forces the
`:hover` pseudo-class on the topmost element under the cursor *and its whole ancestor chain*
(matching how a real pointer makes `.card:hover .title` work, not just the innermost element), and
raises `PaintInvalidated`:

```cs
harness.MousePosition = (50, 40);
var image = harness.PaintToPng();
```

`harness.HoveredElement` exposes the currently hit-tested element if you need it directly.

## Selecting Text

Text can be selected as in a browser. A host forwards the pointer to the harness - after firing
`mousedown`, unless that was canceled:

```cs
harness.BeginTextSelection(x, y);             // pressed; clickCount: 2 selects a word, 3 a paragraph
harness.ExtendTextSelection(x, y);            // moved with the button held
harness.EndTextSelection();                   // released

var text = harness.SelectedText;              // as shown, e.g., for the clipboard
```

`BeginTextSelection` takes `extend: true` for a press with Shift held, and returns false when
nothing can be selected there. `SelectAllText()` is for Ctrl+A and `ClearTextSelection()` removes
the selection. `PaintInvalidated` is raised when the selection changes; showing it needs no layout,
as `UpdateFrame()` returns the last frame with the selection painted.

The selection also is the DOM's: `document.GetSelection()` (`getSelection()` for scripts) has the
members of the [Selection API](https://w3c.github.io/selection-api/) except `modify`, and its changes are painted just
the same. `selectstart` (cancelable) and `selectionchange` are fired. `user-select` is honored
(`none`, `text`, `all`), what form controls show cannot be selected, and `::selection` gives the colors
of the highlight and the text.

Selecting is on by default. It can be turned off for a document that has no use for it, in which case
layouts do not record where their text comes from:

```cs
harness.IsTextSelectionEnabled = false;
```

## Frames and Their Metrics

An interactive host lays out a frame with `PrepareFrame()` and rasterizes it on any thread, as it
no longer refers to the DOM. Every frame tells what its layout took, e.g., to show it in the
developer tools of a browser:

```cs
var frame = harness.PrepareFrame();
var metrics = frame.Metrics;

Console.WriteLine($"styles {metrics.Style}, boxes {metrics.Layout}, hit testing {metrics.HitTesting}");
Console.WriteLine($"{metrics.Elements} elements, {metrics.Boxes} boxes, {metrics.TrialLayouts} trial layouts");
```

`Total` is the sum of the phases: `Setup` (web fonts and the page's default text style), `Style`
(the render tree, i.e., matching selectors and the cascade), `Layout` (the boxes and what is painted),
`HitTesting` and `Subframes` (the documents of iframes). The counters tell how much work that was:
`StyleMaps`, `TextMeasurements`, `Commands` (of the display list), `HitTestEntries` and the
`AllocatedBytes`. A frame patched by `UpdateFrame()` is `IsIncremental`, and a frame moved with
`Reposition` needed no layout at all: its metrics are `RenderMetrics.None`.

A change of an element's state, i.e., hovering or focusing it, is often confined to it: only what
a selector of that state refers to (e.g., `.card:hover .title`) is styled again, and a change of
its colors, backgrounds, borders, outlines, shadows or text decoration is painted again without
laying out anything, so `UpdateFrame()` patches the last frame (keeping its hit-test map, unless a
`cursor` changed). Selectors that style other elements with the state, like `.tip:hover + .note`
or the rules of a shadow tree, still lay out the document again. The harness knows the hovered
element, while a host that moves the focus tells it so:

```cs
field.DoFocus();
harness.NotifyStateChanged(field);
var frame = harness.UpdateFrame() ?? harness.PrepareFrame();
```

For a large document, the computed styles of its elements are read on all cores (as AngleSharp.Css
matches their selectors), while the DOM is not changed. A host without cores to spare turns this off:

```cs
AppContext.SetSwitch("AngleSharp.Renderer.DisableParallelism", true);
AppContext.SetSwitch("AngleSharp.Css.DisableParallelism", true);
```

## The Virtual Clock

There is no real-time timer anywhere in this renderer. CSS `transition`s and `animation`s are both
measured against a virtual clock that only moves when you tell it to, via `AdvanceTime`:

```cs
harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
var midFrame = harness.PaintToPng();
```

Call `AdvanceTime` with however much time you want a frame to represent, then paint - repeating
that pair of calls is how you produce a sequence of frames (for example, to assemble a GIF or
video of an animation) instead of only ever the resting/end states.

## CSS `transition`

A `transition` animates a property when its own natural value changes for some other reason: a
`:hover` state change, or anything changing the style of an element between two frames (e.g. a
script adding a class). An element seen for the first time (just inserted, or no longer
`display: none`) has no transition, like in a browser:

```cs
var html = """
<html>
  <head>
    <style>
      #box { width: 40px; height: 40px; background-color: blue; transition: background-color 1s ease; }
      #box:hover { background-color: red; }
    </style>
  </head>
  <body><div id="box"></div></body>
</html>
""";

var harness = document.Context.GetDomHarness();
harness.MousePosition = (20, 20); // hovers #box, starts the transition
harness.AdvanceTime(TimeSpan.FromMilliseconds(500)); // halfway through the 1s duration

var midway = harness.PaintToPng(); // background is purple, not yet fully red
```

Moving the pointer away mid-transition reverses it smoothly from whatever is currently on screen,
with its own full declared duration - not a jump back to blue, and not just the remaining
fraction of the original 1s.

Like animations, transitions tell the element about themselves once a frame is painted:
`transitionrun` (when it is created), `transitionstart` (once its delay is over), `transitionend`
and `transitioncancel` (replaced or removed before it ended) are dispatched as `TransitionEvent`s,
with the `propertyName` and the `elapsedTime` in seconds. A property that is not interpolated here
(see below; e.g. `max-height` or `visibility`) still has these events, while its value changes
right away; a shorthand like `padding` or `border-color` transitions its longhands.

## CSS `animation`/`@keyframes`

An `animation` needs no trigger at all - one in the first frame of a document is pinned to
virtual-clock zero (matching a real browser's "starts playing at page load"), one an element gets
later (e.g. by a class a script adds) starts at the time of the frame that finds it, and either
keeps looping per `animation-iteration-count`:

```cs
var html = """
<html>
  <head>
    <style>
      @keyframes fade {
        0% { opacity: 0; }
        100% { opacity: 1; }
      }
      #box { width: 40px; height: 40px; background-color: blue; animation: fade 2s linear; }
    </style>
  </head>
  <body><div id="box"></div></body>
</html>
""";

var harness = document.Context.GetDomHarness();
harness.AdvanceTime(TimeSpan.FromMilliseconds(1000)); // halfway through the 2s duration

var midway = harness.PaintToPng(); // #box is 50% opaque
```

`animation-name` (including multiple, comma-separated animations on one element),
`-duration`, `-delay`, `-timing-function` (`ease`/`linear`/`cubic-bezier()`/`steps()`),
`-iteration-count` (a number, or `infinite`), `-direction` (`normal`/`reverse`/`alternate`/
`alternate-reverse`), and `-fill-mode` (`none`/`forwards`/`backwards`/`both`) are all supported.
`animation-play-state: paused` freezes an animation where it is, and the time it is paused does
not count.

The frames tell the element about its animations, like a browser: once a frame (or a picture) is
painted, `animationstart`, `animationiteration`, `animationend` and `animationcancel` (an
animation the element no longer names before it ended) are dispatched as `AnimationEvent`s, with
the `animationName` and the `elapsedTime` in seconds. With an event loop they are a task of it,
else they are dispatched right after painting. Pages often wait for `animationend` before going
on, e.g. to show the next view of a form.

```cs
box.AddEventListener("animationend", (_, ev) => Console.WriteLine(((AnimationEvent)ev).AnimationName));
harness.PrepareFrame();                            // animationstart
harness.AdvanceTime(TimeSpan.FromSeconds(2));
harness.PrepareFrame();                            // animationend: "fade"
```

## What Can Be Animated

Both `transition` and `animation` only ever interpolate a fixed set of properties:

- **Colors** (channel-wise): `background-color`, `color`, `border-top-color`,
  `border-right-color`, `border-bottom-color`, `border-left-color`
- **Numeric lengths/`opacity`** (plain numeric lerp): `opacity`, `width`, `height`, `font-size`,
  `margin-top`/`-right`/`-bottom`/`-left`, `padding-top`/`-right`/`-bottom`/`-left`,
  `border-top-width`/`-right-width`/`-bottom-width`/`-left-width`, `top`, `left`, `right`, `bottom`
- **Function lists** (function by function): `transform`, `translate`, `rotate`, `scale`,
  `filter`, `backdrop-filter`

`transition-property: all` and any other property named in a `@keyframes` block are limited to
this same list; other properties change right away (a transition of one still has its events).

## Scrolling

The same harness also tracks per-element scroll position, read and set through the standard
CSSOM-view properties/methods (`element.scrollTop`, `element.scrollTo(...)`,
`element.scrollIntoView()`, ...) - `RenderToPng`/`BuildDisplayList` honor whatever scroll position
is currently set once a harness exists for the document.
