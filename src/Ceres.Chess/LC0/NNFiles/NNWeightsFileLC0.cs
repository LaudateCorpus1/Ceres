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
using System.IO;
using Ceres.Chess.LC0.WeightsProtobuf;
using Ceres.Chess.NNFiles;
using Ceres.Chess.UserSettings;

#endregion
namespace Ceres.Chess.LC0.NNFiles
{
  /// <summary>
  /// Represents an NN weights file from an LC0 network (weights protobuf file).
  /// </summary>
  public class NNWeightsFileLC0 : INNWeightsFileInfo
  {
    /// <summary>
    /// Description of generator of weights file (e.g. Leela Chess Zero).
    /// </summary>
    public string GeneratorDescription => "LC0 weights file";

    /// <summary>
    /// Unique network ID (for example typically sequential numeric values such as "32930").
    /// </summary>
    public string NetworkID { get; }

    /// <summary>
    /// Path and name of file containing weights.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Number of blocks (layers) in the convolution section of the network.
    /// </summary>
    public int NumBlocks { get; }

    /// <summary>
    /// Number of convolutional filters.
    /// </summary>
    public int NumFilters { get; }

    /// <summary>
    /// If the network contains a WDL (win/draw/loss) value head.
    /// </summary>
    public bool IsWDL { get; }

    /// <summary>
    /// If the network contains an MLH (moves left head).
    /// </summary>
    public bool HasMovesLeft { get; }


    /// <summary>
    /// Information about the underlying file from the operating system.
    /// </summary>
    FileInfo fileInfo;

    /// <summary>
    /// Constructor for a file with information explicitly provided (obviating reading the protofile).
    /// </summary>
    /// <param name="id"></param>
    /// <param name="filename"></param>
    /// <param name="numBlocks"></param>
    /// <param name="numFilters"></param>
    /// <param name="isWDL"></param>
    /// <param name="hasMovesLeft"></param>
    public NNWeightsFileLC0(string id, string filename, int numBlocks, int numFilters, bool isWDL, bool hasMovesLeft)
    {
      if (!System.IO.File.Exists(filename)) throw new ArgumentException($"NNWeightsFileLC0 file {id} not found with name {filename}");

      NetworkID = id;
      FileName = filename;
      NumBlocks = numBlocks;
      NumFilters = numFilters;
      IsWDL = isWDL;
      HasMovesLeft = hasMovesLeft;

      fileInfo = new FileInfo(filename);
    }

    public LC0ProtobufNet Info => new LC0ProtobufNet(FileName);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="networkID"></param>
    /// <returns></returns>
    public static NNWeightsFileLC0 LookupOrDownload(string networkID)
    {
      // Try to load file from local disk, return if found.
      INNWeightsFileInfo existingFile = NNWeightsFiles.LookupNetworkFile(networkID, false);
      if (existingFile != null) return existingFile as NNWeightsFileLC0;

      // Download the file.
      string baseURL = CeresUserSettingsManager.URLLC0NetworksValidated;
      NNWeightsFileLC0Downloader downloader = new NNWeightsFileLC0Downloader(baseURL, 
                                                                             CeresUserSettingsManager.Settings.DirLC0Networks);
      string fn = downloader.Download(networkID);
      if (fn == null)
      {
        throw new Exception($"Failure in download of {networkID}");
      }

      // Load and return the file.
      return new NNWeightsFileLC0(networkID, fn);
    }

      /// <summary>
      /// Constructor from a file with a specified ID and file name (other information determined from reading protofile).
      /// </summary>
      /// <param name="id"></param>
      /// <param name="filename"></param>
    public NNWeightsFileLC0(string id, string filename)
    {
      if (!File.Exists(filename)) throw new ArgumentException($"NNWeightsFileLC0 file {id} not found with name {filename}");

      NetworkID = id;
      FileName = filename;
      fileInfo = new FileInfo(filename);

      try
      {
        LC0ProtobufNet pbn = new LC0ProtobufNet(filename);
        NumBlocks = pbn.NumBlocks;
        NumFilters = pbn.NumFilters;
        IsWDL = pbn.HasWDL;
        HasMovesLeft = pbn.HasMovesLeft;
      }
      catch (Exception exc)
      {
        throw new Exception($"Failure in parsing LC0 weights file: {filename}. File possibly corrupt or more recent format than supported. {exc}");
      }
    }

    /// <summary>
    /// Information about the underlying file from the operating system.
    /// </summary>
    public FileInfo FileInfo => fileInfo;


    /// <summary>
    /// Returns string summary.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
      return $"<NNWeightsFileLC0 {NetworkID} at {FileName} >";
    }

  }
}
