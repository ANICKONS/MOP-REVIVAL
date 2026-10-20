// MOP - REVIVAL
// Copyright(C) 2019-2022 Athlon (original Modern Optimization Plugin), 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
//
// Full screen loading overlay while MOP takes over the scene (rewritten in 5.1).
//
// The original MOP drew a uGUI prefab from an embedded asset bundle. This version is drawn with IMGUI and
// needs no bundle, like the MOPR 4.x screen it is modelled on: the "mopping man" logo flips between two
// frames embedded in the DLL. On top of 4.x it shows what MOP is actually doing (phase captions), an eased
// progress bar, rotating tips, fades out instead of vanishing and is scaled for any resolution.
//
// It only draws. Hypervisor freezes and releases the player; the "Update Cursor" FSM of the player is paused
// while the screen is up so the cursor stays hidden, and re-enabled when the screen goes away.

using System;
using System.IO;
using System.Reflection;
using UnityEngine;

using MOP.FSM;
using MOP.Localization;

namespace MOP.Common
{
    internal enum LoadPhase
    {
        WaitingForMods = 0,
        CheckingSatsuma = 1,
        Restarting = 2,
        Optimizing = 3,
        FinalTouches = 4,
    }

    internal class LoadScreen : MonoBehaviour
    {
        const float DotInterval = 0.4f;     // "..." animation step
        const float FrameInterval = 0.45f;  // logo frame flip
        const float TipInterval = 5.5f;     // how long a tip stays
        const float FadeTime = 0.35f;       // fade-out after loading
        const float CreepTime = 2.5f;       // time constant of the bar creep when a phase reports no progress
        const string Brand = "<b><color=#FF7A1A>MOP</color> - REVIVAL</b>";
        const float MarkScale = 1.9f;       // size of the version mark against the brand
        static readonly Color Orange = new Color(1f, 0.478f, 0.102f, 1f);       // #FF7A1A
        static readonly Color OrangeLight = new Color(1f, 0.72f, 0.38f, 1f);    // upper half of the mark
        static readonly Color OrangeDeep = new Color(0.30f, 0.09f, 0f, 1f);     // its extruded shadow

        // Share of the progress bar for each phase (same order as LoadPhase).
        static readonly float[] PhaseStart = { 0.00f, 0.40f, 0.40f, 0.50f, 0.85f };
        static readonly float[] PhaseEnd = { 0.40f, 0.50f, 0.50f, 0.85f, 1.00f };
        static readonly string[] PhaseKeys = { "load.waiting_mods", "load.checking_satsuma", "load.restarting", "load.optimizing", "load.final_touches" };

        // April Fools gibberish (deliberate nonsense, not localized).
        static readonly string[] AprilFools =
        {
            "MOADING LOP...", "LOADINP MOG...", "...POM GNIDAOL", "LDNG MP...",
            "WOADING MOPR UWU", "MOPR?", "REVIVING MOP...", "UNLOADING MOPR...",
            "ŁADOWANIE MOPR...", "MOPR LADEN...", "LADATAAN MOPR...", "ЗАГРУЖАЕМ ШВАБРУ...",
        };

        Texture2D background, pixel;
        Texture2D[] frames;
        GUIStyle brandStyle, markStyle, captionStyle, tipStyle, percentStyle;
        GUIContent brandContent, markContent;
        int styledHeight = -1;

        PlayMakerFSM cursorFsm;
        bool visible, fading;
        float alpha = 1f;

        LoadPhase phase = LoadPhase.WaitingForMods;
        float phaseProgress = -1f; // explicit progress inside the phase, -1 = unknown (bar creeps)
        float phaseTime;
        float shownProgress;

        string overrideCaption; // April Fools / easter egg: replaces the phase caption
        float dotTimer, frameTimer, tipTimer;
        int dots, frame, tip;

        /// <summary>Creates and shows the loading screen.</summary>
        internal static LoadScreen Show()
        {
            GameObject go = new GameObject("MOP_LoadScreen");
            LoadScreen screen = go.AddComponent<LoadScreen>();
            screen.Activate();
            return screen;
        }

        void Awake()
        {
            background = SolidTexture(new Color(0f, 0f, 0f, 0.99f));
            pixel = SolidTexture(Color.white);
            frames = new[] { LoadEmbedded("MOPR.Resources.loadScreen1.png"), LoadEmbedded("MOPR.Resources.loadScreen2.png") };

            try
            {
                GameObject player = GameObject.Find("PLAYER");
                if (player != null)
                    cursorFsm = player.GetPlayMaker("Update Cursor");
            }
            catch (Exception ex)
            {
                ModConsole.Debug("Load screen: cursor FSM not found (" + ex.Message + ")");
            }

            ChooseOverrideCaption();
            tip = UnityEngine.Random.Range(0, StringsEn.TipCount);

            // The version mark is the major version ("5" for 5.x).
            string version = MOP.ModVersion ?? "";
            int dot = version.IndexOf('.');
            brandContent = new GUIContent(Brand);
            markContent = new GUIContent(dot > 0 ? version.Substring(0, dot) : "5");
        }

