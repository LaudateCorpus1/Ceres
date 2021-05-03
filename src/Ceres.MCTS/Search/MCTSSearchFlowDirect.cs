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
using System.Threading.Tasks;
using System.Collections.Generic;

using Ceres.MCTS.MTCSNodes;
using Ceres.Base.DataTypes;
using System.Diagnostics;
using Ceres.Base.Environment;
using Ceres.MCTS.MTCSNodes.Struct;
using Ceres.MCTS.Iteration;
using Ceres.MCTS.Params;
using Ceres.MCTS.Environment;
using Ceres.Chess.NNEvaluators;

#endregion

namespace Ceres.MCTS.Search
{
  /// <summary>
  /// Primary coordinator of an MCTS search, orhestrating the
  /// dual overlapped selectors.
  /// 
  /// See the comments below for a sketch of the batch gathering/submission algorithm.
  /// </summary>
  public partial class MCTSSearchFlow
  {
    public static float LastNNIdleTimeSecs = 0;
    public static float TotalNNIdleTimeSecs = 0;
    public static float TotalNNWaitTimeSecs = 0;

    const bool DUMP_WAITING = false;

    internal const int MAX_PRELOAD_NODES_PER_BATCH = 16;


    void WaitEvaluationDoneAndApply(Task<MCTSNodesSelectedSet> finalizingTask, int curCount = 0)
    {
      if (finalizingTask != null)
      {
        DateTime timeStartedWait = default;

        bool waited = false;
        if (finalizingTask.IsCompleted)
        {
          LastNNIdleTimeSecs = (float)(DateTime.Now - timeLastNNFinished).TotalSeconds;
          TotalNNIdleTimeSecs += LastNNIdleTimeSecs;

          if (DUMP_WAITING) Console.Write($"Wait {LastNNIdleTimeSecs * 100.0f,6:F2}ms GC={GC.CollectionCount(0)} cur= {curCount} last= ");
          waited = true;
        }
        else
        {
          if (DUMP_WAITING) Console.Write($"Nowait ms GC={GC.CollectionCount(0)} cur= {curCount} last= ");

          LastNNIdleTimeSecs = 0;

          // Since we are waiting here for at least some amount of time,
          // give hint to garbage collector that it might want to 
          // do a collection now if getting close to the need
          GC.Collect(1, GCCollectionMode.Optimized, true);

          if (!finalizingTask.IsCompleted)
          {
            if (CeresEnvironment.MONITORING_METRICS) timeStartedWait = DateTime.Now;
          }
        }

        finalizingTask.Wait();

        MCTSNodesSelectedSet resultNodes = finalizingTask.Result;

        if (timeStartedWait != default)
          TotalNNWaitTimeSecs += (float)(DateTime.Now - timeStartedWait).TotalSeconds;

        if (DUMP_WAITING && waited && resultNodes != null)
          Console.WriteLine(resultNodes.NodesNN.Count);

        if (resultNodes != null)
        {
          resultNodes.ApplyAll();
        }

      }
    }


    // Algorithm
    //   priorEvaluateTask <- null
    //   priorNodesNN <- null
    //   do
    //   { 
    //     // Select new nodes
    //     newNodes <- Select()
    //    
    //     // Remove any which may have been already selected by alternate selector
    //     newNodes <- Deduplicate(newNodes, priorNodesNN)
    //     
    //     // Check for those that can be immediately evaluated and split them out
    //     (newNodesNN, newNodesImm) <- TryEvalImmediateAndPartition(newNodes)
    //     if (OUT_OF_ORDER_ENABLED) BackupApply(newNodesImm) 
    //     
    //     // Launch evaluation of new nodes which need NN evaluation
    //     newEvaluateTask <- new Task(Evaluate(newNodesNN))
    //     
    //     // Wait for prior NN evaluation to finish and apply nodes
    //     if (priorEvaluateTask != null)
    //     {
    //       priorNodesNN <- Wait(priorEvaluateTask)
    //       BackupApply(priorNodesNN)
    //     }
    //     
    //     if (!OUT_OF_ORDER_ENABLED) BackupApply(newNodesImm)
    //
    //     // Prepare to cycle again       
    //     priorEvaluateTask <- newEvaluateTask
    //   } until (end of search)
    // 
    //   // Finalize last batch
    //   priorNodesNN <- Wait(priorEvaluateTask)
    //   BackupApply(priorNodesNN)


