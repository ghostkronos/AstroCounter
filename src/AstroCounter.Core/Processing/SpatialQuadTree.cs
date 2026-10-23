namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Models;

/// <summary>
/// High-performance Spatial QuadTree for astronomical viewport culling.
/// Partitions tens of thousands to hundreds of thousands of celestial objects
/// into hierarchical quadrant bounding boxes, enabling sub-millisecond frustum culling
/// and guaranteed 60 FPS viewport rendering.
/// </summary>
public sealed class SpatialQuadTree
{
    private readonly Node _root;

    public float MinX => _root.MinX;
    public float MinY => _root.MinY;
    public float MaxX => _root.MaxX;
    public float MaxY => _root.MaxY;
    public int TotalCount => _root.TotalCount;

    public SpatialQuadTree(IReadOnlyList<AstrophotographyObject> objects, int maxItemsPerNode = 64, int maxDepth = 10)
    {
        if (objects == null || objects.Count == 0)
        {
            _root = new Node(0, 0, 1000, 1000, 0);
            return;
        }

        // Compute global enclosing bounding box
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            float cx = o.CenterX;
            float cy = o.CenterY;
            if (cx < minX) minX = cx;
            if (cy < minY) minY = cy;
            if (cx > maxX) maxX = cx;
            if (cy > maxY) maxY = cy;
        }

        // Add small safety margin to avoid edge singularities
        minX -= 10f;
        minY -= 10f;
        maxX += 10f;
        maxY += 10f;

        _root = new Node(minX, minY, maxX, maxY, 0);

        for (int i = 0; i < objects.Count; i++)
        {
            _root.Insert(objects[i], maxItemsPerNode, maxDepth);
        }
    }

    /// <summary>
    /// Fast viewport query returning all celestial objects intersecting or within the viewport bounds.
    /// </summary>
    public void Query(float qMinX, float qMinY, float qMaxX, float qMaxY, List<AstrophotographyObject> results)
    {
        _root.Query(qMinX, qMinY, qMaxX, qMaxY, results);
    }

    public void Query(BoundingBox viewport, List<AstrophotographyObject> results)
    {
        Query(viewport.X, viewport.Y, viewport.Right, viewport.Bottom, results);
    }

    private sealed class Node
    {
        public float MinX { get; }
        public float MinY { get; }
        public float MaxX { get; }
        public float MaxY { get; }
        public float MidX { get; }
        public float MidY { get; }
        public int Depth { get; }
        public int TotalCount { get; private set; }

        private List<AstrophotographyObject>? _items;
        private Node? _nw;
        private Node? _ne;
        private Node? _sw;
        private Node? _se;

        public Node(float minX, float minY, float maxX, float maxY, int depth)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            MidX = minX + ((maxX - minX) * 0.5f);
            MidY = minY + ((maxY - minY) * 0.5f);
            Depth = depth;
            _items = new List<AstrophotographyObject>(32);
        }

        public void Insert(AstrophotographyObject item, int maxItemsPerNode, int maxDepth)
        {
            TotalCount++;

            // If this node is split into children, insert directly into matching child quadrant
            if (_nw != null)
            {
                GetChildForPoint(item.CenterX, item.CenterY).Insert(item, maxItemsPerNode, maxDepth);
                return;
            }

            _items!.Add(item);

            // Split if capacity exceeded and max depth not reached
            if (_items.Count > maxItemsPerNode && Depth < maxDepth)
            {
                Split(maxItemsPerNode, maxDepth);
            }
        }

        private void Split(int maxItemsPerNode, int maxDepth)
        {
            _nw = new Node(MinX, MinY, MidX, MidY, Depth + 1);
            _ne = new Node(MidX, MinY, MaxX, MidY, Depth + 1);
            _sw = new Node(MinX, MidY, MidX, MaxY, Depth + 1);
            _se = new Node(MidX, MidY, MaxX, MaxY, Depth + 1);

            for (int i = 0; i < _items!.Count; i++)
            {
                var item = _items[i];
                GetChildForPoint(item.CenterX, item.CenterY).Insert(item, maxItemsPerNode, maxDepth);
            }

            _items = null;
        }

        private Node GetChildForPoint(float x, float y)
        {
            if (x < MidX)
            {
                return y < MidY ? _nw! : _sw!;
            }
            else
            {
                return y < MidY ? _ne! : _se!;
            }
        }

        public void Query(float qMinX, float qMinY, float qMaxX, float qMaxY, List<AstrophotographyObject> results)
        {
            // Disjoint test: node bounding box completely outside query viewport -> Instant prune
            if (MaxX < qMinX || MinX > qMaxX || MaxY < qMinY || MinY > qMaxY)
            {
                return;
            }

            // Node fully contained inside viewport -> Fast collect entire subtree without per-item checks
            if (MinX >= qMinX && MaxX <= qMaxX && MinY >= qMinY && MaxY <= qMaxY)
            {
                CollectAll(results);
                return;
            }

            // Leaf node: check items against query rectangle
            if (_items != null)
            {
                int count = _items.Count;
                for (int i = 0; i < count; i++)
                {
                    var item = _items[i];
                    float cx = item.CenterX;
                    float cy = item.CenterY;
                    if (cx >= qMinX && cx <= qMaxX && cy >= qMinY && cy <= qMaxY)
                    {
                        results.Add(item);
                    }
                }
                return;
            }

            // Internal node: recurse into intersecting children
            _nw?.Query(qMinX, qMinY, qMaxX, qMaxY, results);
            _ne?.Query(qMinX, qMinY, qMaxX, qMaxY, results);
            _sw?.Query(qMinX, qMinY, qMaxX, qMaxY, results);
            _se?.Query(qMinX, qMinY, qMaxX, qMaxY, results);
        }

        private void CollectAll(List<AstrophotographyObject> results)
        {
            if (_items != null)
            {
                results.AddRange(_items);
                return;
            }

            _nw?.CollectAll(results);
            _ne?.CollectAll(results);
            _sw?.CollectAll(results);
            _se?.CollectAll(results);
        }
    }
}

