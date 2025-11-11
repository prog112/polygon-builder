namespace PolygonBuilder
{
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Packing helpers for points and edges that assumes each coordinate fits in 16 bits (0..65535).
    /// Packs a point into a uint (high 16 = x, low 16 = y).
    /// Packs an ordered edge into a single ulong (high 32 = v1, low 32 = v2).
    /// </summary>
    public static class Packing
    {
        private const int CoordBits = 16;
        private const uint CoordMask = 0xFFFFu;

        private const int VertexBits = 32;
        private const ulong VertexMask = 0xFFFFFFFFUL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackPoint(int x, int y)
        {
            return (((uint)x & CoordMask) << CoordBits) | ((uint)y & CoordMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackPoint(uint packed, out int x, out int y)
        {
            x = (int)(packed >> CoordBits);
            y = (int)(packed & CoordMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PackEdge(uint v1, uint v2)
        {
            // Smaller goes first so that all edges are ordered
            return v1 <= v2
                ? (((ulong)v1 & VertexMask) << VertexBits) | ((ulong)v2 & VertexMask)
                : (((ulong)v2 & VertexMask) << VertexBits) | ((ulong)v1 & VertexMask);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackEdge(ulong packed, out uint v1, out uint v2)
        {
            v1 = (uint)(packed >> VertexBits);
            v2 = (uint)(packed & VertexMask);
        }
    }
}