    public void ProcessDirectOverlapped(MCTSManager manager, int hardLimitNumNodes, int startingBatchSequenceNum, int? forceBatchSize)
    {
      Debug.Assert(!manager.Root.IsInFlight);
      if (hardLimitNumNodes == 0) hardLimitNumNodes = 1;

      bool overlappingAllowed = Context.ParamsSearch.Execution.FlowDirectOverlapped;
      int initialRootN = Context.Root.N;

      int guessMaxNumLeaves = NNEvaluator.MAX_BATCH_SIZE;

      ILeafSelector selector1;
      ILeafSelector selector2;

      selector1 = new LeafSelectorMulti(Context, 0, Context.StartPosAndPriorMoves, guessMaxNumLeaves);
      int secondSelectorID = Context.ParamsSearch.Execution.FlowDualSelectors ? 1 : 0;
      selector2 = overlappingAllowed ? new LeafSelectorMulti(Context, secondSelectorID, Context.StartPosAndPriorMoves, guessMaxNumLeaves) : null;

      MCTSNodesSelectedSet[] nodesSelectedSets = new MCTSNodesSelectedSet[overlappingAllowed ? 2 : 1];
      for (int i = 0; i < nodesSelectedSets.Length; i++) 
      {
        nodesSelectedSets[i] = new MCTSNodesSelectedSet(Context, 
                                                        i == 0 ? (LeafSelectorMulti)selector1 
                                                               : (LeafSelectorMulti)selector2,
                                                        guessMaxNumLeaves, guessMaxNumLeaves, BlockApply,
                                                        Context.ParamsSearch.Execution.InFlightThisBatchLinkageEnabled,
                                                        Context.ParamsSearch.Execution.InFlightOtherBatchLinkageEnabled);
      }

      int selectorID = 0;
      int batchSequenceNum = startingBatchSequenceNum;

      Task<MCTSNodesSelectedSet> overlappingTask = null;
      MCTSNodesSelectedSet pendingOverlappedNodes = null;
      int numOverlappedNodesImmediateApplied = 0;

      int iterationCount = 0;
      int numSelected = 0;
      int nodesLastSecondaryNetEvaluation = 0;
      while (true)
      {
        // Only start overlapping past 3000 nodes because
        // CPU latency will be very small at small tree sizes,
        // obviating the overlapping beneifts of hiding this latency.
        bool overlapThisSet = overlappingAllowed && numSelected > 3000;

        iterationCount++;
        Context.Tree.BATCH_SEQUENCE_COUNTER++;

        ILeafSelector selector = selectorID == 0 ? selector1 : selector2;

        float thisBatchDynamicVLossBoost = batchingManagers[selectorID].VLossDynamicBoostForSelector();

        // Call progress callback and check if reached search limit
        Context.ProgressCallback?.Invoke(manager);
        Manager.UpdateSearchStopStatus();
        if (Manager.StopStatus != MCTSManager.SearchStopStatus.Continue)
          break;

        int numCurrentlyOverlapped = Context.Root.NInFlight + Context.Root.NInFlight2;

        int numApplied = Context.Root.N - initialRootN;
        int hardLimitNumNodesThisBatch = int.MaxValue;
        if (hardLimitNumNodes > 0)
        {
          // Subtract out number already applied or in flight
          hardLimitNumNodesThisBatch = hardLimitNumNodes - (numApplied + numCurrentlyOverlapped);

          // Stop search if we have already exceeded search limit
          // or if remaining number is very small relative to full search
          // (this avoids incurring latency with a few small batches at end of a search).
          if (hardLimitNumNodesThisBatch <= numApplied / 1000) break;
        }

        //          Console.WriteLine($"Remap {targetThisBatch} ==> {Context.Root.N} {TargetBatchSize(Context.EstimatedNumSearchNodes, Context.Root.N)}");
        int targetThisBatch = OptimalBatchSizeCalculator.CalcOptimalBatchSize(Manager.EstimatedNumSearchNodes, Context.Root.N,
                                                                              overlapThisSet,                                                                    
                                                                              Context.ParamsSearch.Execution.FlowDualSelectors,
                                                                              Context.ParamsSearch.Execution.MaxBatchSize,
                                                                              Context.ParamsSearch.BatchSizeMultiplier);

        targetThisBatch = Math.Min(targetThisBatch, Manager.MaxBatchSizeDueToPossibleNearTimeExhaustion);
        if (forceBatchSize.HasValue) targetThisBatch = forceBatchSize.Value;
        if (targetThisBatch > hardLimitNumNodesThisBatch)
        {
          targetThisBatch = hardLimitNumNodesThisBatch;
        }

        int thisBatchTotalNumLeafsTargeted = 0;

        // Compute number of dynamic nodes to add (do not add any when tree is very small and impure child selection is particularly deleterious)
        int numNodesPadding = 0;
        if (manager.Root.N > 50 && manager.Context.ParamsSearch.PaddedBatchSizing)
          numNodesPadding = manager.Context.ParamsSearch.PaddedExtraNodesBase
                          + (int)(targetThisBatch * manager.Context.ParamsSearch.PaddedExtraNodesMultiplier);
        int numVisitsTryThisBatch = targetThisBatch + numNodesPadding;

        numVisitsTryThisBatch = (int)(numVisitsTryThisBatch * batchingManagers[selectorID].BatchSizeDynamicScaleForSelector());

        // Select a batch using this selector
        // It will select a set of Leafs completely independent of what a possibly other selector already selected
        // It may find some unevaluated leafs in the tree (extant but N = 0) due to action of the other selector
        // These leafs will nevertheless be recorded but specifically ignored later
        MCTSNodesSelectedSet nodesSelectedSet = nodesSelectedSets[selectorID];
        nodesSelectedSet.Reset(pendingOverlappedNodes);

        // Select the batch of nodes  
        if (numVisitsTryThisBatch < 5 || !Context.ParamsSearch.Execution.FlowSplitSelects)
        {
          thisBatchTotalNumLeafsTargeted += numVisitsTryThisBatch;
          ListBounded<MCTSNode> selectedNodes = selector.SelectNewLeafBatchlet(Context.Root, numVisitsTryThisBatch, thisBatchDynamicVLossBoost);
          nodesSelectedSet.AddSelectedNodes(selectedNodes, true);
        }
        else
        {
          // Set default assumed max batch size
          nodesSelectedSet.MaxNodesNN = numVisitsTryThisBatch;

          // In first attempt try to get 60% of target
          int numTry1 = Math.Max(1, (int)(numVisitsTryThisBatch * 0.60f));
          int numTry2 = (int)(numVisitsTryThisBatch * 0.40f);
          thisBatchTotalNumLeafsTargeted += numTry1;

          ListBounded<MCTSNode> selectedNodes1 = selector.SelectNewLeafBatchlet(Context.Root, numTry1, thisBatchDynamicVLossBoost);
          nodesSelectedSet.AddSelectedNodes(selectedNodes1, true);
          int numGot1 = nodesSelectedSet.NumNewLeafsAddedNonDuplicates;
          nodesSelectedSet.ApplyImmeditateNotYetApplied();

          // In second try target remaining 40%
          if (Context.ParamsSearch.Execution.SmartSizeBatches
           && Context.EvaluatorDef.NumDevices == 1
           && Context.NNEvaluators.PerfStatsPrimary != null) // TODO: somehow handle this for multiple GPUs
          {
            int[] optimalBatchSizeBreaks;
            if (Context.NNEvaluators.PerfStatsPrimary.Breaks != null)
              optimalBatchSizeBreaks = Context.NNEvaluators.PerfStatsPrimary.Breaks;
            else
              optimalBatchSizeBreaks = Context.GetOptimalBatchSizeBreaks(Context.EvaluatorDef.DeviceIndices[0]);

            // Make an educated guess about the total number of NN nodes that will be sent 
            // to the NN (resulting from both try1 and try2)
            // We base this on the fraction of nodes in try1 which actually are going to NN
            // then discounted by 0.8 because the yield on the second try is typically lower
            const float TRY2_SUCCESS_DISCOUNT_FACTOR = 0.8f;
            float fracNodesFirstTryGoingToNN = (float)nodesSelectedSet.NodesNN.Count / (float)numTry1;
            int estimatedAdditionalNNNodesTry2 = (int)(numTry2 * fracNodesFirstTryGoingToNN * TRY2_SUCCESS_DISCOUNT_FACTOR);

            int estimatedTotalNNNodes = nodesSelectedSet.NodesNN.Count + estimatedAdditionalNNNodesTry2;

            const float NEARBY_BREAK_FRACTION = 0.20f;
            int? closeByBreak = NearbyBreak(optimalBatchSizeBreaks, estimatedTotalNNNodes, NEARBY_BREAK_FRACTION);
            if (closeByBreak is not null)
            {
              nodesSelectedSet.MaxNodesNN = closeByBreak.Value;
            }

          }

          // Only try to collect the second half of the batch if the first one yielded
          // a good fraction of desired nodes (otherwise too many collisions to profitably continue)
          const float THRESHOLD_SUCCESS_TRY1 = 0.667f;
          bool shouldProcessTry2 = numTry1 < 10 || ((float)numGot1 / (float)numTry1) >= THRESHOLD_SUCCESS_TRY1;
          if (shouldProcessTry2)
          {
            thisBatchTotalNumLeafsTargeted += numTry2;
            ListBounded<MCTSNode> selectedNodes2 = selector.SelectNewLeafBatchlet(Context.Root, numTry2, thisBatchDynamicVLossBoost);

            // TODO: clean this up
            //  - Note that ideally we might not apply immeidate nodes here (i.e. pass false instead of true in next line)
            //  - This is because once done selecting nodes for this batch, we want to get it launched as soon as possible,
            //    we could defer and call ApplyImmeditateNotYetApplied only later (below)
            // *** WARNING*** However, setting this to false causes NInFlight errors (seen when running test matches within 1 or 2 minutes)
            nodesSelectedSet.AddSelectedNodes(selectedNodes2, true); // MUST BE true; see above
          }
        }

        // Possibly pad with "preload nodes"
        if (rootPreloader != null && nodesSelectedSet.NodesNN.Count <=  MCTSRootPreloader.PRELOAD_THRESHOLD_BATCH_SIZE)
        {
          // TODO: do we need to update thisBatchTotalNumLeafsTargeted ?
          TryAddRootPreloadNodes(manager, MAX_PRELOAD_NODES_PER_BATCH, nodesSelectedSet, selector);
        }

#if FEATURE_SUPPLEMENTAL
        //if (Context.ParamsSearch.TestFlag)
        {
          TryAddSupplementalNodes(manager, MAX_PRELOAD_NODES_PER_BATCH, nodesSelectedSet, selector);
        }
#endif

        // TODO: make flow private belows   
        if (Context.EvaluatorDef.SECONDARY_NETWORK_ID != null && (manager.Root.N - nodesLastSecondaryNetEvaluation > 500))
        {
          manager.RunSecondaryNetEvaluations(8, manager.flow.BlockNNEvalSecondaryNet);
          nodesLastSecondaryNetEvaluation = manager.Root.N;
        }

        // Update statistics
        numSelected += nodesSelectedSet.NumNewLeafsAddedNonDuplicates;
        UpdateStatistics(selectorID, thisBatchTotalNumLeafsTargeted, nodesSelectedSet);

        // Convert any excess nodes to CacheOnly
        if (Context.ParamsSearch.PaddedBatchSizing)
        {
          throw new Exception("Needs remediation");
          // Mark nodes not eligible to be applied as "cache only"
          //for (int i = numApplyThisBatch; i < selectedNodes.Count; i++)
          //  selectedNodes[i].ActionType = MCTSNode.NodeActionType.CacheOnly;
        }

        CeresEnvironment.LogInfo("MCTS", "Batch", $"Batch Target={numVisitsTryThisBatch} "
                                 + $"yields NN={nodesSelectedSet.NodesNN.Count} Immediate= {nodesSelectedSet.NodesImmediateNotYetApplied.Count} "
                                 + $"[CacheOnly={nodesSelectedSet.NumCacheOnly} None={nodesSelectedSet.NumNotApply}]", manager.InstanceID);

        // Now launch NN evaluation on the non-immediate nodes
        bool isPrimary = selectorID == 0;
        if (overlapThisSet)
        {
          Task<MCTSNodesSelectedSet> priorOverlappingTask = overlappingTask;

          numOverlappedNodesImmediateApplied = nodesSelectedSet.NodesImmediateNotYetApplied.Count;

          // Launch a new task to preprocess and evaluate these nodes
          overlappingTask = Task.Run(() => LaunchEvaluate(manager, targetThisBatch, isPrimary, nodesSelectedSet));
          nodesSelectedSet.ApplyImmeditateNotYetApplied();
          pendingOverlappedNodes = nodesSelectedSet;

          WaitEvaluationDoneAndApply(priorOverlappingTask, nodesSelectedSet.NodesNN.Count);
        }
        else
        {
          LaunchEvaluate(manager, targetThisBatch, isPrimary, nodesSelectedSet);
          nodesSelectedSet.ApplyAll();
          //Console.WriteLine("applied " + selector.Leafs.Count + " " + manager.Root);
        }

        if (manager.Root.N == 1)
        {
          manager.TerminationManager.ApplySearchMoves();
        }

        RunPeriodicMaintenance(manager, batchSequenceNum, iterationCount);

        // Advance (rotate) selector
        if (overlappingAllowed) selectorID = (selectorID + 1) % 2;
        batchSequenceNum++;
      }

      WaitEvaluationDoneAndApply(overlappingTask);

      //      Debug.Assert(!manager.Root.IsInFlight);

      if ((manager.Root.NInFlight != 0 || manager.Root.NInFlight2 != 0) && !haveWarned)
      {
        Console.WriteLine($"Internal error: search ended with N={manager.Root.N} NInFlight={manager.Root.NInFlight} NInFlight2={manager.Root.NInFlight2} " + manager.Root);
        int count = 0;
        manager.Root.Ref.TraverseSequential(manager.Root.Context.Tree.Store, delegate (ref MCTSNodeStruct node, MCTSNodeStructIndex index)
        {
          if (node.IsInFlight && node.NumChildrenVisited == 0 && count++ < 20)
            Console.WriteLine("  " + index.Index + " " + node.Terminal + " " + node.N + " " + node.IsTranspositionLinked + " " + node.NumNodesTranspositionExtracted);
          return true;
        });
        haveWarned = true;
      }

      selector1.Shutdown();
      selector2?.Shutdown();
    }


    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    static int? NearbyBreak(int[] breaks, int value, float maxDeviationFractionUpOrDown)
    {
      // Nothing to do if no breaks available
      if (breaks == null) return null;

      float min = value - value * maxDeviationFractionUpOrDown;
      float max = value + value * maxDeviationFractionUpOrDown;

      // Find break value which largest break less than targetTotalNumNodes
      for (int i = 0; i < breaks.Length; i++)
        if (breaks[i] >= min && breaks[i] < max)
          return breaks[i];

      return null;
    }


