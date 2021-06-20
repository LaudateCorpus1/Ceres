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
using Ceres.Base.OperatingSystem;
using Ceres.Chess;
using Ceres.Chess.PositionEvalCaching;
using Ceres.Chess.UserSettings;

#endregion

namespace Ceres.MCTS.Params
{
  public static class MCTSParamsFixed
  {
    public const bool TRACK_NODE_TREND = false;

    public const bool LARGE_HARDWARE_CONFIG = true;

   
    /// <summary>
    /// If operating system large pages (2MB each under Windows) 
    /// should be used for the arrays of raw nodes ahd child infos.
    /// 
    /// This potentially reduces memory access time, but requires
    /// elevated priveleges and sufficient contiguous memory available 
    /// (which cannot be paged)
    /// 
    /// NOTE: On a dual socket machine performance was clearly inferior with large pages.
    ///       On a single socket machine performance was considerably improved, although
    ///       a limitation (on Windows only) is that larges pages is incompatible 
    ///       with incremental allocation under Windows.
    ///       
    /// NOTE: On Linux sometimes large page allocations may fail and seemingly cannot 
    ///       be detected in the mreserve or mprotect call, causing access violations.
    /// </summary>
    public static bool TryEnableLargePages => CeresUserSettingsManager.Settings.UseLargePages && SoftwareManager.IsLinux ? true : false;

    /// <summary>
    /// Optionally the storage can make use of an another running process
    /// which has allocated space for the nodes and children (experimental).
    /// </summary>
    public const bool STORAGE_USE_EXISTING_SHARED_MEM = false;

    /// <summary>
    /// In incremental storage mode memory is reserved at initialization
    /// but only actually committed incrementally as the search tree grows.
    /// </summary>
    public const bool STORAGE_USE_INCREMENTAL_ALLOC = true;

    /// <summary>
    /// If enabled the largest possible search tree is about 2 billion nodes
    /// rather than the default of 1 billion nodes.
    /// 
    /// This should be enabled only if needed and obviously 
    /// only if the system has the required 250GB to 500GB of memory
    /// (because it slightly increases memory usage per node).
    /// </summary>
    public const bool ENABLE_MAX_SEARCH_TREE = false;

    /// <summary>
    /// 
    /// Possibly using Level1 helps. However possibly with large pages it is better to use None (?).
    /// </summary>
    public const MTCSNodes.Struct.MCTSNodeStruct.CacheLevel PrefetchCacheLevel = MTCSNodes.Struct.MCTSNodeStruct.CacheLevel.Level1;
  }
}
