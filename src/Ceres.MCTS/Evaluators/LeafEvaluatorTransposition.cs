#region License notice

/*
  This file is part of the Ceres project at https://github.com/dje-dev/ceres.
  Copyright (C) 2020- by David Elliott and the Ceres Authors.

  Ceres is free software under the terms of the GNU General Public License v3.0.
  You should have received a copy of the GNU General Public License
  along with Ceres. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

#region Using directives

using System;
using System.Diagnostics;
using System.Threading;

using Ceres.Chess;
using Ceres.MCTS.Iteration;
using Ceres.MCTS.MTCSNodes;
using Ceres.MCTS.MTCSNodes.Struct;
using Ceres.MCTS.Params;

#endregion

namespace Ceres.MCTS.Evaluators
{
  /// <summary>
  /// Node evaluator which can yield evaluations by other nodes
  /// representing the same position which are already extant elsewhere in the search tree.
  /// </summary>
  public partial class LeafEvaluatorTransposition : LeafEvaluatorBase
  {
    // TODO: Transposition counts temporarily disabled for performance reasons (false sharing)
    static ulong NumHits = 0;
    static ulong NumMisses = 0;
    public static float HitRatePct => 100.0f * (float)NumHits / (float)(NumHits + NumMisses);


    /// <summary>
    /// Maintain data structure to map between position hash codes and 
    /// indices of nodes already extant in the search tree store.
    /// </summary>
    public readonly TranspositionRootsDict TranspositionRoots;

    /// <summary>
    /// Record new pending transposition roots accumulated during a batch
    /// so they can be added to the TranspositionRoots dictionary 
    /// all at once at end of batch collection.
    /// </summary>
    int nextIndexPendingTranspositionRoots;
    (ulong, int)[] pendingTranspositionRoots;

    const bool VERBOSE = false;
    static int WARN_COUNT = 0;


    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="transpositionRoots"></param>
    public LeafEvaluatorTransposition(TranspositionRootsDict transpositionRoots)
    {
      TranspositionRoots = transpositionRoots;

      // TODO: currently hardcoded at 2048, potentially
      // dynamically detemrine possibly smaller necessary value
      pendingTranspositionRoots = new (ulong, int)[2048];
    }


    /// <summary>
    /// Called when a node pending evaluation is found to be the same position
    /// (for the purposes of transposition equivalence classes)
    /// as another node in the tree.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="transpositionRootNodeIndex"></param>
    /// <param name="transpositionRootNode"></param>
    /// <returns></returns>
    LeafEvaluationResult ProcessFirstLinkage(MCTSNode node, MCTSNodeStructIndex transpositionRootNodeIndex,
                                             in MCTSNodeStruct transpositionRootNode)
    {
      ref MCTSNodeStruct nodeRef = ref node.Ref;

      nodeRef.NumPolicyMoves = transpositionRootNode.NumPolicyMoves;

      Debug.Assert(transpositionRootNodeIndex.Index != 0);

      TranspositionMode transpositionMode = node.Context.ParamsSearch.Execution.TranspositionMode;
      if (transpositionMode == TranspositionMode.SingleNodeCopy
       || transpositionRootNode.Terminal.IsTerminal())
      {
        nodeRef.CopyUnexpandedChildrenFromOtherNode(node.Tree, transpositionRootNodeIndex);
      }
      else if (transpositionMode == TranspositionMode.SingleNodeDeferredCopy
            || transpositionMode == TranspositionMode.SharedSubtree)
      {
        Debug.Assert(transpositionRootNodeIndex.Index != 0);

        // We mark this as just extracted, but do not (yet) allocate and move over the children.
        nodeRef.TranspositionRootIndex = transpositionRootNodeIndex.Index;

        // Compute the number of times to apply.
        int applyTarget = node.Context.ParamsSearch.MaxTranspositionRootApplicationsFixed;
        applyTarget += (int)Math.Round(transpositionRootNode.N * node.Context.ParamsSearch.MaxTranspositionRootApplicationsFraction, 0);
        applyTarget = Math.Min(applyTarget, MCTSNodeStruct.MAX_NUM_VISITS_PENDING_TRANSPOSITION_ROOT_EXTRACTION);

        // Repeatedly sampling the same leaf node value is not a reasonable strategy.
        Debug.Assert(applyTarget <= 1 || node.Context.ParamsSearch.TranspositionUseTransposedQ);

        // If the root has far more visits than our fixed target
        // then only use it once (since it represents a value based on a very different subtree).
        if (ForceUseV(node, in transpositionRootNode))
        {
          applyTarget = 1;
        }

        // Limit number of potential applications to be not greater than number of root nodes.
        // TODO: could we apply this rule later, at such a time as the number may have grown?
        nodeRef.NumVisitsPendingTranspositionRootExtraction = Math.Min(transpositionRootNode.N, applyTarget);

        EnsurePendingTranspositionValuesSet(node, false);
      }
      else
      {
        throw new NotImplementedException();
      }

      //      if (CeresEnvironment.MONITORING_METRICS) NumHits++;

      if (node.Context.ParamsSearch.TranspositionUseTransposedQ && transpositionMode != TranspositionMode.SharedSubtree)
      {
#if NOT
        // We deviate from pure MCTS and 
        // set the backed up node for this leaf node to be the 
        // overall score (Q) from the complete linked tranposition subtree
        if (node.Context.ParamsSearch.TranspositionUseCluster)
        {
          // Take the Q from the largest subtree (presumably the most accurate value)
          ref MCTSNodeStruct biggestTranspositionClusterNode = ref MCTSNodeTranspositionManager.GetNodeWithMaxNInCluster(node);
          node.OverrideVToApplyFromTransposition = (FP16)biggestTranspositionClusterNode.Q;
          node.OverrideMPositionToApplyFromTransposition = (FP16)biggestTranspositionClusterNode.MPosition;
        }
        else
        {
          node.OverrideVToApplyFromTransposition = (FP16)transpositionRootNode.Q;
          node.OverrideMPositionToApplyFromTransposition = (FP16)transpositionRootNode.MPosition;
        }

#endif
      }

      return new LeafEvaluationResult(transpositionRootNode.Terminal, transpositionRootNode.WinP,
                                      transpositionRootNode.LossP, transpositionRootNode.MPosition);
    }



    /// <summary>
    /// Implements virtual method to try to resolve evaluation of a specified node
    /// from the transposition data structures.
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    protected override LeafEvaluationResult DoTryEvaluate(MCTSNode node)
    {
      Debug.Assert(float.IsNaN(node.V));

      if (node.Context.ParamsSearch.Execution.TranspositionMode == TranspositionMode.SingleNodeDeferredCopy
       && node.IsTranspositionLinked)
      {
        // Node is already being used for transpositions.
        return default;
      }

      if (node.Context.ParamsSearch.Execution.TranspositionMode == TranspositionMode.SharedSubtree)
      //&& node.Context.ParamsSearch.Execution.TRANSPOSITION_SINGLE_TREE
      //       && !float.IsNaN(node.OverrideVToApplyFromTransposition))
      {
        throw new NotImplementedException();

        // If the selector already stopped descent here 
        // due to a tranpsosition cluster virtual visit,
        // return a NodeEvaluationResult to 
        // indicate this can be processed without NN
        // (and use an NodeEvaluationResult which leaves unchanged)
        //return new LeafEvaluationResult(node.Terminal, node.WinP, node.LossP, node.MPosition);
      }

      // Check if this position already exists in the tree
      if (TranspositionRoots.TryGetValue(node.Ref.ZobristHash, out int transpositionNodeIndex))
      {
        // Found already existing node
        ref MCTSNodeStruct transpositionNode = ref node.Context.Tree.Store.Nodes.nodes[transpositionNodeIndex];

        // Only attempt transposition linkage if the transposition root
        // node is fully initialized (not in flight) and not terminal
        if (transpositionNode.N == 0 || transpositionNode.Terminal.IsTerminal())
        {
          //          if (CeresEnvironment.MONITORING_METRICS) NumMisses++;
          return default;
        }

        if (node.Context.ParamsSearch.TranspositionUseCluster)
        {
          MCTSNodeTranspositionManager.CheckAddToCluster(node);
        }

        return ProcessFirstLinkage(node, new MCTSNodeStructIndex(transpositionNodeIndex), in transpositionNode);
      }
      else
      {
        int pendingIndex = Interlocked.Increment(ref nextIndexPendingTranspositionRoots) - 1;
        pendingTranspositionRoots[pendingIndex] = (node.Ref.ZobristHash, node.Index);

        //        if (CeresEnvironment.MONITORING_METRICS) NumMisses++;

        return default;
      }
    }


    /// <summary>
    /// BatchPostprocess is called at the end of gathering a batch of leaf nodes.
    /// It is guaranteed that no other operations are concurrently active at this time.
    /// </summary>
    public override void BatchPostprocess()
    {
      if (nextIndexPendingTranspositionRoots > 0)
      {
        // Add in all the accumulated transposition roots to the dictionary.
        // This is done in postprocessing for efficiency because it obviates any locking.
        for (int i = 0; i < nextIndexPendingTranspositionRoots - 1; i++)
        {
          TranspositionRoots.TryAdd(pendingTranspositionRoots[i].Item1, pendingTranspositionRoots[i].Item2);
        }

        nextIndexPendingTranspositionRoots = 0;
      }
    }

  }

}
