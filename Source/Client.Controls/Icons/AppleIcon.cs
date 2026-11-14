using Avalonia.Controls;
using Avalonia.Media;

namespace Heron.ClientControls.Icons;

/// <summary>The supplied filled Apple mark, traced as a vector without its white background.
/// Inherits Foreground so theme and control states tint the entire silhouette together.</summary>
public sealed class AppleIcon : PathIcon
{
    private const string PathData =
        "M352 0 L358 0 L358 25 L355 39 L345 63 L331 83 L314 100 L301 109 L279 117 L255 118 L258 83 L265 64 L280 41 L296 25 L311 15 L332 5 Z " +
        "M337 124 L372 125 L403 134 L430 150 L450 172 L451 175 L429 191 L409 216 L399 241 L396 278 L401 307 L411 330 L420 343 L433 356 L451 369 L464 375 L461 389 L446 422 L420 463 L394 494 L372 508 L361 511 L339 511 L289 494 L271 491 L238 495 L204 508 L188 512 L173 512 L160 508 L145 499 L126 481 L107 457 L79 410 L58 355 L48 302 L48 252 L58 211 L76 177 L99 152 L115 141 L145 128 L161 125 L191 126 L222 135 L248 146 L264 147 L310 130 Z";

    public AppleIcon() => Data = Geometry.Parse(PathData);

    // Reuse PathIcon's theme/template, including Foreground binding and disabled states.
    protected override Type StyleKeyOverride => typeof(PathIcon);
}
