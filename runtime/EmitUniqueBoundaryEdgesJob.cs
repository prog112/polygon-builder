namespace PolygonBuilder
{
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Jobs;

    /// <summary>
    /// Scans a 2D tile grid and emits the outer boundary edges of all non-empty tiles.
    /// An edge is emitted when a filled tile borders an empty tile or the grid boundary.
    /// 
    /// Edges are packed into a 64-bit key (two 32-bit packed points) and collected into
    /// a <see cref="NativeHashSet{T}"/> which guarantees uniqueness automatically.
    /// 
    /// The job performs a simple single-threaded pass and is Burst-compiled for high throughput.
    /// It does not allocate and produces data suitable for contour tracing, polygon building,
    /// or collision mesh extraction.
    /// 
    /// Tile layout assumes a 1D array indexed as: index = x + y * width.
    /// </summary>
    [BurstCompile]
    public struct EmitUniqueBoundaryEdgesJob : IJob
    {
        [ReadOnly] public NativeArray<byte> tiles;
        [ReadOnly] public int width;
        [ReadOnly] public int height;
        
        // HashSet will automatically deduplicate edges
        [WriteOnly] public NativeHashSet<ulong> edgesOutput;
        
        public void Execute()
        {
            var size = width * height;
            for (int i = 0; i < size; i++)
            {
                // We only care about non-empty tiles
                if (tiles[i] == 0)
                    continue;

                var x = i % width;
                var y = i / width;

                if (y == 0 || tiles[i - width] == 0)
                {
                    var topEdge = Packing.PackEdge(Packing.PackPoint(x, y), Packing.PackPoint(x + 1, y));
                    edgesOutput.Add(topEdge);
                }

                if (y == height - 1 || tiles[i + width] == 0)
                {
                    var bottomEdge = Packing.PackEdge(Packing.PackPoint(x, y + 1), Packing.PackPoint(x + 1, y + 1));
                    edgesOutput.Add(bottomEdge);
                }

                if (x == 0 || tiles[i - 1] == 0)
                {
                    var leftEdge = Packing.PackEdge(Packing.PackPoint(x, y), Packing.PackPoint(x, y + 1));
                    edgesOutput.Add(leftEdge);
                }

                if (x == width - 1 || tiles[i + 1] == 0)
                {
                    var rightEdge = Packing.PackEdge(Packing.PackPoint(x + 1, y), Packing.PackPoint(x + 1, y + 1));
                    edgesOutput.Add(rightEdge);
                }
            }
        }
    }
}