        void Activate()
        {
            visible = true;
            fading = false;
            alpha = 1f;
            Cursor.visible = false;
            if (cursorFsm != null)
                cursorFsm.enabled = false;
        }

        /// <summary>Moves to a new loading phase. The bar never goes backwards.</summary>
        internal void SetPhase(LoadPhase newPhase)
        {
            phase = newPhase;
            phaseProgress = -1f;
            phaseTime = 0f;
        }

        /// <summary>Progress inside the current phase (0..1).</summary>
        internal void SetProgress(float fraction)
        {
            phaseProgress = Mathf.Clamp01(fraction);
        }

        /// <summary>Hides the screen with a short fade and destroys it. Safe to call more than once.</summary>
        internal void Deactivate()
        {
            if (!visible || fading)
                return;

            fading = true;
            phase = LoadPhase.FinalTouches;
            phaseProgress = 1f;
            if (cursorFsm != null)
                cursorFsm.enabled = true;
        }

        void Update()
        {
            if (!visible)
                return;

            // Unscaled: the game may pause time while loading.
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.25f) dt = 0.25f; // a long hitch (Initialize) should not skip the whole animation

            dotTimer += dt;
            if (dotTimer >= DotInterval)
            {
                dotTimer = 0f;
                dots = (dots + 1) % 4;
            }

            frameTimer += dt;
            if (frameTimer >= FrameInterval)
            {
                frameTimer = 0f;
                frame ^= 1;
            }

            tipTimer += dt;
            if (tipTimer >= TipInterval)
            {
                tipTimer = 0f;
                tip = (tip + 1) % StringsEn.TipCount;
            }

            phaseTime += dt;
            int p = (int)phase;
            float within = phaseProgress >= 0f ? phaseProgress : 0.9f * (1f - Mathf.Exp(-phaseTime / CreepTime));
            float target = Mathf.Lerp(PhaseStart[p], PhaseEnd[p], within);
            if (target > shownProgress)
                shownProgress += (target - shownProgress) * (1f - Mathf.Exp(-dt * 6f));

            if (fading)
            {
                alpha -= dt / FadeTime;
                if (alpha <= 0f)
                {
                    visible = false;
                    Destroy(gameObject);
                }
            }
        }