    private void UpdateStatistics(int selectorID, int numLeafsAttempted, MCTSNodesSelectedSet nodesSet)
    {
      if (nodesSet.NumNewLeafsAddedNonDuplicates > 0)
      {
        Context.NumNodeVisitsAttempted += numLeafsAttempted;
        Context.NumNodeVisitsSucceeded += nodesSet.NumNewLeafsAddedNonDuplicates;

        MCTSIterator.totalNumNodeVisitsAttempted += numLeafsAttempted;
        MCTSIterator.totalNumNodeVisitsSucceeded+= nodesSet.NumNewLeafsAddedNonDuplicates;

        float lastYield = (float)nodesSet.NumNewLeafsAddedNonDuplicates / (float)numLeafsAttempted;
        MCTSIterator.LastBatchYieldFrac = lastYield;
        batchingManagers[selectorID].UpdateVLossDynamicBoost(numLeafsAttempted, lastYield);
      }
    }

    bool rootNodeHasBeenInitialized = false;

    private void RunPeriodicMaintenance(MCTSManager manager, int batchSequenceNum, int iterationCount)
    {
      if (!rootNodeHasBeenInitialized && manager.Root.NumPolicyMoves > 0)
      {
        // We can only apply search noise after first node (so children initialized)
        manager.PossiblySetSearchNoise();
        rootNodeHasBeenInitialized = true;
      }

      // Use this time to perform housekeeping (tree is quiescent)
      if (batchSequenceNum % 3 == 2)
      {
        manager.UpdateEstimatedNPS();
        manager.TerminationManager.UpdatePruningFlags();
      }

      // Check if node cache needs pruning.
      Context.Tree?.PossiblyPruneCache();
    }



