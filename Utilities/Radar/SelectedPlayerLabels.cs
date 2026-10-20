using System;
using System.Globalization;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace Radar
{
    internal sealed class SelectedPlayerLabels
    {
        private const int UnsetStat = 1234567890;
        private const int RemoteMaxNanoPlaceholder = 10;
        private const float GlyphHeight = 0.24f;
        private const float GlyphWidth = 0.14f;
        private const float Tracking = 0.04f;
        private const float CircleGap = 0.12f;
        private const float RowGap = 0.10f;
        private static readonly Vector3 NanoBlue = new Vector3(0.2f, 0.5f, 1);
        private static readonly int[] DigitMasks =
        {
            0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F
        };
        private static readonly Stroke[] Segments =
        {
            new Stroke(0, 0, 1, 0), new Stroke(1, 0, 1, 0.5f),
            new Stroke(1, 0.5f, 1, 1), new Stroke(1, 1, 0, 1),
            new Stroke(0, 1, 0, 0.5f), new Stroke(0, 0.5f, 0, 0),
            new Stroke(0, 0.5f, 1, 0.5f)
        };
        private readonly CameraAxes _camera = new CameraAxes();

        public void Draw(SimpleChar player, Vector3 position, float radius)
        {
            Vector3 right, up;
            if (!Finite(position) || !Finite(radius) || radius <= 0 || !_camera.TryGet(out right, out up))
                return;

            // Keep both complete rows above the circle's visible top, with HP
            // above nano. Include glyph height because the strokes extend down.
            Vector3 centre = position + new Vector3(0, radius, 0);
            Vector3 nanoTop = centre + up * (radius + CircleGap + GlyphHeight);
            Vector3 hpTop = nanoTop + up * (GlyphHeight + RowGap);
            DrawRow(FormatPool(player.Health, player.MaxHealth, false), hpTop, right, up, DebuggingColor.Red);
            DrawRow(FormatPool(player.Nano, player.MaxNano, true),
                nanoTop, right, up, NanoBlue);
        }

        private static string FormatPool(int current, int maximum, bool nano)
        {
            bool currentKnown = current >= 0 && current < UnsetStat;
            bool maximumKnown = maximum > 0 && maximum < UnsetStat &&
                (!currentKnown || current <= maximum) && (!nano || maximum != RemoteMaxNanoPlaceholder);
            // Other players' MaxNano starts at 10 even without actual pool data.
            // Show the remaining valid cache values without inventing a maximum.
            return (currentKnown ? current.ToString(CultureInfo.InvariantCulture) : "?") + " / " +
                (maximumKnown ? maximum.ToString(CultureInfo.InvariantCulture) : "?");
        }

        private static void DrawRow(string text, Vector3 top, Vector3 right, Vector3 up, Vector3 color)
        {
            float width = 0;
            foreach (char symbol in text)
                width += Width(symbol) + Tracking;
            width = Math.Max(0, width - Tracking);

            // Each imaginary row rectangle is centred independently. The font
            // has a fixed world size; normal perspective supplies distance scale.
            Vector3 origin = top - right * (width * 0.5f);
            foreach (char symbol in text)
            {
                if (symbol >= '0' && symbol <= '9')
                {
                    int mask = DigitMasks[symbol - '0'];
                    for (int i = 0; i < Segments.Length; i++)
                        if ((mask & (1 << i)) != 0)
                            DrawStroke(origin, right, up, color, Segments[i]);
                }
                else if (symbol == '/')
                    DrawStroke(origin, right, up, color, new Stroke(0, 1, 1, 0));
                else if (symbol == '?')
                {
                    DrawStroke(origin, right, up, color, new Stroke(0.1f, 0.1f, 0.3f, 0));
                    DrawStroke(origin, right, up, color, new Stroke(0.3f, 0, 0.85f, 0));
                    DrawStroke(origin, right, up, color, new Stroke(0.85f, 0, 1, 0.2f));
                    DrawStroke(origin, right, up, color, new Stroke(1, 0.2f, 0.8f, 0.4f));
                    DrawStroke(origin, right, up, color, new Stroke(0.8f, 0.4f, 0.5f, 0.55f));
                    DrawStroke(origin, right, up, color, new Stroke(0.5f, 0.55f, 0.5f, 0.7f));
                    DrawStroke(origin, right, up, color, new Stroke(0.5f, 0.9f, 0.5f, 1));
                }
                origin += right * (Width(symbol) + Tracking);
            }
        }

        private static float Width(char symbol)
        {
            return symbol == ' ' ? GlyphWidth * 0.5f : GlyphWidth;
        }

        private static void DrawStroke(Vector3 origin, Vector3 right, Vector3 up, Vector3 color, Stroke stroke)
        {
            Vector3 from = origin + right * (stroke.X1 * GlyphWidth) - up * (stroke.Y1 * GlyphHeight);
            Vector3 to = origin + right * (stroke.X2 * GlyphWidth) - up * (stroke.Y2 * GlyphHeight);
            AOSharp.Core.Debug.DrawLine(from, to, color);
        }

        private static bool Normalize(ref Vector3 vector)
        {
            float lengthSquared = vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z;
            if (!Finite(lengthSquared) || lengthSquared < 0.000001f)
                return false;
            vector *= 1 / (float)Math.Sqrt(lengthSquared);
            return true;
        }

        private static bool Finite(Vector3 vector)
        {
            return Finite(vector.X) && Finite(vector.Y) && Finite(vector.Z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private struct Stroke
        {
            public readonly float X1, Y1, X2, Y2;

            public Stroke(float x1, float y1, float x2, float y2)
            {
                X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
            }
        }

        private sealed class CameraAxes
        {
            private const string CameraExport = "?m_spcCurrentCamera@RRefFrame_t@@2PAVRCamera_t@@A";
            private const string MatrixExport = "?GetWorldMatrix@RRefFrame_t@@QBEABVTMatrix4_t@@XZ";
            private readonly float[] _matrix = new float[16];
            private IntPtr _cameraAddress;

            public bool TryGet(out Vector3 right, out Vector3 up)
            {
                right = Vector3.Right;
                up = Vector3.Up;
                if (_cameraAddress == IntPtr.Zero)
                {
                    IntPtr module = GetModuleHandle("Randy31.dll");
                    if (module == IntPtr.Zero || GetProcAddress(module, MatrixExport) == IntPtr.Zero)
                        throw new NotSupportedException("The client does not expose Radar's camera world matrix.");
                    _cameraAddress = GetProcAddress(module, CameraExport);
                    if (_cameraAddress == IntPtr.Zero)
                        throw new NotSupportedException("The client does not expose Radar's current camera.");
                }

                // This is the render camera used by the radar, not N3 Camera.Pointer.
                // Reacquire it each frame; the matrix return is a borrowed pointer.
                IntPtr camera = Marshal.ReadIntPtr(_cameraAddress);
                if (camera == IntPtr.Zero)
                    return false;
                IntPtr matrix = GetWorldMatrix(camera);
                if (matrix == IntPtr.Zero)
                    return false;
                Marshal.Copy(matrix, _matrix, 0, _matrix.Length);
                // Row-vector D3D world matrix: row 0 is right, row 1 is up.
                right = new Vector3(_matrix[0], _matrix[1], _matrix[2]);
                up = new Vector3(_matrix[4], _matrix[5], _matrix[6]);
                return Normalize(ref right) && Normalize(ref up);
            }

            [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern IntPtr GetModuleHandle(string name);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);

            [DllImport("Randy31.dll", EntryPoint = MatrixExport, CallingConvention = CallingConvention.ThisCall)]
            private static extern IntPtr GetWorldMatrix(IntPtr frame);
        }
    }
}
