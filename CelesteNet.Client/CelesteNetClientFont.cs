﻿using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.CelesteNet.Client
{
    // Copy of ActiveFont that always uses the English font.
    public static class CelesteNetClientFont
    {
        public static PixelFont Font
        {
            get
            {
                string face = Dialog.Languages["schinese"].FontFace;
                return Fonts.Get(face) ?? Fonts.Load(face);
            }
        }

        public static PixelFont FontEN => Fonts.Get(Dialog.Languages["english"].FontFace);

        public static PixelFontSize FontSize => Font.Get(BaseSize);

        public static PixelFontSize FontSizeEN => FontEN.Get(BaseSize);

        public static float BaseSize => Dialog.Languages["schinese"].FontFaceSize;

        public static float BaseSizeEN => Dialog.Languages["english"].FontFaceSize;

        public static float LineHeight => FontSize.LineHeight;

        public static Vector2 Measure(char text)
            => FontSize.Measure(text);

        public static Vector2 Measure(string text)
        {
            if (ShouldUseENFont(text))
                return FontSizeEN.Measure(text);
            else
                return FontSize.Measure(text);
        }

        public static float WidthToNextLine(string text, int start)
            => FontSize.WidthToNextLine(text, start);

        public static float HeightOf(string text)
            => FontSize.HeightOf(text);

        private static bool ShouldUseENFont(string text)
        {
            if (!CelesteNetClientModule.Settings.UseENFontWhenPossible) return false;

            var enFlag = true;
            foreach (var c in text)
            {
                if (c > 256)
                    enFlag = false;
            }
            return enFlag;
        }

        public static void Draw(char character, Vector2 position, Vector2 justify, Vector2 scale, Color color)
            => Font.Draw(BaseSize, character, position, justify, scale, color);

        private static void Draw(string text, Vector2 position, Vector2 justify, Vector2 scale, Color color, float edgeDepth, Color edgeColor, float stroke, Color strokeColor)
        {
            if (ShouldUseENFont(text))
                FontEN.Draw(BaseSizeEN, text, position, justify, scale, color, edgeDepth, edgeColor, stroke, strokeColor);
            else
                Font.Draw(BaseSize, text, position, justify, scale, color, edgeDepth, edgeColor, stroke, strokeColor);
        }

        public static void Draw(string text, Vector2 position, Color color)
            => Draw(text, position, Vector2.Zero, Vector2.One, color, 0f, Color.Transparent, 0f, Color.Transparent);

        public static void Draw(string text, Vector2 position, Vector2 justify, Vector2 scale, Color color)
            => Draw(text, position, justify, scale, color, 0f, Color.Transparent, 0f, Color.Transparent);

        public static void DrawOutline(string text, Vector2 position, Vector2 justify, Vector2 scale, Color color, float stroke, Color strokeColor)
            => Draw(text, position, justify, scale, color, 0f, Color.Transparent, stroke, strokeColor);

        public static void DrawEdgeOutline(string text, Vector2 position, Vector2 justify, Vector2 scale, Color color, float edgeDepth, Color edgeColor, float stroke = 0f, Color strokeColor = default)
            => Draw(text, position, justify, scale, color, edgeDepth, edgeColor, stroke, strokeColor);

    }
}
