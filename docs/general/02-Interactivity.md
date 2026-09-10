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

A `transition` animates a property when its own natural value changes for some other reason - in
this renderer, that means a `:hover` state change:

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

## CSS `animation`/`@keyframes`

An `animation` needs no trigger at all - it starts the moment the harness first becomes aware of
it (pinned to virtual-clock zero, matching a real browser's "starts playing at page load") and
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
`animation-play-state` (pausing) is not - every animation the harness knows about is always
running.

## What Can Be Animated

Both `transition` and `animation` only ever interpolate a fixed set of properties:

- **Colors** (channel-wise): `background-color`, `color`, `border-top-color`,
  `border-right-color`, `border-bottom-color`, `border-left-color`
- **Numeric lengths/`opacity`** (plain numeric lerp): `opacity`, `width`, `height`, `font-size`,
  `margin-top`/`-right`/`-bottom`/`-left`, `padding-top`/`-right`/`-bottom`/`-left`,
  `border-top-width`/`-right-width`/`-bottom-width`/`-left-width`, `top`, `left`, `right`, `bottom`

`transition-property: all` and any other property named in a `@keyframes` block are limited to
this same list - properties this renderer does not otherwise resolve component-wise, such as
`transform` and `filter`, are not interpolated.

## Scrolling

The same harness also tracks per-element scroll position, read and set through the standard
CSSOM-view properties/methods (`element.scrollTop`, `element.scrollTo(...)`,
`element.scrollIntoView()`, ...) - `RenderToPng`/`BuildDisplayList` honor whatever scroll position
is currently set once a harness exists for the document.
