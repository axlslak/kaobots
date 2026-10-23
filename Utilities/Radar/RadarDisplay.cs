using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOSharp.Common.GameData;
using AOSharp.Core;
using SmokeLounge.AOtomation.Messaging.Messages;

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
        private static readonly Vector3 Green = new Vector3(0.25f, 1, 0.35f);
        private static readonly Vector3 Pink = new Vector3(1, 0.45f, 0.8f);
        private static readonly Vector3 Red = new Vector3(1, 0, 0);
        private static readonly Vector3 CombatColor = new Vector3(1, 0.78f, 0.35f);
        private static readonly Vector3 OpponentColor = new Vector3(0.5f, 0.5f, 0.4f);
        private readonly HashSet<Identity> _entities = new HashSet<Identity>();
        private readonly HashSet<Identity> _team = new HashSet<Identity>();
        private readonly HashSet<Identity> _raid = new HashSet<Identity>();
        private readonly HashSet<Identity> _pets = new HashSet<Identity>();
        private bool _relationshipColors;
        private readonly RadarCombat _combat = new RadarCombat();
        private readonly HashSet<Identity> _combatWanted = new HashSet<Identity>();
        private readonly HashSet<Identity> _combatContext = new HashSet<Identity>();
        private readonly Dictionary<Identity, Dynel> _combatLoaded = new Dictionary<Identity, Dynel>();
        private readonly Dictionary<Identity, Marker> _combatMarkers = new Dictionary<Identity, Marker>();
        private readonly ScreenLines _lines = new ScreenLines();
        private bool _overview = true;
        private float _forwardX;
        private float _forwardZ = 1;

        public void SetEntities(IEnumerable<Identity> entities, bool overview)
        {
            _entities.Clear();
            foreach (Identity identity in entities)
                _entities.Add(identity);
            _overview = overview;
        }

        public void SetRelationships(RadarRoster roster, IEnumerable<RadarEntity> entities, bool enabled)
        {
            _team.Clear();
            _raid.Clear();
            _pets.Clear();
            foreach (Identity identity in roster.TeamIdentities)
                _team.Add(identity);
            foreach (Identity identity in roster.RaidIdentities)
                _raid.Add(identity);
            foreach (RadarEntity entity in entities)
                if (entity.Kind == RadarEntityKind.Pet)
                    _pets.Add(entity.Identity);
            _relationshipColors = enabled;
        }

        public void ObserveCombat(N3Message message, long now) => _combat.Observe(message, now);
        public void ExpireCombat(long now) => _combat.Expire(now);
        public void ClearCombat()
        {
            _combat.Clear();
            _combatWanted.Clear();
            _combatContext.Clear();
            _combatLoaded.Clear();
            _combatMarkers.Clear();
        }

        public void Clear()
        {
            _entities.Clear();
            _team.Clear();
            _raid.Clear();
            _pets.Clear();
            _relationshipColors = false;
            _overview = true;
            ClearCombat();
            _forwardX = 0;
            _forwardZ = 1;
        }

        public void Draw(Vector2 screen, Identity localIdentity, Vector3 localPosition,
            Vector3 localForward, Identity selectedEntity, RadarViewport viewport, IEnumerable<RadarEditor> editors, long now)
        {
            _combat.Expire(now);
            if (!Finite(screen.X) || !Finite(screen.Y) || screen.X <= 0 || screen.Y <= 0)
                return;

            // The default still reserves half the screen height, including the
            // compass. Editing changes the display footprint, never the 100m range.
            float radius = viewport.Size * 0.5f - 20;
            if (radius < 20 || !_lines.Begin(screen))
                return;

            float fx = localForward.X, fz = localForward.Z;
            if (Normalize(ref fx, ref fz))
            {
                _forwardX = fx;
                _forwardZ = fz;
            }
            float cx = viewport.CenterX, cy = viewport.CenterY;
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

            // Tiny moving strokes stay below the familiar arrows and edit chrome.
            if (_combat.Count > 0)
                DrawCombat(localIdentity, selectedEntity, localPosition, cx, cy, radius, now);

            // Cache only filter membership. Read current loaded poses every
            // engine update, so movement/turning does not step at the UI rate.
            // These wrappers already belong to this frame's AllDynels snapshot;
            // VehiclePointer is the remaining SDK IsValid condition. Avoid its
            // redundant full-list search for every marker in the overview.
            Dynel selected = null;
            foreach (var entity in DynelManager.AllDynels)
            {
                if (entity.Identity == localIdentity ||
                    (!_overview && entity.Identity != selectedEntity && !_entities.Contains(entity.Identity)) || entity.VehiclePointer == IntPtr.Zero)
                    continue;
                if (entity.Identity == selectedEntity)
                    selected = entity;
                else
                    Entity(entity, localPosition, cx, cy, radius, EntityColor(entity.Identity));
            }
            if (selected != null && selected.VehiclePointer != IntPtr.Zero)
                Entity(selected, localPosition, cx, cy, radius, Red);

            // Draw self last: always blue, central and pointing straight up.
            Arrow(cx, cy, 0, -1, Blue);
            if (editors != null)
                foreach (RadarEditor editor in editors)
                    if (editor.Visible)
                        EditingChrome(editor);
        }

        private Vector3 EntityColor(Identity identity)
        {
            if (!_relationshipColors)
                return Yellow;
            if (_team.Contains(identity))
                return Green;
            if (_raid.Contains(identity))
                return Yellow;
            return _pets.Contains(identity) ? Pink : White;
        }

        private void EditingChrome(RadarEditor editor)
        {
            Rect frame = editor.Frame;
            float x = frame.MinX, y = frame.MinY, right = frame.MaxX, bottom = frame.MaxY;
            float reveal = editor.Opacity;
            if (reveal <= 0.001f)
                return;
            reveal = reveal * reveal * (3 - 2 * reveal);
            Vector3 accent = editor.SaveFailed ? new Vector3(1, 0.42f, 0.38f) :
                editor.Moving || editor.Resizing ? new Vector3(1, 0.81f, 0.53f) :
                new Vector3(0.40f, 0.87f, 0.92f);
            Vector3 shadow = new Vector3(0.015f, 0.025f, 0.035f);
            Vector3 soft = new Vector3(0.19f, 0.35f, 0.40f);

            // Every stroke grows continuously with proximity. Add2DLine has no
            // alpha; shortening strokes avoids "fading" them into opaque black.
            FrameCorner(x, y, 1, 1, reveal, shadow, accent);
            FrameCorner(right, y, -1, 1, reveal, shadow, accent);
            FrameCorner(x, bottom, 1, -1, reveal, shadow, accent);
            FrameCorner(right, bottom, -1, -1, reveal, shadow, accent);
            float rail = Math.Max(0, (reveal - 0.12f) / 0.88f);
            GrowRail(x + 30, y + 1, right - 30, y + 1, rail, soft);
            GrowRail(x + 30, bottom - 1, right - 30, bottom - 1, rail, soft);
            GrowRail(x + 1, y + 30, x + 1, bottom - 30, rail, soft);
            GrowRail(right - 1, y + 30, right - 1, bottom - 30, rail, soft);

            // Native labelled Move/Resize captions identify the two real
            // input grips. Keep decorative marks away from false grab points.

        }

        private void GrowRail(float x1, float y1, float x2, float y2, float reveal, Vector3 color)
        {
            if (reveal <= 0)
                return;
            float dx = (x2 - x1) * 0.5f * reveal, dy = (y2 - y1) * 0.5f * reveal;
            _lines.Line(x1, y1, x1 + dx, y1 + dy, color);
            _lines.Line(x2, y2, x2 - dx, y2 - dy, color);
        }

        private void FrameCorner(float x, float y, float dx, float dy, float reveal, Vector3 shadow, Vector3 accent)
        {
            float length = 30 * reveal;
            for (int thickness = -1; thickness <= 4; thickness++)
            {
                _lines.Line(x, y + thickness * dy, x + length * dx, y + thickness * dy, shadow);
                _lines.Line(x + thickness * dx, y, x + thickness * dx, y + length * dy, shadow);
            }
            float start = Math.Min(2, length * 0.25f), end = Math.Max(start, length - 3);
            for (int thickness = 0; thickness < 3; thickness++)
            {
                _lines.Line(x + start * dx, y + thickness * dy, x + end * dx, y + thickness * dy, accent);
                _lines.Line(x + thickness * dx, y + start * dy, x + thickness * dx, y + end * dy, accent);
            }
            float detail = 6 * Math.Max(0, (reveal - 0.3f) / 0.7f);
            if (detail > 0)
            {
                _lines.Line(x + 7 * dx, y + 7 * dy, x + (7 + detail) * dx, y + 7 * dy, accent);
                _lines.Line(x + 7 * dx, y + 7 * dy, x + 7 * dx, y + (7 + detail) * dy, accent);
            }
        }

        private void Entity(Dynel entity, Vector3 local, float cx, float cy, float radius, Vector3 color)
        {
            Marker marker;
            if (TryProject(entity, local, cx, cy, radius, out marker))
                DrawMarker(marker, color);
        }

        private bool TryProject(Dynel entity, Vector3 local, float cx, float cy, float radius, out Marker marker)
        {
            marker = new Marker();
            Vector3 position = entity.GlobalPosition;
            float dx = position.X - local.X, dz = position.Z - local.Z;
            float distanceSquared = dx * dx + dz * dz;
            if (!Finite(distanceSquared) || distanceSquared > RangeMetres * RangeMetres)
                return false;

            float scale = radius / RangeMetres;
            float x = cx + (dx * _forwardZ - dz * _forwardX) * scale;
            float y = cy - (dx * _forwardX + dz * _forwardZ) * scale;
            marker = new Marker { Valid = true, X = x, Y = y };
            if (entity.Identity.Type == IdentityType.SimpleChar)
            {
                Vector3 forward = entity.Forward;
                float fx = forward.X, fz = forward.Z;
                if (Normalize(ref fx, ref fz))
                {
                    marker.IsArrow = true;
                    marker.DX = fx * _forwardZ - fz * _forwardX;
                    marker.DY = -(fx * _forwardX + fz * _forwardZ);
                }
            }
            return true;
        }

        private void DrawMarker(Marker marker, Vector3 color)
        {
            if (marker.IsArrow)
            {
                Arrow(marker.X, marker.Y, marker.DX, marker.DY, color);
                return;
            }
            float x = marker.X, y = marker.Y;

            // Objects have no useful facing. A missing/vertical character
            // heading must not hide an otherwise valid position either.
            _lines.Line(x, y - 4, x + 4, y, color);
            _lines.Line(x + 4, y, x, y + 4, color);
            _lines.Line(x, y + 4, x - 4, y, color);
            _lines.Line(x - 4, y, x, y - 4, color);
        }

        private bool CombatMember(Identity identity, Identity local, Identity selected) =>
            _overview || identity == local || identity == selected || _entities.Contains(identity);

        private void DrawCombat(Identity localIdentity, Identity selected, Vector3 local,
            float cx, float cy, float radius, long now)
        {
            _combatWanted.Clear();
            _combatContext.Clear();
            _combatMarkers.Clear();
            _combatLoaded.Clear();
            try
            {
                foreach (CombatPulse pulse in _combat.Events)
                {
                    if (!CombatMember(pulse.Source, localIdentity, selected) && !CombatMember(pulse.Target, localIdentity, selected))
                        continue;
                    _combatWanted.Add(pulse.Source);
                    _combatWanted.Add(pulse.Target);
                }
                if (_combatWanted.Count == 0) return;
                foreach (Dynel entity in DynelManager.AllDynels)
                    if (_combatWanted.Contains(entity.Identity))
                        _combatLoaded[entity.Identity] = entity;
                _combatMarkers[localIdentity] = new Marker { Valid = true, IsArrow = true, X = cx, Y = cy, DX = 0, DY = -1 };

                foreach (CombatPulse pulse in _combat.Events)
                {
                    if (!CombatMember(pulse.Source, localIdentity, selected) && !CombatMember(pulse.Target, localIdentity, selected))
                        continue;
                    Marker source, target;
                    if (!CombatMarker(pulse.Source, local, cx, cy, radius, out source) ||
                        !CombatMarker(pulse.Target, local, cx, cy, radius, out target))
                        continue;
                    if (!CombatMember(pulse.Source, localIdentity, selected)) _combatContext.Add(pulse.Source);
                    if (!CombatMember(pulse.Target, localIdentity, selected)) _combatContext.Add(pulse.Target);
                    float dx = target.TipX - source.TipX, dy = target.TipY - source.TipY;
                    float length = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (!Finite(length)) continue;
                    for (int dot = 0; dot < RadarCombat.DotCount; dot++)
                    {
                        long age = now - pulse.Started - dot * RadarCombat.DotSpacingMilliseconds;
                        if (age < 0 || age >= RadarCombat.TravelMilliseconds) continue;
                        float progress = (float)age / RadarCombat.TravelMilliseconds;
                        if (length < 0.5f)
                        {
                            // Self-targeted activity (or overlapping tips) has
                            // one visible location: loop from its tip back to it.
                            double angle = progress * Math.PI * 2;
                            float sine = (float)Math.Sin(angle), cosine = (float)Math.Cos(angle);
                            float x = source.TipX + sine * 6, y = source.TipY + (cosine - 1) * 6;
                            CombatStroke(x - cosine * 2.2f, y + sine * 2.2f, x, y, cx, cy, radius);
                        }
                        else
                        {
                            float x = source.TipX + dx * progress, y = source.TipY + dy * progress;
                            CombatStroke(x - dx / length * 2.2f, y - dy / length * 2.2f, x, y, cx, cy, radius);
                        }
                    }
                }
                // Reveal only direct opponents of the chosen category/current target
                // or self. These faint context markers never alter lists or counts.
                foreach (Identity identity in _combatContext)
                    DrawMarker(_combatMarkers[identity], OpponentColor);
            }
            finally
            {
                // Retain identities/events, never dynel wrappers between frames.
                _combatLoaded.Clear();
            }
        }

        private bool CombatMarker(Identity identity, Vector3 local, float cx, float cy, float radius, out Marker marker)
        {
            if (_combatMarkers.TryGetValue(identity, out marker)) return marker.Valid;
            Dynel entity;
            marker = new Marker();
            if (_combatLoaded.TryGetValue(identity, out entity) && entity.VehiclePointer != IntPtr.Zero)
                TryProject(entity, local, cx, cy, radius, out marker);
            _combatMarkers[identity] = marker;
            return marker.Valid;
        }

        private void CombatStroke(float x1, float y1, float x2, float y2, float cx, float cy, float radius)
        {
            // Clip the actual tiny stroke, after interpolation, to the outer ring.
            // Arrow tips themselves can sit six pixels beyond a 100m centre.
            double px = x1 - cx, py = y1 - cy, dx = x2 - x1, dy = y2 - y1;
            double a = dx * dx + dy * dy;
            if (a < 0.000001) return;
            double b = px * dx + py * dy, c = px * px + py * py - (double)radius * radius;
            double discriminant = b * b - a * c;
            if (discriminant < 0) return;
            double root = Math.Sqrt(discriminant);
            double start = Math.Max(0, (-b - root) / a), end = Math.Min(1, (-b + root) / a);
            if (end <= start) return;
            _lines.Line(x1 + (float)(dx * start), y1 + (float)(dy * start),
                x1 + (float)(dx * end), y1 + (float)(dy * end), CombatColor);
        }

        private struct Marker
        {
            public float X, Y, DX, DY;
            public bool Valid, IsArrow;
            public float TipX => X + (IsArrow ? DX * 6 : 0);
            public float TipY => Y + (IsArrow ? DY * 6 : 0);
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
