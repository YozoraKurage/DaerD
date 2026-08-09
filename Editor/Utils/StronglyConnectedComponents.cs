using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Tarjan's strongly-connected-components algorithm over a directed graph given as an
    /// adjacency list of node indices (used by the analyzer to find state groups that can be
    /// entered but never left).
    /// </summary>
    static class StronglyConnectedComponents
    {
        /// <summary>Iterative Tarjan; returns each node's component id (count via out parameter).</summary>
        public static int[] Compute(List<int>[] edges, out int sccCount)
        {
            int n = edges.Length;
            var ids = new int[n];
            var low = new int[n];
            var onStack = new bool[n];
            var comp = new int[n];
            for (int i = 0; i < n; i++) ids[i] = -1;
            var stack = new Stack<int>();
            int nextId = 0, components = 0;

            // Explicit work stack: (node, next edge index to visit).
            var work = new Stack<(int node, int edge)>();
            for (int start = 0; start < n; start++)
            {
                if (ids[start] != -1) continue;
                work.Push((start, 0));
                while (work.Count > 0)
                {
                    var (node, edge) = work.Pop();
                    if (edge == 0)
                    {
                        ids[node] = low[node] = nextId++;
                        stack.Push(node);
                        onStack[node] = true;
                    }
                    else
                    {
                        // Returning from the recursive visit of edges[node][edge - 1].
                        low[node] = Mathf.Min(low[node], low[edges[node][edge - 1]]);
                    }

                    bool descended = false;
                    while (edge < edges[node].Count)
                    {
                        int next = edges[node][edge];
                        edge++;
                        if (ids[next] == -1)
                        {
                            work.Push((node, edge));
                            work.Push((next, 0));
                            descended = true;
                            break;
                        }
                        if (onStack[next])
                            low[node] = Mathf.Min(low[node], ids[next]);
                    }
                    if (descended) continue;

                    if (low[node] == ids[node])
                    {
                        int member;
                        do
                        {
                            member = stack.Pop();
                            onStack[member] = false;
                            comp[member] = components;
                        } while (member != node);
                        components++;
                    }
                }
            }
            sccCount = components;
            return comp;
        }
    }
}
