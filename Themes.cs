// Theme table ported from the Canvas Suite design language.
//
// Source of truth is launchcanvas/public/themes.js + style.css :root. The var
// names below are the CSS names minus the --se- prefix, and each theme lists
// only its overrides, exactly as themes.js does - so this file can be diffed
// against that one when the suite palette changes. Anything a theme omits
// falls back to the classic defaults, which is what applyTheme() achieves in
// the web apps by removing the vars before setting them.
//
// C# 5 only (in-box csc). No interpolation, no null-conditional operators.

using System;
using System.Collections.Generic;
using System.Drawing;

namespace RSFind
{
    public class Theme
    {
        public string Key;
        public string Label;
        public string Group;

        public Color Panel;
        public Color Panel2;
        public Color Input;
        public Color Border;
        public Color Txt;
        public Color TxtDim;
        public Color Accent;
        public Color Active;
        public Color Up;
        public Color Down;
        public Color Warn;
        public Color Unknown;
        public Color SeriesOut;
        public Color LogoA;
        public Color LogoB;
    }

    public static class Themes
    {
        // style.css :root - every theme starts here and overrides what it names.
        const string DEFAULTS =
            "panel:#262a33 panel-2:#2d323d input:#1b1e25 border:#3a4150 " +
            "txt:#e6e9ef txt-dim:#9aa3b2 accent:#4c8bf5 active:#0066cc " +
            "up:#2e9b57 down:#d64545 warn:#d9a92f unknown:#8a8f98 " +
            "series-out:#2e9b57 logo-a:#4c8bf5 logo-b:#2e9b57";

        // Shared light-chrome status treatment: darker green/red/amber so text
        // stays legible on pale panels. '@light' expands to this, mirroring the
        // ...LIGHT_STATUS spread in themes.js.
        const string LIGHT_STATUS = "up:#1e7a43 down:#c23934 warn:#9a7415";

        static readonly List<Theme> _all = new List<Theme>();
        static readonly Dictionary<string, Theme> _byKey =
            new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);

        public static IList<Theme> All { get { return _all; } }

