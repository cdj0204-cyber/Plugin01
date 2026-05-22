using System.Collections.Generic;
using Rhino.Geometry;

namespace Plugin01
{
    /// <summary>Brep 면들 중 탄젠트(매끄럽게) 연결된 면 그룹을 찾는다.</summary>
    public static class FaceGrouping
    {
        /// <summary>
        /// seed 면에서 시작해, 탄젠트(부드러운) 모서리로 이어진 모든 면 인덱스를 수집한다.
        /// </summary>
        public static List<int> GrowTangent(Brep brep, int seed, double angleTolRad)
        {
            var result = new List<int>();
            if (brep == null || seed < 0 || seed >= brep.Faces.Count) return result;

            var visited = new HashSet<int> { seed };
            var queue = new Queue<int>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                int fi = queue.Dequeue();
                result.Add(fi);

                var face = brep.Faces[fi];
                foreach (int ei in face.AdjacentEdges())
                {
                    var edge = brep.Edges[ei];
                    if (!edge.IsSmoothManifoldEdge(angleTolRad)) continue; // 탄젠트 연결만

                    foreach (int af in edge.AdjacentFaces())
                    {
                        if (af >= 0 && !visited.Contains(af))
                        {
                            visited.Add(af);
                            queue.Enqueue(af);
                        }
                    }
                }
            }

            return result;
        }
    }
}
