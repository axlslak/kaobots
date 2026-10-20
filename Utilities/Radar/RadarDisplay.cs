using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace Radar
{
    // All geometry is drawn in display pixels, then converted to the camera's
    // actual view plane. Add2DLine itself does not accept pixel coordinates.
    internal sealed class RadarDisplay
    {
        private const float RangeMetres = 100;
        private const int CircleSegments = 128;
        private static readonly Vector2[] Circle = CreateCircle();
        private static readonly Vector3 White = new Vector3(1, 1, 1);
        private static readonly Vector3 Blue = new Vector3(0.2f, 0.5f, 1);
        private static readonly Vector3 Yellow = new Vector3(1, 1, 0);
        private static readonly Vector3 Red = new Vector3(1, 0, 0);
        private readonly HashSet<Identity> _entities = new HashSet<Identity>();
        private readonly ScreenLines _lines = new ScreenLines();
        private float _forwardX;
        private float _forwardZ = 1;

        public void SetEntities(IEnumerable<Identity> entities)
        {
            _entities.Clear();
            foreach (Identity identity in entities)
                _entities.Add(identity);
        }

        public void Clear()
        {
            _entities.Clear();
            _forwardX = 0;
            _forwardZ = 1;
        }

        public void Draw(Vector2 screen, Identity localIdentity, Vector3 localPosition,
            Vector3 localForward, Identity selectedEntity)
        {
            if (!Finite(screen.X) || !Finite(screen.Y) || screen.X <= 0 || screen.Y <= 0)
                return;

            // Reserve 50% of the height for the complete radar, including the
            // outside letters. At 1080p the outer circle is 500 pixels across.
            float radius = Math.Min(screen.Y * 0.25f - 20, screen.X * 0.5f - 20);
            if (radius < 20 || !_lines.Begin(screen))
                return;

            float fx = localForward.X, fz = localForward.Z;
            if (Normalize(ref fx, ref fz))
            {
                _forwardX = fx;
                _forwardZ = fz;
            }
            float cx = screen.X * 0.5f, cy = screen.Y * 0.5f;
            for (int ring = 1; ring <= 5; ring++)
            {
                float r = radius * ring / 5;
                for (int i = 0; i < CircleSegments; i++)
                    _lines.Line(cx + Circle[i].X * r, cy + Circle[i].Y * r,
                        cx + Circle[i + 1].X * r, cy + Circle[i + 1].Y * r, White);
            }

            // World north is +Z and east is +X. The letters stay upright while
            // their positions rotate with the local toon's facing, not the camera.
            Cardinal('N', 0, 1, cx, cy, radius);
            Cardinal('E', 1, 0, cx, cy, radius);
            Cardinal('S', 0, -1, cx, cy, radius);
            Cardinal('W', -1, 0, cx, cy, radius);

            // Cache only filter membership. Read current loaded poses every
            // engine update, so movement/turning does not step at the UI rate.
            Dynel selected = null;
            foreach (var entity in DynelManager.AllDynels)
            {
                if (entity.Identity == localIdentity || !_entities.Contains(entity.Identity) || !entity.IsValid)
                    continue;
                if (entity.Identity == selectedEntity)
                    selected = entity;
                else
                    Entity(entity, localPosition, cx, cy, radius, Yellow);
            }
            if (selected != null && selected.IsValid)
                Entity(selected, localPosition, cx, cy, radius, Red);

            // Draw self last: always blue, central and pointing straight up.
            Arrow(cx, cy, 0, -1, Blue);
        }

        private void Entity(Dynel entity, Vector3 local, float cx, float cy, float radius, Vector3 color)
        {
            Vector3 position = entity.GlobalPosition;
            float dx = position.X - local.X, dz = position.Z - local.Z;
            float distanceSquared = dx * dx + dz * dz;
            if (!Finite(distanceSquared) || distanceSquared > RangeMetres * RangeMetres)
                return;

            float scale = radius / RangeMetres;
            float x = cx + (dx * _forwardZ - dz * _forwardX) * scale;
            float y = cy - (dx * _forwardX + dz * _forwardZ) * scale;
            if (entity.Identity.Type == IdentityType.SimpleChar)
            {
                Vector3 forward = entity.Forward;
                float fx = forward.X, fz = forward.Z;
                if (Normalize(ref fx, ref fz))
                {
                    Arrow(x, y, fx * _forwardZ - fz * _forwardX,
                        -(fx * _forwardX + fz * _forwardZ), color);
                    return;
                }
            }

            // Objects have no useful facing. A missing/vertical character
            // heading must not hide an otherwise valid position either.
            _lines.Line(x, y - 4, x + 4, y, color);
            _lines.Line(x + 4, y, x, y + 4, color);
            _lines.Line(x, y + 4, x - 4, y, color);
            _lines.Line(x - 4, y, x, y - 4, color);
        }

        private void Arrow(float x, float y, float dx, float dy, Vector3 color)
        {
            float tipX = x + dx * 6, tipY = y + dy * 6;
            _lines.Line(x - dx * 5, y - dy * 5, tipX, tipY, color);
            _lines.Line(tipX, tipY, tipX - dx * 4 - dy * 3, tipY - dy * 4 + dx * 3, color);
            _lines.Line(tipX, tipY, tipX - dx * 4 + dy * 3, tipY - dy * 4 - dx * 3, color);
        }

        private void Cardinal(char letter, float dx, float dz, float cx, float cy, float radius)
        {
            float x = cx + (dx * _forwardZ - dz * _forwardX) * (radius + 13) - 4;
            float y = cy - (dx * _forwardX + dz * _forwardZ) * (radius + 13) - 6;
            // Small 8x12 line letters avoid backgrounds, extra windows and GUI
            // layout changes. Their bounds fit outside the circle with a gap.
            switch (letter)
            {
                case 'N':
                    Stroke(x, y, 0, 12, 0, 0);
                    Stroke(x, y, 0, 0, 8, 12);
                    Stroke(x, y, 8, 12, 8, 0);
                    break;
                case 'E':
                    Stroke(x, y, 8, 0, 0, 0);
                    Stroke(x, y, 0, 0, 0, 12);
                    Stroke(x, y, 0, 12, 8, 12);
                    Stroke(x, y, 0, 6, 6, 6);
                    break;
                case 'S':
                    Stroke(x, y, 8, 0, 0, 0);
                    Stroke(x, y, 0, 0, 0, 6);
                    Stroke(x, y, 0, 6, 8, 6);
                    Stroke(x, y, 8, 6, 8, 12);
                    Stroke(x, y, 8, 12, 0, 12);
                    break;
                case 'W':
                    Stroke(x, y, 0, 0, 2, 12);
                    Stroke(x, y, 2, 12, 4, 5);
                    Stroke(x, y, 4, 5, 6, 12);
                    Stroke(x, y, 6, 12, 8, 0);
                    break;
            }
        }

        private void Stroke(float x, float y, float x1, float y1, float x2, float y2)
        {
            _lines.Line(x + x1, y + y1, x + x2, y + y2, White);
        }

        private static Vector2[] CreateCircle()
        {
            var circle = new Vector2[CircleSegments + 1];
            for (int i = 0; i < CircleSegments; i++)
            {
                double angle = i * (Math.PI * 2 / CircleSegments);
                circle[i] = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            }
            circle[CircleSegments] = circle[0];
            return circle;
        }

        private static bool Normalize(ref float x, ref float z)
        {
            float lengthSquared = x * x + z * z;
            if (!Finite(lengthSquared) || lengthSquared < 0.000001f)
                return false;
            float inverseLength = 1 / (float)Math.Sqrt(lengthSquared);
            x *= inverseLength;
            z *= inverseLength;
            return true;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private sealed class ScreenLines
        {
            private const string CameraExport = "?m_spcCurrentCamera@RRefFrame_t@@2PAVRCamera_t@@A";
            private const string ViewWindowExport = "?GetViewPlaneWindow@RCamera_t@@QBEXAAM000@Z";
            private const string LineExport = "?Add2DLine@Debugger_t@@QAEXVVector3_t@@0MMM_N@Z";
            private IntPtr _cameraAddress;
            private IntPtr _debugger;
            private float _left, _top, _scaleX, _scaleY;

            public bool Begin(Vector2 screen)
            {
                if (_cameraAddress == IntPtr.Zero)
                {
                    IntPtr module = GetModuleHandle("Randy31.dll");
                    if (module == IntPtr.Zero || GetProcAddress(module, LineExport) == IntPtr.Zero ||
                        GetProcAddress(module, ViewWindowExport) == IntPtr.Zero)
                        throw new NotSupportedException("The client does not expose Radar's 2D drawing functions.");
                    _cameraAddress = GetProcAddress(module, CameraExport);
                    if (_cameraAddress == IntPtr.Zero)
                        throw new NotSupportedException("The client does not expose Radar's current camera.");
                }

                // Resolve the current camera each frame; never retain the camera
                // object across zoning. The export is a pointer variable, not code.
                IntPtr camera = Marshal.ReadIntPtr(_cameraAddress);
                if (camera == IntPtr.Zero)
                    return false;
                float right, bottom;
                GetViewPlaneWindow(camera, out _left, out _top, out right, out bottom);
                if (!Finite(_left) || !Finite(_top) || !Finite(right) || !Finite(bottom) ||
                    right <= _left || _top <= bottom)
                    return false;
                _debugger = GetDebugger();
                if (_debugger == IntPtr.Zero)
                    return false;

                // Standard full-display game viewport: top-left UI pixels map
                // to the view plane at z=1. Reading the frustum keeps circles
                // circular at different aspect ratios and camera FOVs.
                _scaleX = (right - _left) / screen.X;
                _scaleY = (bottom - _top) / screen.Y;
                return true;
            }

            public void Line(float x1, float y1, float x2, float y2, Vector3 color)
            {
                Add2DLine(_debugger, _left + x1 * _scaleX, _top + y1 * _scaleY, 1,
                    _left + x2 * _scaleX, _top + y2 * _scaleY, 1,
                    color.X, color.Y, color.Z, false);
            }

            [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
            private static extern IntPtr GetModuleHandle(string name);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);

            [DllImport("Randy31.dll", EntryPoint = "?Get@Debugger_t@@SAPAV1@XZ", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr GetDebugger();

            [DllImport("Randy31.dll", EntryPoint = ViewWindowExport, CallingConvention = CallingConvention.ThisCall)]
            private static extern void GetViewPlaneWindow(IntPtr camera,
                out float left, out float top, out float right, out float bottom);

            // Same native extension used by Mali's Dungeon Map. The decorated
            // export returns void; its two Vector3 arguments are passed by value.
            [DllImport("Randy31.dll", EntryPoint = LineExport, CallingConvention = CallingConvention.ThisCall)]
            private static extern void Add2DLine(IntPtr debugger,
                float x1, float y1, float z1, float x2, float y2, float z2,
                float red, float green, float blue, [MarshalAs(UnmanagedType.I1)] bool scaleY);
        }
    }
}