        static Themes()
        {
            // Authored in picker order; Group labels become submenus.
            Add("classic", "Classic", null, "");

            Add("canvas", "Canvas", "Paper",
                "panel:#ece5d3 panel-2:#e0d7c0 input:#f8f4e9 border:#c9bda0 txt:#3d362a txt-dim:#83795f " +
                "accent:#8a5a2b active:#8a5a2b @light series-out:#4f7a45 logo-a:#c49b5f logo-b:#8f6a38");
            Add("gesso", "Gesso", "Paper",
                "panel:#f4f1ea panel-2:#eae6db input:#fbf9f4 border:#d8d2c2 txt:#45403a txt-dim:#948d7c " +
                "accent:#b07040 active:#96603a @light series-out:#5f8a52 logo-a:#c9a06a logo-b:#96754a");
            Add("parchment", "Parchment", "Paper",
                "panel:#f0e9dc panel-2:#e6dcc9 input:#fffdf8 border:#d8cbb2 txt:#4a3f2f txt-dim:#7d6f58 " +
                "accent:#b5732e active:#b5732e @light series-out:#6b8a3f logo-a:#c99e5e logo-b:#94703f");
            Add("chalk", "Chalk", "Paper",
                "panel:#2b3230 panel-2:#353d3a input:#232928 border:#45504c txt:#eef0ec txt-dim:#a7b0ab " +
                "accent:#f7c948 active:#d9a92f series-out:#5a8c9e");
            Add("graphite", "Graphite", "Paper",
                "panel:#e8e8ea panel-2:#dcdce0 input:#f6f6f7 border:#c4c4c9 txt:#3a3a3e txt-dim:#86868c " +
                "accent:#5a5a62 active:#46464c @light series-out:#8a8a92");
            Add("mono", "Mono", "Paper",
                "panel:#efefef panel-2:#e4e4e4 input:#fafafa border:#cccccc txt:#222222 txt-dim:#777777 " +
                "accent:#333333 active:#000000 @light series-out:#888888");

            Add("garnet", "Garnet", "Warm",
                "panel:#33141d panel-2:#401a25 input:#240d14 border:#592433 txt:#f2e6e9 txt-dim:#c39aa5 " +
                "accent:#d9556e active:#a52238 series-out:#dcae4a down:#ff7a45");
            Add("ember", "Ember", "Warm",
                "panel:#1a1a1c panel-2:#242427 input:#101012 border:#3a3a3e txt:#ecebe9 txt-dim:#a09d99 " +
                "accent:#ff8c1a active:#d97528 series-out:#7fb26a");
            Add("rose", "Rose", "Warm",
                "panel:#2e2129 panel-2:#3b2b35 input:#211820 border:#4d3a45 txt:#f2e8ee txt-dim:#c3a3b4 " +
                "accent:#e08aa4 active:#c25f7f series-out:#96b489");
            Add("sakura", "Sakura", "Warm",
                "panel:#fbeef2 panel-2:#f6e0e8 input:#fef8fa border:#eecdd8 txt:#4a3540 txt-dim:#9a7c88 " +
                "accent:#e58aab active:#d06b90 up:#2f8a4d down:#c23934 unknown:#9a8a92 series-out:#6aa06a");
            Add("retro", "Retro", "Warm",   // Gruvbox
                "panel:#282828 panel-2:#3c3836 input:#1d2021 border:#504945 txt:#ebdbb2 txt-dim:#a89984 " +
                "accent:#fe8019 active:#d65d0e series-out:#b8bb26 up:#98971a down:#fb4934");

            Add("blueprint", "Blueprint", "Cool",
                "panel:#142c47 panel-2:#1a3a5c input:#0e2136 border:#2a4d73 txt:#dfe9f5 txt-dim:#93aecb " +
                "accent:#7fd4ff active:#2e6da4 series-out:#6be59a logo-a:#dcc296 logo-b:#b3945f");
            Add("sage", "Sage", "Cool",
                "panel:#e4ebe1 panel-2:#d6e0d1 input:#fbfdfa border:#c0cdba txt:#2f3a2e txt-dim:#5f6d5b " +
                "accent:#5b8c4f active:#4e7a44 @light series-out:#b5732e");
            Add("evergreen", "Evergreen", "Cool",
                "panel:#173726 panel-2:#1f4a33 input:#10281c border:#2c5c40 txt:#e7efe9 txt-dim:#9db8a7 " +
                "accent:#58b380 active:#2d7a4f series-out:#d9c34f");
            Add("lagoon", "Lagoon", "Cool",
                "panel:#0f3a38 panel-2:#144a47 input:#0a2827 border:#1f5c58 txt:#e4efee txt-dim:#93b5b2 " +
                "accent:#2dd4bf active:#0f766e series-out:#c9814e");
            Add("glacier", "Glacier", "Cool",
                "panel:#e9edf2 panel-2:#dbe1ea input:#ffffff border:#c3ccd8 txt:#1f2a37 txt-dim:#5c6672 " +
                "accent:#2f6fd0 active:#2f6fd0 @light series-out:#1e7a43");
            Add("slate", "Slate", "Cool",
                "panel:#23272b panel-2:#2c3136 input:#17191c border:#3d4349 txt:#e8eaec txt-dim:#9aa1a8 " +
                "accent:#7d97ad active:#546a7e");
            Add("storm", "Storm", "Cool",
                "panel:#1e2530 panel-2:#28313f input:#161c25 border:#37414f txt:#d5dce6 txt-dim:#8593a5 " +
                "accent:#6fa8dc active:#4a80b8");
            Add("arctic", "Arctic", "Cool",   // Nord
                "panel:#2e3440 panel-2:#3b4252 input:#272c36 border:#434c5e txt:#eceff4 txt-dim:#9aa5b8 " +
                "accent:#88c0d0 active:#5e81ac series-out:#a3be8c");
            Add("solarLight", "Solar Light", "Cool",   // Solarized cream
                "panel:#eee8d5 panel-2:#e3dcc4 input:#fdf6e3 border:#cfc8b0 txt:#586e75 txt-dim:#93a1a1 " +
                "accent:#268bd2 active:#2aa198 up:#859900 down:#dc322f series-out:#859900");

            Add("ink", "Ink", "Night",
                "panel:#211d1a panel-2:#2a2521 input:#161311 border:#3d362f txt:#ede7dc txt-dim:#a89e8f " +
                "accent:#c98f4e active:#96683a series-out:#8aab6d logo-a:#d3a866 logo-b:#9c7847");
            Add("midnight", "Midnight", "Night",
                "panel:#1e1b3a panel-2:#272248 input:#141126 border:#383163 txt:#eae8f4 txt-dim:#a29cc4 " +
                "accent:#8b7cf8 active:#5b4bc4 series-out:#4fc9a8");
            Add("crimsonNavy", "Crimson Navy", "Night",
                "panel:#152a52 panel-2:#1c3766 input:#0e1d3a border:#2a4576 txt:#e8ecf5 txt-dim:#9fadc9 " +
                "accent:#dc2743 active:#dc2743 series-out:#3dbf9a down:#ff7a45");
            Add("synthwave", "Synthwave", "Night",
                "panel:#1a1030 panel-2:#251643 input:#120a24 border:#3a2560 txt:#ece6ff txt-dim:#a596c9 " +
                "accent:#ff4d8d active:#d6337a series-out:#36d6e7 up:#39e58c");
            Add("nocturne", "Nocturne", "Night",   // Dracula
                "panel:#282a36 panel-2:#343746 input:#1e1f29 border:#44475a txt:#f8f8f2 txt-dim:#a3a9c9 " +
                "accent:#bd93f9 active:#ff79c6 series-out:#50fa7b");
            Add("tokyoNight", "Tokyo Night", "Night",
                "panel:#1a1b26 panel-2:#24283b input:#16161e border:#2f334d txt:#c0caf5 txt-dim:#7f88b3 " +
                "accent:#7aa2f7 active:#bb9af7 series-out:#9ece6a");
            Add("solarDark", "Solar Dark", "Night",   // Solarized dark
                "panel:#002b36 panel-2:#073642 input:#00212b border:#0d4a58 txt:#93a1a1 txt-dim:#5f7883 " +
                "accent:#2aa198 active:#268bd2 up:#859900 down:#dc322f series-out:#b58900");

            Add("phosphor", "Phosphor", "Screen",
                "panel:#0d1a0d panel-2:#12240f input:#071007 border:#1e3a1a txt:#a8f0a0 txt-dim:#5c9e57 " +
                "accent:#39ff14 active:#2bcc10 up:#39ff14 down:#ff5544 series-out:#c8f04f");
            Add("amber", "Amber", "Screen",
                "panel:#1a1206 panel-2:#241a0a input:#100b04 border:#3a2a12 txt:#ffcc66 txt-dim:#a8813c " +
                "accent:#ffb000 active:#cc8c00 up:#ffb000 down:#ff5544 series-out:#e07b39");
        }

