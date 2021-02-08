﻿#region License notice

/*
  This file is part of the Ceres project at https://github.com/dje-dev/ceres.
  Copyright (C) 2020- by David Elliott and the Ceres Authors.

  Ceres is free software under the terms of the GNU General Public License v3.0.
  You should have received a copy of the GNU General Public License
  along with Ceres. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System.Collections.Generic;

namespace Ceres.Features.Visualization.TreePlot
{
  /// <summary>
  /// Holds information about the draw tree needed for plotting and some general info shown in the plot title.
  /// </summary>
  public class DrawTreeInfo
  {
    public float maxX;
    public float maxDepth;
    public List<int> nodesPerDepth;
    public int nrLeafNodes;
    public int nrNodes;

    public DrawTreeInfo()
    {
      maxX = float.MinValue;
      maxDepth = float.MinValue;
      nodesPerDepth = new List<int>();
      nrNodes = 0;
      nrLeafNodes = 0;
    }
  }
}