        void OnGUI()
        {
            if (!visible || Event.current.type != EventType.Repaint)
                return;

            GUI.depth = -1000; // above every other IMGUI overlay
            EnsureStyles();

            Color old = GUI.color;
            float w = Screen.width, h = Screen.height;
            float a = Mathf.Clamp01(alpha);

            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), background, ScaleMode.StretchToFill);

            // Brand and the version mark.
            DrawBrand(w, h, a);

            // Logo.
            float logoSize = Mathf.Min(h * 0.42f, w * 0.6f);
            float logoY = h * 0.5f - logoSize * 0.62f;
            Texture2D logo = frames[frame] ?? frames[frame ^ 1];
            if (logo != null)
                GUI.DrawTexture(new Rect((w - logoSize) * 0.5f, logoY, logoSize, logoSize), logo, ScaleMode.ScaleToFit);

            // Caption.
            float captionY = logoY + logoSize + h * 0.01f;
            string caption = overrideCaption ?? (Loc.T(PhaseKeys[(int)phase]) + new string('.', dots));
            GUI.Label(new Rect(0f, captionY, w, h * 0.06f), caption, captionStyle);

            // Progress bar.
            float barW = Mathf.Min(w * 0.36f, h * 0.75f);
            float barH = Mathf.Max(4f, h * 0.008f);
            float barX = (w - barW) * 0.5f;
            float barY = captionY + h * 0.07f;
            GUI.color = new Color(1f, 1f, 1f, 0.12f * a);
            GUI.DrawTexture(new Rect(barX, barY, barW, barH), pixel);
            GUI.color = new Color(1f, 0.478f, 0.102f, a); // #FF7A1A
            GUI.DrawTexture(new Rect(barX, barY, barW * Mathf.Clamp01(shownProgress), barH), pixel);
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.Label(new Rect(barX, barY + barH + 2f, barW, h * 0.035f), Mathf.RoundToInt(shownProgress * 100f) + "%", percentStyle);

            // Tip.
            string tipText = Loc.T("load.tip_prefix") + Loc.T("load.tip." + tip);
            GUI.Label(new Rect(w * 0.1f, h * 0.88f, w * 0.8f, h * 0.08f), tipText, tipStyle);

            GUI.color = old;
        }

        /// <summary>
        /// "MOP - REVIVAL" and after it a large slanted "5" (5.1): orange with a lighter upper half, an extruded
        /// dark shadow and a faint glow, set a little higher than the brand. Both are centered as one line.
        /// </summary>
        void DrawBrand(float w, float h, float a)
        {
            Vector2 brand = brandStyle.CalcSize(brandContent);
            Vector2 mark = markStyle.CalcSize(markContent);
            float slant = markStyle.fontSize * 0.25f;      // italic overhang on the right (CalcSize leaves it out)
            float gap = brandStyle.fontSize * 0.4f;
            float centerY = h * 0.14f;
            float x = Mathf.Round((w - (brand.x + gap + mark.x + slant)) * 0.5f);

            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.Label(new Rect(x, Mathf.Round(centerY - brand.y * 0.5f), brand.x + 2f, brand.y), brandContent, brandStyle);

            Rect r = new Rect(Mathf.Round(x + brand.x + gap), Mathf.Round(centerY - mark.y * 0.5f - brandStyle.fontSize * 0.25f),
                              mark.x + slant, mark.y);
            float u = Mathf.Max(1f, Mathf.Round(markStyle.fontSize * 0.025f));

            // Glow: the mark around itself, faint.
            GUI.color = new Color(Orange.r, Orange.g, Orange.b, 0.06f * a);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * Mathf.PI / 6f;
                GUI.Label(Shift(r, Mathf.Cos(angle) * u * 4f, Mathf.Sin(angle) * u * 4f), markContent, markStyle);
            }

            // Extruded shadow, down and to the right.
            GUI.color = new Color(OrangeDeep.r, OrangeDeep.g, OrangeDeep.b, 0.95f * a);
            for (int i = 3; i >= 1; i--)
                GUI.Label(Shift(r, u * i, u * i), markContent, markStyle);

            // Face, then the lighter upper half on top of it (clipped by a group).
            GUI.color = new Color(Orange.r, Orange.g, Orange.b, a);
            GUI.Label(r, markContent, markStyle);
            GUI.BeginGroup(new Rect(r.x, r.y, r.width, Mathf.Round(r.height * 0.5f)));
            GUI.color = new Color(OrangeLight.r, OrangeLight.g, OrangeLight.b, a);
            GUI.Label(new Rect(0f, 0f, r.width, r.height), markContent, markStyle);
            GUI.EndGroup();

            GUI.color = new Color(1f, 1f, 1f, a);
        }

        static Rect Shift(Rect r, float dx, float dy) => new Rect(r.x + dx, r.y + dy, r.width, r.height);

        void OnDestroy()
        {
            // The screen can be destroyed by a scene change while still visible: never leave the cursor FSM off.
            if (cursorFsm != null && !cursorFsm.enabled)
                cursorFsm.enabled = true;

            if (background != null) Destroy(background);
            if (pixel != null) Destroy(pixel);
            if (frames != null)
                for (int i = 0; i < frames.Length; i++)
                    if (frames[i] != null)
                        Destroy(frames[i]);
        }

        void EnsureStyles()
        {
            int h = Screen.height;
            if (brandStyle != null && styledHeight == h)
                return;
            styledHeight = h;

            brandStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                richText = true,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(h * 0.06f), 24, 96),
            };
            brandStyle.normal.textColor = Color.white;

            // The version mark: white, tinted with GUI.color when drawn.
            markStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.BoldAndItalic,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                fontSize = Mathf.RoundToInt(brandStyle.fontSize * MarkScale),
            };
            markStyle.normal.textColor = Color.white;

            captionStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                richText = true,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(h * 0.032f), 16, 52),
            };
            captionStyle.normal.textColor = new Color(0.85f, 0.88f, 0.92f, 1f);

            percentStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(h * 0.018f), 11, 28),
            };
            percentStyle.normal.textColor = new Color(0.6f, 0.65f, 0.7f, 1f);

            tipStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                richText = true,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(h * 0.021f), 12, 34),
            };
            tipStyle.normal.textColor = new Color(0.6f, 0.65f, 0.7f, 1f);
        }

        /// <summary>Usually the phase captions; 1% of the time a friendly easter egg; on April 1st gibberish.</summary>
        void ChooseOverrideCaption()
        {
            DateTime today = DateTime.Today;
            if (today.Day == 1 && today.Month == 4)
            {
                overrideCaption = AprilFools[UnityEngine.Random.Range(0, AprilFools.Length)];
                return;
            }

            if (UnityEngine.Random.Range(0, 100) == 0)
                overrideCaption = Loc.T("load.easter");
        }

        static Texture2D SolidTexture(Color color)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, color);
            t.Apply();
            return t;
        }

        /// <summary>Loads a PNG embedded in the mod DLL (null on any error).</summary>
        static Texture2D LoadEmbedded(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return null;

                    byte[] data = new byte[stream.Length];
                    int offset = 0, read;
                    while (offset < data.Length && (read = stream.Read(data, offset, data.Length - offset)) > 0)
                        offset += read;

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(data))
                    {
                        Destroy(texture);
                        return null;
                    }
                    texture.filterMode = FilterMode.Bilinear;
                    texture.wrapMode = TextureWrapMode.Clamp;
                    return texture;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