        static void Add(string key, string label, string group, string spec)
        {
            Theme t = new Theme();
            t.Key = key;
            t.Label = label;
            t.Group = group;
            Apply(t, DEFAULTS);
            Apply(t, spec.Replace("@light", LIGHT_STATUS));
            _all.Add(t);
            _byKey[key] = t;
        }

        static void Apply(Theme t, string spec)
        {
            string[] parts = spec.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = parts[i].Substring(0, colon);
                Color c = ColorTranslator.FromHtml(parts[i].Substring(colon + 1));
                switch (name)
                {
                    case "panel": t.Panel = c; break;
                    case "panel-2": t.Panel2 = c; break;
                    case "input": t.Input = c; break;
                    case "border": t.Border = c; break;
                    case "txt": t.Txt = c; break;
                    case "txt-dim": t.TxtDim = c; break;
                    case "accent": t.Accent = c; break;
                    case "active": t.Active = c; break;
                    case "up": t.Up = c; break;
                    case "down": t.Down = c; break;
                    case "warn": t.Warn = c; break;
                    case "unknown": t.Unknown = c; break;
                    case "series-out": t.SeriesOut = c; break;
                    case "logo-a": t.LogoA = c; break;
                    case "logo-b": t.LogoB = c; break;
                }
            }
        }

        public static Theme Get(string key)
        {
            Theme t;
            if (key != null && _byKey.TryGetValue(key, out t)) return t;
            return _byKey["classic"];
        }
    }

    // The palette currently in force. Controls read Th.T when they paint and
    // repaint on Changed, so a theme switch needs no control rebuild.
    public static class Th
    {
        static Theme _current = Themes.Get("classic");

        public static event EventHandler Changed;

        public static Theme T { get { return _current; } }

        public static void Set(string key)
        {
            _current = Themes.Get(key);
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        // Matches the CSS filter: brightness(1.1) used on primary button hover.
        public static Color Brighten(Color c, double factor)
        {
            int r = (int)Math.Min(255.0, c.R * factor);
            int g = (int)Math.Min(255.0, c.G * factor);
            int b = (int)Math.Min(255.0, c.B * factor);
            return Color.FromArgb(c.A, r, g, b);
        }

        // Blend toward another color, for subtle hover fills.
        public static Color Mix(Color a, Color b, double amount)
        {
            int r = (int)(a.R + (b.R - a.R) * amount);
            int g = (int)(a.G + (b.G - a.G) * amount);
            int bl = (int)(a.B + (b.B - a.B) * amount);
            return Color.FromArgb(255, r, g, bl);
        }

        // White or near-black, whichever reads better on the given fill.
        public static Color OnColor(Color bg)
        {
            double luma = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return luma > 0.6 ? Color.FromArgb(20, 20, 20) : Color.White;
        }
    }
}
