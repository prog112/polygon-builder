namespace PolygonBuilder
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Cysharp.Threading.Tasks;
    using Unity.Collections;
    using Unity.Jobs;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.Pool;

    /// <summary>
    /// Converts tile-based solid geometry into a stream of simple 2D collision polygons.
    ///  
    /// The builder works in three stages:
    /// 1) Extracts exposed tile edges from the input grid.
    /// 2) Stitches those edges into closed contours.
    /// 3) Simplifies the contours by removing duplicate and collinear points.
    ///
    /// Polygons are emitted incrementally with zero per-polygon allocation. The caller supplies
    /// a reusable <see cref="Vector2"/> buffer which is filled for each polygon callback.
    ///
    /// <b>Coordinate Constraint:</b>
    /// Internally, grid points are packed into 16-bit components (ushort),
    /// meaning tile coordinates must not exceed <c>0..65535</c> on either axis.
    ///
    /// Non-zero tile values are considered solid. No engine or coordinate-space assumptions are made.
    /// </summary>
    public static class PolygonBuilder
    {
        /// <summary>
        /// Generates and streams simplified 2D collision polygons from a tile grid without allocating per-polygon memory.
        ///
        /// <para>
        /// The method processes the input and invokes <paramref name="consumer"/> once for every valid polygon found.
        /// Each polygon is written into the caller-owned <paramref name="array"/> buffer to avoid heap allocations.
        /// The provided vertex count specifies how many elements in the buffer are valid for that polygon.
        /// </para>
        ///
        /// <para>
        /// <b>Input Requirements:</b>
        /// <list type="bullet">
        ///     <item><paramref name="tileArray"/> is a 1D grid of tiles, row-major, where non-zero values are treated as solid.</item>
        ///     <item><paramref name="array"/> must be large enough to hold the largest possible polygon in the input chunk.</item>
        ///     <item><paramref name="consumer"/> must consume or copy the polygon data before the next invocation.</item>
        /// </list>
        /// </para>
        ///
        /// <para>
        /// <b>Processing Guarantees:</b>
        /// <list type="bullet">
        ///     <item>All emitted polygons are closed and wound consistently.</item>
        ///     <item>No duplicate or collinear vertices are produced.</item>
        ///     <item>No intermediate polygon allocations occur.</item>
        ///     <item>Degenerate or sub-triangle polygons are discarded.</item>
        /// </list>
        /// </para>
        ///
        /// <para><b>Threading:</b> Runs asynchronously and supports cancellation via <paramref name="ct"/>.</para>
        /// </summary>
        /// <param name="tileArray">Flattened tile grid, where non-zero values are treated as collidable/solid.</param>
        /// /// <param name="width">Width of the tile grid, matching the number of columns in <paramref name="tileArray"/>.</param>
        /// <param name="height">Height of the tile grid, matching the number of rows in <paramref name="tileArray"/>.</param>
        /// <param name="array">Reusable vertex buffer used to write polygon vertices into. Must be reused and not reallocated.</param>
        /// <param name="consumer">
        /// Callback invoked for each polygon. Receives the shared <paramref name="array"/> and a vertex count. 
        /// Data is only valid during the call and must be copied if stored.
        /// </param>
        /// <param name="ct">Optional cancellation token to abort processing early.</param>
        /// <returns>An awaitable task that completes when polygon streaming finishes or is canceled.</returns>
        public static async UniTask StreamPolygons(byte[] tileArray, int width, int height, Vector2[] array, Action<Vector2[], int> consumer, CancellationToken ct = default)
        {
            JobHandle handle = default;

            // Managed buffers
            using var poolHandle = DictionaryPool<uint, List<uint>>.Get(out var adjacencyMap);

            // Unmanaged buffers
            var tilesBuffer = new NativeArray<byte>(tileArray, Allocator.TempJob);
            var edgesBuffer = new NativeHashSet<ulong>(width * height * 4, Allocator.TempJob);

            try
            {
                var emitEdgesJob = new EmitUniqueBoundaryEdgesJob
                {
                    tiles = tilesBuffer,
                    width = width,
                    height = height,
                    edgesOutput = edgesBuffer
                };

                handle = emitEdgesJob.Schedule();
                await handle.ToUniTask(PlayerLoopTiming.PostLateUpdate).AttachExternalCancellation(ct);

                BuildAdjacencyMap(adjacencyMap, edgesBuffer);
                BuildPolygons(array, edgesBuffer, adjacencyMap, consumer);
            }
            catch (OperationCanceledException)
            {
                if (!handle.IsCompleted)
                    handle.Complete();

                throw;
            }
            finally
            {
                edgesBuffer.DisposeIfCreated();
                tilesBuffer.DisposeIfCreated();
                foreach (var list in adjacencyMap.Values)
                    ListPool<uint>.Release(list);
            }
        }

        /// <summary>
        /// Builds an adjacency list mapping each vertex to all vertices it shares an edge with.
        /// Used to walk continuous edge contours when assembling polygons.
        /// </summary>
        private static void BuildAdjacencyMap(Dictionary<uint, List<uint>> adjacencyMap, NativeHashSet<ulong> edges)
        {
            foreach (var edge in edges)
            {
                Packing.UnpackEdge(edge, out var start, out var end);

                if (!adjacencyMap.TryGetValue(start, out var startList))
                {
                    ListPool<uint>.Get(out startList);
                    adjacencyMap.Add(start, startList);
                }

                startList.Add(end);

                if (!adjacencyMap.TryGetValue(end, out var endList))
                {
                    ListPool<uint>.Get(out endList);
                    adjacencyMap.Add(end, endList);
                }

                endList.Add(start);
            }
        }

        /// <summary>
        /// Extracts closed polygons from a set of unique edges. Consumes edges as they are walked.
        /// Each resulting polygon is simplified (duplicate and collinear vertices removed) and
        /// emitted to the caller via <paramref name="consumer"/> using a preallocated <see cref="Vector2"/> buffer.
        /// </summary>
        private static void BuildPolygons(Vector2[] array, NativeHashSet<ulong> edges, Dictionary<uint, List<uint>> adjacencyMap,
            Action<Vector2[], int> consumer)
        {
            var buffer = ArrayPool<uint>.Shared.Rent(array.Length);

            try
            {
                while (edges.Count > 0)
                {
                    var vertexCount = 0;

                    var edge = GrabFirst(edges);
                    Packing.UnpackEdge(edge, out var startPoint, out var endPoint);

                    AppendVertex(startPoint, ref vertexCount);
                    AppendVertex(endPoint, ref vertexCount);
                    edges.Remove(edge);

                    var start = startPoint;
                    var previous = start;
                    var next = endPoint;

                    var maxSteps = Math.Max(256, edges.Count * 4);
                    var steps = 0;
                    while (!start.Equals(next))
                    {
                        // Safety net
                        if (++steps > maxSteps)
                            break;

                        if (!adjacencyMap.TryGetValue(next, out var neighbors) || neighbors.Count == 0)
                        {
                            // Shouldn't happen with closed bounding edges, but in case something goes wrong
                            // Skip this contour
                            break;
                        }

                        if (!SelectNextVertex(edges, neighbors, previous, next, out var chosen))
                            break;

                        previous = next;
                        next = chosen;

                        AppendVertex(next, ref vertexCount);
                        edges.Remove(Packing.PackEdge(previous, next));
                    }

                    // Make sure we have an actual polygon
                    if (start.Equals(next))
                        ProcessAndEmitPolygon(vertexCount);
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(buffer);
            }

            void AppendVertex(uint vertex, ref int vertexCount)
            {
                if (vertexCount >= array.Length)
                    throw new InvalidOperationException($"Array buffer is too small to hold the polygon. Needed more than {array.Length} vertices!");

                buffer[vertexCount++] = vertex;
            }

            void ProcessAndEmitPolygon(int vertexCount)
            {
                // Build an output array for the caller
                var finalVertexCount = RemoveRedundantVertices(buffer, vertexCount);
                for (int i = 0; i < finalVertexCount; i++)
                {
                    Packing.UnpackPoint(buffer[i], out var x, out var y);
                    array[i] = new Vector2(x, y);
                }

                consumer?.Invoke(array, finalVertexCount); // Emit the found polygon
            }
        }

        /// <summary>
        /// Selects the next vertex to walk toward when tracing a polygon contour.
        /// Prioritizes continuing in the same direction, then prefers a right turn,
        /// and finally falls back to any valid connected edge.
        /// Returns false if no valid outbound edge exists.
        /// </summary>
        private static bool SelectNextVertex(NativeHashSet<ulong> availableEdges, List<uint> neighbors, uint previous, uint next, out uint chosen)
        {
            chosen = default;

            var direction = CalculateDirection(previous, next);
            var bestScore = 0;
            var bestCandidate = -1;

            for (var i = 0; i < neighbors.Count; i++)
            {
                var candidate = neighbors[i];
                if (!availableEdges.Contains(Packing.PackEdge(next, candidate)))
                    continue;

                // Best: Favor the same direction
                var candidateDirection = CalculateDirection(next, candidate);
                if (candidateDirection.Equals(direction))
                {
                    // Instantly return 
                    chosen = candidate;
                    return true;
                }

                // Second: Prefer right
                if (SignedCross(direction, candidateDirection) > 0)
                {
                    if (bestScore < 2)
                    {
                        bestScore = 2;
                        bestCandidate = i;
                    }
                }

                // Otherwise just take whatever is left
                if (bestScore < 1)
                {
                    bestScore = 1;
                    bestCandidate = i;
                }
            }

            if (bestScore == 0)
                return false;

            chosen = neighbors[bestCandidate];
            return true;
        }

        /// <summary>
        /// Removes duplicate vertices and collinear points from a polygon (packed vertex array) in-place.
        /// Returns the new vertex count. A result less than 3 indicates the polygon is invalid.
        /// </summary>
        private static int RemoveRedundantVertices(uint[] polygon, int n)
        {
            if (n <= 2)
                return 0;

            // Remove duplicates
            var write = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == 0 || !polygon[i].Equals(polygon[i - 1]))
                    polygon[write++] = polygon[i];
            }

            if (write > 1 && polygon[write - 1].Equals(polygon[0]))
                write--;

            // Can't be a polygon with just 2 vertices
            if (write < 3)
                return 0;

            var points = ArrayPool<int2>.Shared.Rent(write);
            try
            {
                // Unpack the polygon into an array of points
                for (int i = 0; i < write; i++)
                {
                    Packing.UnpackPoint(polygon[i], out var x, out var y);
                    points[i] = new int2(x, y);
                }

                var write2 = 0;
                for (int i = 0; i < write; i++)
                {
                    var prev = (i - 1 + write) % write;
                    var next = (i + 1) % write;

                    var a = points[prev];
                    var b = points[i];
                    var c = points[next];

                    // Only write those vertices that are not collinear
                    var cross = SignedCross(b - a, c - b);
                    if (cross != 0)
                        points[write2++] = b;
                }

                // Pack back into the caller buffer
                for (int i = 0; i < write2; i++)
                    polygon[i] = Packing.PackPoint(points[i].x, points[i].y);

                return write2;
            }
            finally
            {
                ArrayPool<int2>.Shared.Return(points);
            }
        }

        /// <summary>
        /// Computes a normalized step direction between two packed 2D grid points.
        /// Each component is clamped to -1, 0, or 1, representing movement direction only.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int2 CalculateDirection(uint from, uint to)
        {
            Packing.UnpackPoint(from, out var fromX, out var fromY);
            Packing.UnpackPoint(to, out var toX, out var toY);

            var direction = new int2(toX - fromX, toY - fromY);
            direction.x = math.clamp(direction.x, -1, 1);
            direction.y = math.clamp(direction.y, -1, 1);

            return direction;
        }

        /// <summary>
        /// 2D signed cross product of vectors A and B.
        /// Positive = B is to the right of A, negative = left, 0 = collinear.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long SignedCross(int2 a, int2 b)
        {
            return (long)a.x * b.y - (long)a.y * b.x;
        }

        /// <summary>
        /// Returns any element from the hash set without allocating or enumerating fully.
        /// Just because LINQ sucks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong GrabFirst(NativeHashSet<ulong> edges)
        {
            foreach (var edge in edges)
                return edge;

            return default;
        }
    }
}