    bool haveWarned = false;


    /// <summary>
    /// Possibly run preloader to evaluate and cache 
    /// nodes near the root during early phases of search.
    /// 
    /// </summary>
    /// <param name="manager"></param>
    /// <param name="maxNodes"></param>
    /// <param name="selectedNodes"></param>
    /// <param name="selector"></param>
    private void TryAddRootPreloadNodes(MCTSManager manager, int maxNodes, 
                                        MCTSNodesSelectedSet selectedNodes, ILeafSelector selector)
    {
      if (rootPreloader == null) return;

      List<MCTSNode> rootPreloadNodes = rootPreloader.GetRootPreloadNodes(manager.Root, selector.SelectorID, maxNodes, MCTSRootPreloader.PRELOAD_MIN_P);

      if (rootPreloadNodes != null)
      {
        for (int i = 0; i < rootPreloadNodes.Count; i++)
        {
          MCTSNode node = rootPreloadNodes[i];
          selector.InsureAnnotated(node);
          selectedNodes.ProcessNode(node);
        }

      }
    }


#if FEATURE_SUPPLEMENTAL
    private void TryAddSupplementalNodes(MCTSManager manager, int maxNodes,
                                         MCTSNodesSelectedSet selectedNodes, ILeafSelector selector)
    {
      foreach ((MCTSNode parentNode, int selectorID, int childIndex) in ((LeafSelectorMulti)selector).supplementalCandidates) // TODO: remove cast
      {
        if (childIndex <= parentNode.NumChildrenExpanded -1)
        {
          // This child was already selected as part of the normal leaf gathering process.
          continue;
        }
        else
        {
          MCTSEventSource.TestCounter1++;

          // Record visit to this child in the parent (also increments the child NInFlight counter)
          parentNode.UpdateRecordVisitsToChild(selectorID, childIndex, 1);

          MCTSNode node = parentNode.CreateChild(childIndex);

          ((LeafSelectorMulti)selector).DoVisitLeafNode(node, 1);// TODO: remove cast

          if (!parentNode.IsRoot)
          {
            if (selectorID == 0)
              parentNode.Parent.Ref.BackupIncrementInFlight(1, 0);
            else
              parentNode.Parent.Ref.BackupIncrementInFlight(0, 1);
          }

          // Try to process this node
          int nodesBefore = selectedNodes.NodesNN.Count;
          selector.InsureAnnotated(node);
          selectedNodes.ProcessNode(node);
          bool wasSentToNN = selectedNodes.NodesNN.Count != nodesBefore;
          //if (wasSentToNN) MCTSEventSource.TestCounter2++;

          // dje: add counter?
        }
      }

    }
#endif

    enum ApplyMode { ApplyNone, ApplyIfImmediate, ApplyAll };

    DateTime timeLastNNFinished;


    private MCTSNodesSelectedSet LaunchEvaluate(MCTSManager manager, int numNodesTargeted,
                                                    bool isPrimary, MCTSNodesSelectedSet nodes)
    {
      if (nodes.NodesNN.Count == 0) return null;

      using (new SearchContextExecutionBlock(manager.Context))
      {
        if (nodes.NodesNN.Count > numNodesTargeted)
        {
          // Mark the excess nodes as "CacheOnly"
          for (int i = numNodesTargeted; i < nodes.NodesNN.Count; i++)
            nodes.NodesNN[i].ActionType = MCTSNode.NodeActionType.CacheOnly;
        }
        if (isPrimary)
          BlockNNEval1.Evaluate(manager.Context, nodes.NodesNN);
        else
          BlockNNEval2.Evaluate(manager.Context, nodes.NodesNN);

        timeLastNNFinished = DateTime.Now;
        return nodes;
      }

    }
  }
}
