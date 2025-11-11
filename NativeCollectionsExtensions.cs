namespace PolygonBuilder
{
    using System;
    using Unity.Collections;

    public static class NativeCollectionsExtensions
    {
        public static void DisposeIfCreated<T>(this NativeArray<T> collection) where T : unmanaged
        {
            if (collection.IsCreated) 
                collection.Dispose();
        }
        
        public static void DisposeIfCreated<T>(this NativeList<T> collection) where T : unmanaged
        {
            if (collection.IsCreated) 
                collection.Dispose();
        }
        
        public static void DisposeIfCreated<T>(this NativeHashSet<T> collection) where T : unmanaged, IEquatable<T>
        {
            if (collection.IsCreated) 
                collection.Dispose();
        }
    }
}