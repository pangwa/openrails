﻿// COPYRIGHT 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

// This module covers all classes and code for signal, speed post, track occupation and track reservation control

// Debug flags :
// #define DEBUG_PRINT
// prints details of the derived signal structure
// #define DEBUG_REPORTS
// print details of train behaviour
// #define DEBUG_DEADLOCK
// print details of deadlock processing

using Microsoft.Xna.Framework;
using Orts.Formats.Msts;
using Orts.Formats.OR;
using Orts.MultiPlayer;
using Orts.Parsers.Msts;
using Orts.Simulation.AIs;
using Orts.Simulation.Physics;
using Orts.Simulation.Timetables;
using ORTS.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Event = Orts.Common.Event;

namespace Orts.Simulation.Signalling
{


    //================================================================================================//
    //
    // class Signals
    //
    //================================================================================================//

    public class Signals
    {

        //================================================================================================//
        // local data
        //================================================================================================//

        internal readonly Simulator Simulator;

        public TrackDB trackDB;
        private TrackSectionsFile tsectiondat;
        private TrackDatabaseFile tdbfile;

        private SignalObject[] signalObjects;
        private List<SignalWorldObject> SignalWorldList = new List<SignalWorldObject>();
        private Dictionary<uint, SignalRefObject> SignalRefList;
        private Dictionary<uint, SignalObject> SignalHeadList;
        public static SIGSCRfile scrfile;

        public int noSignals;
        private int foundSignals;

        private static int updatecount;

        public List<TrackCircuitSection> TrackCircuitList;
        private Dictionary<int, CrossOverItem> CrossoverList = new Dictionary<int, CrossOverItem>();
        public List<PlatformDetails> PlatformDetailsList = new List<PlatformDetails>();
        public Dictionary<int, int> PlatformXRefList = new Dictionary<int, int>();
        public Dictionary<string, List<int>> StationXRefList = new Dictionary<string, List<int>>();

        public bool UseLocationPassingPaths;                    // Use location-based style processing of passing paths (set by Simulator)
        public Dictionary<int, DeadlockInfo> DeadlockInfoList;  // each deadlock info has unique reference
        public int deadlockIndex;                               // last used reference index
        public Dictionary<int, int> DeadlockReference;          // cross-reference between trackcircuitsection (key) and deadlockinforeference (value)

        //================================================================================================//
        ///
        /// Constructor
        ///

        public Signals(Simulator simulator, SignalConfigurationFile sigcfg, CancellationToken cancellation)
        {
            Simulator = simulator;

#if DEBUG_REPORTS
            File.Delete(@"C:\temp\printproc.txt");
#endif

            SignalRefList = new Dictionary<uint, SignalRefObject>();
            SignalHeadList = new Dictionary<uint, SignalObject>();
            Dictionary<int, int> platformList = new Dictionary<int, int>();

            trackDB = simulator.TDB.TrackDB;
            tsectiondat = simulator.TSectionDat;
            tdbfile = Simulator.TDB;

            // read SIGSCR files

            Trace.Write(" SIGSCR ");
            scrfile = new SIGSCRfile(new SignalScripts(sigcfg.ScriptPath, sigcfg.ScriptFiles, sigcfg.SignalTypes));

            // build list of signal world file information

            BuildSignalWorld(simulator, sigcfg, cancellation);

            // build list of signals in TDB file

            BuildSignalList(trackDB.TrItemTable, trackDB.TrackNodes, tsectiondat, tdbfile, platformList);

            if (foundSignals > 0)
            {
                // Add CFG info

                AddCFG(sigcfg);

                // Add World info

                AddWorldInfo();

                // check for any backfacing heads in signals
                // if found, split signal

                SplitBackfacing(trackDB.TrItemTable, trackDB.TrackNodes);
            }

            if (SignalObjects != null)
                SetNumSignalHeads();

            //
            // Create trackcircuit database
            //
#if ACTIVITY_EDITOR
            CreateTrackCircuits(trackDB.TrItemTable, trackDB.TrackNodes, tsectiondat, simulator.orRouteConfig);
#else
            CreateTrackCircuits(trackDB.TrItemTable, trackDB.TrackNodes, tsectiondat);
#endif

            //
            // Process platform information
            //

            ProcessPlatforms(platformList, trackDB.TrItemTable, trackDB.TrackNodes);

            //
            // Process tunnel information
            //

            ProcessTunnels();

            //
            // Process trough information
            //

            ProcessTroughs();

            //
            // Print all info (DEBUG only)
            //

#if DEBUG_PRINT

            PrintTCBase(trackDB.TrackNodes);

            if (File.Exists(@"C:\temp\SignalObjects.txt"))
            {
                File.Delete(@"C:\temp\SignalObjects.txt");
            }
            if (File.Exists(@"C:\temp\SignalShapes.txt"))
            {
                File.Delete(@"C:\temp\SignalShapes.txt");
            }

			var sob = new StringBuilder();
            for (var isignal = 0; isignal < signalObjects.Length - 1; isignal++)
            {
				var singleSignal = signalObjects[isignal];
                if (singleSignal == null)
                {
					sob.AppendFormat("\nInvalid entry : {0}\n", isignal);
                }
                else
                {
					sob.AppendFormat("\nSignal ref item     : {0}\n", singleSignal.thisRef);
					sob.AppendFormat("Track node + index  : {0} + {1}\n", singleSignal.trackNode, singleSignal.trRefIndex);

                    foreach (var thisHead in singleSignal.SignalHeads)
                    {
						sob.AppendFormat("Type name           : {0}\n", thisHead.signalType.Name);
						sob.AppendFormat("Type                : {0}\n", thisHead.signalType.FnType);
						sob.AppendFormat("item Index          : {0}\n", thisHead.trItemIndex);
						sob.AppendFormat("TDB  Index          : {0}\n", thisHead.TDBIndex);
						sob.AppendFormat("Junction Main Node  : {0}\n", thisHead.JunctionMainNode);
						sob.AppendFormat("Junction Path       : {0}\n", thisHead.JunctionPath);
                    }

					sob.AppendFormat("TC Reference   : {0}\n", singleSignal.TCReference);
					sob.AppendFormat("TC Direction   : {0}\n", singleSignal.TCDirection);
					sob.AppendFormat("TC Position    : {0}\n", singleSignal.TCOffset);
					sob.AppendFormat("TC TCNextTC    : {0}\n", singleSignal.TCNextTC);
                }
            }
			File.AppendAllText(@"C:\temp\SignalObjects.txt", sob.ToString());

			var ssb = new StringBuilder();
            foreach (var sshape in sigcfg.SignalShapes)
            {
				var thisshape = sshape.Value;
				ssb.Append("\n==========================================\n");
				ssb.AppendFormat("Shape key   : {0}\n", sshape.Key);
				ssb.AppendFormat("Filename    : {0}\n", thisshape.ShapeFileName);
				ssb.AppendFormat("Description : {0}\n", thisshape.Description);

                foreach (var ssobj in thisshape.SignalSubObjs)
                {
					ssb.AppendFormat("\nSubobj Index : {0}\n", ssobj.Index);
					ssb.AppendFormat("Matrix       : {0}\n", ssobj.MatrixName);
					ssb.AppendFormat("Description  : {0}\n", ssobj.Description);
					ssb.AppendFormat("Sub Type (I) : {0}\n", ssobj.SignalSubType);
                    if (ssobj.SignalSubSignalType != null)
                    {
						ssb.AppendFormat("Sub Type (C) : {0}\n", ssobj.SignalSubSignalType);
                    }
                    else
                    {
						ssb.AppendFormat("Sub Type (C) : not set \n");
                    }
					ssb.AppendFormat("Optional     : {0}\n", ssobj.Optional);
					ssb.AppendFormat("Default      : {0}\n", ssobj.Default);
					ssb.AppendFormat("BackFacing   : {0}\n", ssobj.BackFacing);
					ssb.AppendFormat("JunctionLink : {0}\n", ssobj.JunctionLink);
                }
				ssb.Append("\n==========================================\n");
            }
			File.AppendAllText(@"C:\temp\SignalShapes.txt", ssb.ToString());
#endif

            // Clear world lists to save memory

            SignalWorldList.Clear();
            SignalRefList.Clear();
            SignalHeadList.Clear();

            if (SignalObjects != null)
            {
                foreach (SignalObject thisSignal in SignalObjects)
                {
                    if (thisSignal != null)
                    {
                        if (thisSignal.isSignalNormal())
                        {
                            if (thisSignal.TCNextTC < 0)
                            {
                                Trace.TraceInformation("Signal " + thisSignal.thisRef +
                                    " ; TC : " + thisSignal.TCReference +
                                    " ; NextTC : " + thisSignal.TCNextTC +
                                    " ; TN : " + thisSignal.trackNode);
                            }

                            if (thisSignal.TCReference < 0) // signal is not on any track - remove it!
                            {
                                Trace.TraceInformation("Signal removed " + thisSignal.thisRef +
                                    " ; TC : " + thisSignal.TCReference +
                                    " ; NextTC : " + thisSignal.TCNextTC +
                                    " ; TN : " + thisSignal.trackNode);
                                SignalObjects[thisSignal.thisRef] = null;
                            }
                        }
                    }
                }
            }

            DeadlockInfoList = new Dictionary<int, DeadlockInfo>();
            deadlockIndex = 1;
            DeadlockReference = new Dictionary<int, int>();
        }

        //================================================================================================//
        ///
        /// Overlay constructor for restore after saved game
        ///

        public Signals(Simulator simulator, SignalConfigurationFile sigcfg, BinaryReader inf, CancellationToken cancellation)
            : this(simulator, sigcfg, cancellation)
        {
            int signalIndex = inf.ReadInt32();
            while (signalIndex >= 0)
            {
                SignalObject thisSignal = SignalObjects[signalIndex];
                thisSignal.Restore(simulator, inf);
                signalIndex = inf.ReadInt32();
            }

            int tcListCount = inf.ReadInt32();

            if (tcListCount != TrackCircuitList.Count)
            {
                Trace.TraceError("Mismatch between saved : {0} and existing : {1} TrackCircuits", tcListCount, TrackCircuitList.Count);
                throw new InvalidDataException("Cannot resume route due to altered data");
            }
            else
            {
                foreach (TrackCircuitSection thisSection in TrackCircuitList)
                {
                    thisSection.Restore(simulator, inf);
                }
            }

            UseLocationPassingPaths = inf.ReadBoolean();

            DeadlockInfoList = new Dictionary<int, DeadlockInfo>();
            int totalDeadlocks = inf.ReadInt32();
            for (int iDeadlock = 0; iDeadlock <= totalDeadlocks - 1; iDeadlock++)
            {
                int thisDeadlockIndex = inf.ReadInt32();
                DeadlockInfo thisInfo = new DeadlockInfo(this, inf);
                DeadlockInfoList.Add(thisDeadlockIndex, thisInfo);
            }

            deadlockIndex = inf.ReadInt32();

            DeadlockReference = new Dictionary<int, int>();
            int totalReferences = inf.ReadInt32();
            for (int iReference = 0; iReference <= totalReferences - 1; iReference++)
            {
                int thisSectionIndex = inf.ReadInt32();
                int thisDeadlockIndex = inf.ReadInt32();
                DeadlockReference.Add(thisSectionIndex, thisDeadlockIndex);
            }
        }

        //================================================================================================//
        //
        // Restore Train links
        // Train links must be restored separately as Trains is restored later as Signals
        //

        public void RestoreTrains(List<Train> trains)
        {
            foreach (TrackCircuitSection thisSection in TrackCircuitList)
            {
                thisSection.CircuitState.RestoreTrains(trains);
            }

            // restore train information

            if (SignalObjects != null)
            {
                foreach (SignalObject thisSignal in SignalObjects)
                {
                    if (thisSignal != null)
                    {
                        thisSignal.RestoreTrains(trains);
                    }
                }

                // restore correct aspects
                foreach (SignalObject thisSignal in SignalObjects)
                {
                    if (thisSignal != null)
                    {
                        thisSignal.RestoreAspect();
                    }
                }
            }
        }

        //================================================================================================//
        ///
        /// Save game
        ///

        public void Save(BinaryWriter outf)
        {
            if (SignalObjects != null)
            {
                foreach (SignalObject thisSignal in SignalObjects)
                {
                    if (thisSignal != null)
                    {
                        outf.Write(thisSignal.thisRef);
                        thisSignal.Save(outf);
                    }
                }
            }
            outf.Write(-1);

            outf.Write(TrackCircuitList.Count);
            foreach (TrackCircuitSection thisSection in TrackCircuitList)
            {
                thisSection.Save(outf);
            }

            outf.Write(UseLocationPassingPaths);

            outf.Write(DeadlockInfoList.Count);
            foreach (KeyValuePair<int, DeadlockInfo> deadlockDetails in DeadlockInfoList)
            {
                outf.Write(deadlockDetails.Key);
                deadlockDetails.Value.Save(outf);
            }

            outf.Write(deadlockIndex);

            outf.Write(DeadlockReference.Count);
            foreach (KeyValuePair<int, int> referenceDetails in DeadlockReference)
            {
                outf.Write(referenceDetails.Key);
                outf.Write(referenceDetails.Value);
            }

        }

        //================================================================================================//
        /// 
        /// Gets an array of all the SignalObjects.
        ///

        public SignalObject[] SignalObjects
        {
            get
            {
                return signalObjects;
            }
        }

        //================================================================================================//
        ///
        /// Read all world files to get signal flags
        ///

        private void BuildSignalWorld(Simulator simulator, SignalConfigurationFile sigcfg, CancellationToken cancellation)
        {

            // get all filesnames in World directory

            var WFilePath = simulator.RoutePath + @"\WORLD\";

            var Tokens = new List<TokenID>();
            Tokens.Add(TokenID.Signal);

            // loop through files, use only extention .w, skip w+1000000+1000000.w file

            foreach (var fileName in Directory.GetFiles(WFilePath, "*.w"))
            {
                if (cancellation.IsCancellationRequested) return; // ping loader watchdog
                // validate file name a little bit

                if (Path.GetFileName(fileName).Length != 17)
                    continue;

                // read w-file, get SignalObjects only

                Trace.Write("W");
                WorldFile WFile;
                try
                {
                    WFile = new WorldFile(fileName, Tokens);
                }
                catch (Exception error)
                {
                    Trace.WriteLine(new FileLoadException(fileName, error));
                    continue;
                }

                // loop through all signals

                foreach (var worldObject in WFile.Tr_Worldfile)
                {
                    if (worldObject.GetType() == typeof(SignalObj))
                    {
                        var thisWorldObject = worldObject as SignalObj;
                        if (thisWorldObject.SignalUnits == null) continue; //this has no unit, will ignore it and treat it as static in scenary.cs

                        //check if signalheads are on same or adjacent tile as signal itself - otherwise there is an invalid match
                        uint? BadSignal = null;
                        foreach (var si in thisWorldObject.SignalUnits.Units)
                        {
                            if (this.trackDB.TrItemTable == null || si.TrItem >= this.trackDB.TrItemTable.Count())
                            {
                                BadSignal = si.TrItem;
                                break;
                            }
                            var item = this.trackDB.TrItemTable[si.TrItem];
                            if (Math.Abs(item.TileX - WFile.TileX) > 1 || Math.Abs(item.TileZ - WFile.TileZ) > 1)
                            {
                                BadSignal = si.TrItem;
                                break;
                            }
                        }
                        if (BadSignal.HasValue)
                        {
                            Trace.TraceWarning("Signal referenced in .w file {0} {1} as TrItem {2} not present in .tdb file ", WFile.TileX, WFile.TileZ, BadSignal.Value);
                            continue;
                        }

                        // if valid, add signal

                        var SignalWorldSignal = new SignalWorldObject(thisWorldObject, sigcfg);
                        SignalWorldList.Add(SignalWorldSignal);
                        foreach (var thisref in SignalWorldSignal.HeadReference)
                        {
                            var thisSignalCount = SignalWorldList.Count() - 1;    // Index starts at 0
                            var thisRefObject = new SignalRefObject(thisSignalCount, thisref.Value);
                            if (!SignalRefList.ContainsKey(thisref.Key))
                            {
                                SignalRefList.Add(thisref.Key, thisRefObject);
                            }
                        }
                    }
                }
            }

#if DEBUG_PRINT
			var srlb = new StringBuilder();
            foreach (var thisref in SignalRefList)
            {
                var TBDRef = thisref.Key;
				var signalRef = thisref.Value;
                var reffedObject = SignalWorldList[(int)signalRef.SignalWorldIndex];
                uint headref;
                if (!reffedObject.HeadReference.TryGetValue(TBDRef, out headref))
                {
                    srlb.AppendFormat("Incorrect Ref : {0}\n", TBDRef);
                    foreach (var headindex in reffedObject.HeadReference)
                    {
						srlb.AppendFormat("TDB : {0} + {1}\n", headindex.Key, headindex.Value);
                    }
                }
            }
			File.AppendAllText(@"WorldSignalList.txt", srlb.ToString());
#endif

        }  //BuildSignalWorld


        //================================================================================================//
        /// 
        /// Update : perform signal updates
        /// 

        public void Update(bool preUpdate)
        {
            if (MPManager.IsClient()) return; //in MP, client will not update

            if (foundSignals > 0)
            {

                // loop through all signals
                // update required part
                // in preupdate, process all

                int totalSignal = signalObjects.Length - 1;

                int updatestep = (totalSignal / 20) + 1;
                if (preUpdate)
                {
                    updatestep = totalSignal;
                }

                for (int icount = updatecount; icount < Math.Min(totalSignal, updatecount + updatestep); icount++)
                {
                    SignalObject signal = signalObjects[icount];
                    if (signal != null) // to cater for orphans
                    {
                        signal.Update();
                    }
                }

                updatecount += updatestep;
                updatecount = updatecount > totalSignal ? 0 : updatecount;
            }
        }  //Update

        //================================================================================================//
        ///
        /// Build signal list from TDB
        ///

        private void BuildSignalList(TrItem[] TrItems, TrackNode[] trackNodes, TrackSectionsFile tsectiondat,
                TrackDatabaseFile tdbfile, Dictionary<int, int> platformList)
        {

            //  Determaine the number of signals in the track Objects list

            noSignals = 0;
            if (TrItems == null)
                return;                // No track Objects in route.
            foreach (TrItem trItem in TrItems)
            {
                if (trItem != null)
                {
                    if (trItem.ItemType == TrItem.trItemType.trSIGNAL)
                    {
                        noSignals++;
                    }
                    else if (trItem.ItemType == TrItem.trItemType.trSPEEDPOST)
                    {
                        SpeedPostItem Speedpost = (SpeedPostItem)trItem;
                        if (Speedpost.IsLimit)
                        {
                            noSignals++;
                        }
                    }
                }
            }

            //  Only continue if one or more signals in route.

            if (noSignals > 0)
            {
                signalObjects = new SignalObject[noSignals];
                SignalObject.signalObjects = signalObjects;
            }
            SignalObject.trackNodes = trackNodes;
            SignalObject.trItems = TrItems;

            for (int i = 1; i < trackNodes.Length; i++)
            {
                ScanSection(TrItems, trackNodes, i, tsectiondat, tdbfile, platformList);
            }
            if (noSignals > 0)
            {
                // using world cross-reference list, merge heads to single signal

                MergeHeads();

                // rebuild list - clear out null elements

                int firstfree = -1;
                for (int iSignal = 0; iSignal < SignalObjects.Length; iSignal++)
                {
                    if (SignalObjects[iSignal] == null && firstfree < 0)
                    {
                        firstfree = iSignal;
                    }
                    else if (SignalObjects[iSignal] != null && firstfree >= 0)
                    {
                        SignalObjects[firstfree] = SignalObjects[iSignal];
                        SignalObjects[iSignal] = null;
                        firstfree++;
                    }
                }

                if (firstfree < 0)
                    firstfree = SignalObjects.Length - 1;

                // restore all links and indices

                for (var iSignal = 0; iSignal < SignalObjects.Length; iSignal++)
                {
                    if (SignalObjects[iSignal] != null)
                    {
                        var thisObject = SignalObjects[iSignal];
                        thisObject.thisRef = iSignal;

                        foreach (var thisHead in thisObject.SignalHeads)
                        {
                            thisHead.mainSignal = thisObject;
                            var trackItem = TrItems[thisHead.TDBIndex];
                            var sigItem = trackItem as SignalItem;
                            var speedItem = trackItem as SpeedPostItem;
                            if (sigItem != null)
                            {
                                sigItem.SigObj = thisObject.thisRef;
                            }
                            else if (speedItem != null)
                            {
                                speedItem.SigObj = thisObject.thisRef;
                            }
                        }
                    }
                }

                foundSignals = firstfree;

            }
            else
            {
                signalObjects = new SignalObject[0];
            }

        } //BuildSignalList


        //================================================================================================//
        ///
        /// Split backfacing signals
        ///

        private void SplitBackfacing(TrItem[] TrItems, TrackNode[] TrackNodes)
        {

            List<SignalObject> newSignals = new List<SignalObject>();
            int newindex = foundSignals; //the last was placed into foundSignals-1, thus the new ones need to start from foundSignals

            //
            // Loop through all signals to check on Backfacing heads
            //

            for (int isignal = 0; isignal < signalObjects.Length - 1; isignal++)
            {
                SignalObject singleSignal = signalObjects[isignal];
                if (singleSignal != null && singleSignal.isSignal &&
                                singleSignal.WorldObject != null && singleSignal.WorldObject.Backfacing.Count > 0)
                {

                    //
                    // create new signal - copy of existing signal
                    // use Backfacing flags and reset head indication
                    //

                    SignalObject newSignal = new SignalObject(singleSignal);

                    newSignal.thisRef = newindex;
                    newSignal.signalRef = this;
                    newSignal.trRefIndex = 0;

                    newSignal.WorldObject.FlagsSet = new bool[singleSignal.WorldObject.FlagsSetBackfacing.Length];
                    singleSignal.WorldObject.FlagsSetBackfacing.CopyTo(newSignal.WorldObject.FlagsSet, 0);

                    for (int iindex = 0; iindex < newSignal.WorldObject.HeadsSet.Length; iindex++)
                    {
                        newSignal.WorldObject.HeadsSet[iindex] = false;
                    }

                    //
                    // loop through the list with headreferences, check this agains the list with backfacing heads
                    // use the TDBreference to find the actual head
                    //

                    List<int> removeHead = new List<int>();  // list to keep trace of heads which are moved //

                    foreach (KeyValuePair<uint, uint> thisHeadRef in singleSignal.WorldObject.HeadReference)
                    {
                        for (int iindex = singleSignal.WorldObject.Backfacing.Count - 1; iindex >= 0; iindex--)
                        {
                            int ihead = singleSignal.WorldObject.Backfacing[iindex];
                            if (thisHeadRef.Value == ihead)
                            {
                                for (int ihIndex = 0; ihIndex < singleSignal.SignalHeads.Count; ihIndex++)
                                {
                                    SignalHead thisHead = singleSignal.SignalHeads[ihIndex];

                                    //
                                    // backfacing head found - add to new signal, set to remove from exising signal
                                    //

                                    if (thisHead.TDBIndex == thisHeadRef.Key)
                                    {
                                        removeHead.Add(ihIndex);

                                        thisHead.mainSignal = newSignal;
                                        newSignal.SignalHeads.Add(thisHead);
                                    }
                                }
                            }

                            //
                            // update flags for available heads
                            //

                            newSignal.WorldObject.HeadsSet[ihead] = true;
                            singleSignal.WorldObject.HeadsSet[ihead] = false;
                        }
                    }

                    //
                    // check if there were actually any backfacing signal heads
                    //

                    if (removeHead.Count > 0)
                    {

                        //
                        // remove moved heads from existing signal
                        //

                        for (int ihead = singleSignal.SignalHeads.Count - 1; ihead >= 0; ihead--)
                        {
                            if (removeHead.Contains(ihead))
                            {
                                singleSignal.SignalHeads.RemoveAt(ihead);
                            }
                        }

                        //
                        // Check direction of heads to set correct direction for signal
                        //

                        if (singleSignal.SignalHeads.Count > 0)
                        {
                            SignalItem thisItemOld = TrItems[singleSignal.SignalHeads[0].TDBIndex] as SignalItem;
                            if (singleSignal.direction != thisItemOld.Direction)
                            {
                                singleSignal.direction = (int)thisItemOld.Direction;
                                singleSignal.tdbtraveller.ReverseDirection();                           // reverse //
                            }
                        }

                        SignalItem thisItemNew = TrItems[newSignal.SignalHeads[0].TDBIndex] as SignalItem;
                        if (newSignal.direction != thisItemNew.Direction)
                        {
                            newSignal.direction = (int)thisItemNew.Direction;
                            newSignal.tdbtraveller.ReverseDirection();                           // reverse //
                        }

                        //
                        // set correct trRefIndex for this signal, and set cross-reference for all backfacing trRef items
                        //

                        for (int i = 0; i < TrackNodes[newSignal.trackNode].TrVectorNode.NoItemRefs; i++)
                        {
                            int TDBRef = TrackNodes[newSignal.trackNode].TrVectorNode.TrItemRefs[i];
                            if (TrItems[TDBRef] != null)
                            {
                                if (TrItems[TDBRef].ItemType == TrItem.trItemType.trSIGNAL)
                                {
                                    foreach (SignalHead thisHead in newSignal.SignalHeads)
                                    {
                                        if (TDBRef == thisHead.TDBIndex)
                                        {
                                            SignalItem sigItem = (SignalItem)TrItems[TDBRef];
                                            sigItem.SigObj = newSignal.thisRef;
                                            newSignal.trRefIndex = i;

                                            // remove this key from the original signal //

                                            singleSignal.WorldObject.HeadReference.Remove((uint)TDBRef);
                                        }
                                    }
                                }
                            }
                        }

                        //
                        // reset cross-references for original signal (it may have been set for a backfacing head)
                        //

                        for (int i = 0; i < TrackNodes[newSignal.trackNode].TrVectorNode.NoItemRefs; i++)
                        {
                            int TDBRef = TrackNodes[newSignal.trackNode].TrVectorNode.TrItemRefs[i];
                            if (TrItems[TDBRef] != null)
                            {
                                if (TrItems[TDBRef].ItemType == TrItem.trItemType.trSIGNAL)
                                {
                                    foreach (SignalHead thisHead in singleSignal.SignalHeads)
                                    {
                                        if (TDBRef == thisHead.TDBIndex)
                                        {
                                            SignalItem sigItem = (SignalItem)TrItems[TDBRef];
                                            sigItem.SigObj = singleSignal.thisRef;
                                            singleSignal.trRefIndex = i;

                                            // remove this key from the new signal //

                                            newSignal.WorldObject.HeadReference.Remove((uint)TDBRef);
                                        }
                                    }
                                }
                            }
                        }

                        //
                        // add new signal to signal list
                        //

                        newindex++;
                        newSignals.Add(newSignal);

                        //
                        // revert existing signal to NULL if no heads remain
                        //

                        if (singleSignal.SignalHeads.Count <= 0)
                        {
                            signalObjects[isignal] = null;
                        }
                    }
                }
            }

            //
            // add all new signals to the signalObject array
            // length of array was set to all possible signals, so there will be space to spare
            //

            newindex = foundSignals;
            foreach (SignalObject newSignal in newSignals)
            {
                signalObjects[newindex] = newSignal;
                newindex++;
            }

            foundSignals = newindex;
        }

        //================================================================================================//
        //
        //  ScanSection : This method checks a section in the TDB for signals or speedposts
        //

        private void ScanSection(TrItem[] TrItems, TrackNode[] trackNodes, int index,
                               TrackSectionsFile tsectiondat, TrackDatabaseFile tdbfile, Dictionary<int, int> platformList)
        {
            int lastSignal = -1;                // Index to last signal found in path -1 if none

            if (trackNodes[index].TrEndNode)
                return;

            //  Is it a vector node then it may contain objects.
            if (trackNodes[index].TrVectorNode != null && trackNodes[index].TrVectorNode.NoItemRefs > 0)
            {
                // Any obects ?
                for (int i = 0; i < trackNodes[index].TrVectorNode.NoItemRefs; i++)
                {
                    if (TrItems[trackNodes[index].TrVectorNode.TrItemRefs[i]] != null)
                    {
                        int TDBRef = trackNodes[index].TrVectorNode.TrItemRefs[i];

                        // Track Item is signal
                        if (TrItems[TDBRef].ItemType == TrItem.trItemType.trSIGNAL)
                        {
                            SignalItem sigItem = (SignalItem)TrItems[TDBRef];
                            sigItem.SigObj = foundSignals;

                            bool validSignal = true;
                            lastSignal = AddSignal(index, i, sigItem, TDBRef, tsectiondat, tdbfile, ref validSignal);

                            if (validSignal)
                            {
                                sigItem.SigObj = lastSignal;
                            }
                            else
                            {
                                sigItem.SigObj = -1;
                            }
                        }

        // Track Item is speedpost - check if really limit
                        else if (TrItems[TDBRef].ItemType == TrItem.trItemType.trSPEEDPOST)
                        {
                            SpeedPostItem speedItem = (SpeedPostItem)TrItems[TDBRef];
                            if (speedItem.IsLimit)
                            {
                                speedItem.SigObj = foundSignals;

                                lastSignal = AddSpeed(index, i, speedItem, TDBRef, tsectiondat, tdbfile);
                                speedItem.SigObj = lastSignal;

                            }
                        }
                        else if (TrItems[TDBRef].ItemType == TrItem.trItemType.trPLATFORM)
                        {
                            if (platformList.ContainsKey(TDBRef))
                            {
                                Trace.TraceInformation("Double reference to platform ID {0} in nodes {1} and {2}\n", TDBRef, platformList[TDBRef], index);
                            }
                            else
                            {
                                platformList.Add(TDBRef, index);
                            }
                        }
                        else if (TrItems[TDBRef].ItemType == TrItem.trItemType.trSIDING)
                        {
                            if (platformList.ContainsKey(TDBRef))
                            {
                                Trace.TraceInformation("Double reference to siding ID {0} in nodes {1} and {2}\n", TDBRef, platformList[TDBRef], index);
                            }
                            else
                            {
                                platformList.Add(TDBRef, index);
                            }
                        }
                    }
                }
            }
        }   //ScanSection 

        //================================================================================================//
        //
        // Merge Heads
        //

        public void MergeHeads()
        {
            //            foreach (SignalWorldObject thisWorldObject in SignalWorldList)
            //            {
            for (int iWorldIndex = 0; iWorldIndex < SignalWorldList.Count; iWorldIndex++)
            {
                SignalWorldObject thisWorldObject = SignalWorldList[iWorldIndex];
                SignalObject MainSignal = null;

                if (thisWorldObject.HeadReference.Count > 1)
                {

                    foreach (KeyValuePair<uint, uint> thisReference in thisWorldObject.HeadReference)
                    {
                        if (SignalHeadList.ContainsKey(thisReference.Key))
                        {
                            if (MainSignal == null)
                            {
                                MainSignal = SignalHeadList[thisReference.Key];
                            }
                            else
                            {
                                SignalObject AddSignal = SignalHeadList[thisReference.Key];
                                if (MainSignal.trackNode != AddSignal.trackNode)
                                {
                                    Trace.TraceWarning("Signal head {0}  in different track node than signal head {1} of same signal", MainSignal.trItem, thisReference.Key);
                                    MainSignal = null;
                                    break;
                                }
                                foreach (SignalHead thisHead in AddSignal.SignalHeads)
                                {
                                    MainSignal.SignalHeads.Add(thisHead);
                                    SignalObjects[AddSignal.thisRef] = null;
                                }
                            }
                        }
                        else
                        {
                            Trace.TraceInformation("Signal found in Worldfile but not in TDB - TDB Index : {0}", thisReference.Key);
                            MainSignal = null;
                        }
                    }
                }
            }
        }

        //================================================================================================//
        ///
        /// This method adds a new Signal to the list
        ///

        private int AddSignal(int trackNode, int nodeIndx, SignalItem sigItem, int TDBRef, TrackSectionsFile tsectiondat, TrackDatabaseFile tdbfile, ref bool validSignal)
        {
            validSignal = true;

            signalObjects[foundSignals] = new SignalObject();
            signalObjects[foundSignals].isSignal = true;
            signalObjects[foundSignals].direction = (int)sigItem.Direction;
            signalObjects[foundSignals].trackNode = trackNode;
            signalObjects[foundSignals].trRefIndex = nodeIndx;
            signalObjects[foundSignals].AddHead(nodeIndx, TDBRef, sigItem);
            signalObjects[foundSignals].thisRef = foundSignals;
            signalObjects[foundSignals].signalRef = this;

            if (tdbfile.TrackDB.TrackNodes[trackNode] == null || tdbfile.TrackDB.TrackNodes[trackNode].TrVectorNode == null)
            {
                validSignal = false;
                Trace.TraceInformation("Reference to invalid track node {0} for Signal {1}\n", trackNode, TDBRef);
            }
            else
            {
                signalObjects[foundSignals].tdbtraveller =
                new Traveller(tsectiondat, tdbfile.TrackDB.TrackNodes, tdbfile.TrackDB.TrackNodes[trackNode],
                        sigItem.TileX, sigItem.TileZ, sigItem.X, sigItem.Z,
                (Traveller.TravellerDirection)(1 - sigItem.Direction));
            }

            signalObjects[foundSignals].WorldObject = null;

            if (SignalHeadList.ContainsKey((uint)TDBRef))
            {
                validSignal = false;
                Trace.TraceInformation("Invalid double TDBRef {0} in node {1}\n", TDBRef, trackNode);
            }

            if (!validSignal)
            {
                signalObjects[foundSignals] = null;  // reset signal, do not increase signal count
            }
            else
            {
                SignalHeadList.Add((uint)TDBRef, signalObjects[foundSignals]);
                foundSignals++;
            }

            return foundSignals - 1;
        } // AddSignal


        //================================================================================================//
        ///
        /// This method adds a new Speedpost to the list
        ///

        private int AddSpeed(int trackNode, int nodeIndx, SpeedPostItem speedItem, int TDBRef, TrackSectionsFile tsectiondat, TrackDatabaseFile tdbfile)
        {
            signalObjects[foundSignals] = new SignalObject();
            signalObjects[foundSignals].isSignal = false;
            signalObjects[foundSignals].direction = 0;                  // preset - direction not yet known //
            signalObjects[foundSignals].trackNode = trackNode;
            signalObjects[foundSignals].trRefIndex = nodeIndx;
            signalObjects[foundSignals].AddHead(nodeIndx, TDBRef, speedItem);
            signalObjects[foundSignals].thisRef = foundSignals;
            signalObjects[foundSignals].signalRef = this;

            signalObjects[foundSignals].tdbtraveller =
            new Traveller(tsectiondat, tdbfile.TrackDB.TrackNodes, tdbfile.TrackDB.TrackNodes[trackNode],
                    speedItem.TileX, speedItem.TileZ, speedItem.X, speedItem.Z,
                    (Traveller.TravellerDirection)signalObjects[foundSignals].direction);

            double delta_angle = signalObjects[foundSignals].tdbtraveller.RotY - ((Math.PI / 2) - speedItem.Angle);
            float delta_float = MathHelper.WrapAngle((float)delta_angle);
            if (Math.Abs(delta_float) < (Math.PI / 2))
            {
                signalObjects[foundSignals].direction = signalObjects[foundSignals].tdbtraveller.Direction == 0 ? 1 : 0;
            }
            else
            {
                signalObjects[foundSignals].direction = (int)signalObjects[foundSignals].tdbtraveller.Direction;
                signalObjects[foundSignals].tdbtraveller.ReverseDirection();
            }

#if DEBUG_PRINT
            File.AppendAllText(@"C:\temp\speedpost.txt",
				String.Format("\nPlaced : at : {0} {1}:{2} {3}; angle - track : {4}:{5}; delta : {6}; dir : {7}\n",
				speedItem.TileX, speedItem.TileZ, speedItem.X, speedItem.Z,
				speedItem.Angle, signalObjects[foundSignals].tdbtraveller.RotY,
				delta_angle,
				signalObjects[foundSignals].direction));
#endif

            signalObjects[foundSignals].WorldObject = null;
            foundSignals++;
            return foundSignals - 1;
        } // AddSpeed

        //================================================================================================//
        //
        //      AddCFG : This method adds the sigcfg reference to each signal object.
        //

        private void AddCFG(SignalConfigurationFile sigCFG)
        {
            foreach (SignalObject signal in signalObjects)
            {
                if (signal != null)
                {
                    if (signal.isSignal)
                    {
                        signal.SetSignalType(sigCFG);
                    }
                }
            }
        }//AddCFG

        //================================================================================================//
        //
        //     AddWorldInfo : add info from signal world objects to signal
        //

        private void AddWorldInfo()
        {

            // loop through all signal and all heads

            foreach (SignalObject signal in signalObjects)
            {
                if (signal != null)
                {
                    foreach (SignalHead head in signal.SignalHeads)
                    {

                        // get reference using TDB index from head

                        uint TDBRef = Convert.ToUInt32(head.TDBIndex);
                        SignalRefObject thisRef;

                        if (SignalRefList.TryGetValue(TDBRef, out thisRef))
                        {
                            uint signalIndex = thisRef.SignalWorldIndex;
                            if (signal.WorldObject == null)
                            {
                                signal.WorldObject = SignalWorldList[(int)signalIndex];
                            }
                            SignalRefList.Remove(TDBRef);
                        }
                    }
                }
            }

        }//AddWorldInfo

        //================================================================================================//
        ///
        //  FindByTrItem : find required signalObj + signalHead
        ///

        public KeyValuePair<SignalObject, SignalHead>? FindByTrItem(uint trItem)
        {
            foreach (var signal in signalObjects)
                if (signal != null)
                    foreach (var head in signal.SignalHeads)
                        if (SignalObject.trackNodes[signal.trackNode].TrVectorNode.TrItemRefs[head.trItemIndex] == (int)trItem)
                            return new KeyValuePair<SignalObject, SignalHead>(signal, head);
            return null;
        }//FindByTrItem

        //================================================================================================//
        /// 
        /// Count number of normal signal heads
        ///

        public void SetNumSignalHeads()
        {
            foreach (SignalObject thisSignal in signalObjects)
            {
                if (thisSignal != null)
                {
                    foreach (SignalHead thisHead in thisSignal.SignalHeads)
                    {
                        if (thisHead.sigFunction == MstsSignalFunction.NORMAL)
                        {
                            thisSignal.SignalNumNormalHeads++;
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Find_Next_Object_InRoute : find next item along path of train - using Route List (only forward)
        // Objects to search for : SpeedPost, Normal Signal
        //
        // Usage :
        //   always set : RouteList, RouteNodeIndex, distance along RouteNode, fnType
        //
        //   from train :
        //     optional : maxdistance
        //
        // returned :
        //   >= 0 : signal object reference
        //   -1  : end of track 
        //   -3  : no item within required distance
        //   -5  : end of authority
        //   -6  : end of (sub)route
        //

        public TrackCircuitSignalItem Find_Next_Object_InRoute(Train.TCSubpathRoute routePath,
                int routeIndex, float routePosition, float maxDistance, MstsSignalFunction fn_type, Train.TrainRouted thisTrain)
        {

            ObjectItemInfo.ObjectItemFindState locstate = ObjectItemInfo.ObjectItemFindState.None;
            // local processing state     //

            int actRouteIndex = routeIndex;      // present node               //
            Train.TCRouteElement thisElement = routePath[actRouteIndex];
            int actSection = thisElement.TCSectionIndex;
            int actDirection = thisElement.Direction;
            TrackCircuitSection thisSection = TrackCircuitList[actSection];
            float totalLength = 0;
            float lengthOffset = routePosition;

            SignalObject foundObject = null;
            TrackCircuitSignalItem thisItem = null;

            //
            // loop through trackcircuits until :
            //  - end of track or route is found
            //  - end of authorization is found
            //  - required item is found
            //  - max distance is covered
            //

            while (locstate == ObjectItemInfo.ObjectItemFindState.None)
            {

                // normal signal
                if (fn_type == MstsSignalFunction.NORMAL)
                {
                    if (thisSection.EndSignals[actDirection] != null)
                    {
                        foundObject = thisSection.EndSignals[actDirection];
                        totalLength += (thisSection.Length - lengthOffset);
                        locstate = ObjectItemInfo.ObjectItemFindState.Object;
                    }
                }

        // speedpost
                else if (fn_type == MstsSignalFunction.SPEED)
                {
                    TrackCircuitSignalList thisSpeedpostList =
                               thisSection.CircuitItems.TrackCircuitSpeedPosts[actDirection];
                    locstate = ObjectItemInfo.ObjectItemFindState.None;

                    for (int iPost = 0;
                             iPost < thisSpeedpostList.TrackCircuitItem.Count &&
                                     locstate == ObjectItemInfo.ObjectItemFindState.None;
                             iPost++)
                    {
                        TrackCircuitSignalItem thisSpeedpost = thisSpeedpostList.TrackCircuitItem[iPost];
                        if (thisSpeedpost.SignalLocation > lengthOffset)
                        {
                            ObjectSpeedInfo thisSpeed = thisSpeedpost.SignalRef.this_sig_speed(MstsSignalFunction.SPEED);
                            if (thisTrain == null ||
                                (thisSpeed != null &&
                                (thisSpeed.speed_flag == 1 || thisSpeed.speed_reset == 1 ||
                                (thisTrain.Train.IsFreight && thisSpeed.speed_freight != -1) || (!thisTrain.Train.IsFreight && thisSpeed.speed_pass != -1))))
                            {
                                locstate = ObjectItemInfo.ObjectItemFindState.Object;
                                foundObject = thisSpeedpost.SignalRef;
                                totalLength += (thisSpeedpost.SignalLocation - lengthOffset);
                            }
                        }
                    }
                }

                // next section accessed via next route element

                if (locstate == ObjectItemInfo.ObjectItemFindState.None)
                {
                    totalLength += (thisSection.Length - lengthOffset);
                    lengthOffset = 0;

                    int setSection = thisSection.ActivePins[thisElement.OutPin[0], thisElement.OutPin[1]].Link;
                    actRouteIndex++;

                    if (setSection < 0)
                    {
                        locstate = ObjectItemInfo.ObjectItemFindState.EndOfAuthority;
                    }
                    else if (actRouteIndex >= routePath.Count)
                    {
                        locstate = ObjectItemInfo.ObjectItemFindState.EndOfPath;
                    }
                    else if (maxDistance > 0 && totalLength > maxDistance)
                    {
                        locstate = ObjectItemInfo.ObjectItemFindState.PassedMaximumDistance;
                    }
                    else
                    {
                        thisElement = routePath[actRouteIndex];
                        actSection = thisElement.TCSectionIndex;
                        actDirection = thisElement.Direction;
                        thisSection = TrackCircuitList[actSection];
                    }
                }
            }

            if (foundObject != null)
            {
                thisItem = new TrackCircuitSignalItem(foundObject, totalLength);
            }
            else
            {
                thisItem = new TrackCircuitSignalItem(locstate);
            }

            return (thisItem);
        }

        //================================================================================================//
        //
        // GetNextObject_InRoute : find next item along path of train - using Route List (only forward)
        //
        // Usage :
        //   always set : Train (may be null), RouteList, RouteNodeIndex, distance along RouteNode, fn_type
        //
        //   from train :
        //     optional : maxdistance
        //
        // returned :
        //   >= 0 : signal object reference
        //   -1  : end of track 
        //   -2  : passed signal at danger
        //   -3  : no item within required distance
        //   -5  : end of authority
        //   -6  : end of (sub)route
        //


        // call without position
        public ObjectItemInfo GetNextObject_InRoute(Train.TrainRouted thisTrain, Train.TCSubpathRoute routePath,
                    int routeIndex, float routePosition, float maxDistance, ObjectItemInfo.ObjectItemType req_type)
        {

            Train.TCPosition thisPosition = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex];

            return (GetNextObject_InRoute(thisTrain, routePath, routeIndex, routePosition, maxDistance, req_type, thisPosition));
        }

        // call with position
        public ObjectItemInfo GetNextObject_InRoute(Train.TrainRouted thisTrain, Train.TCSubpathRoute routePath,
                    int routeIndex, float routePosition, float maxDistance, ObjectItemInfo.ObjectItemType req_type,
                    Train.TCPosition thisPosition)
        {

            TrackCircuitSignalItem foundItem = null;

            bool findSignal = false;
            bool findSpeedpost = false;

            float signalDistance = -1f;
            float speedpostDistance = -1f;

            if (req_type == ObjectItemInfo.ObjectItemType.Any ||
                req_type == ObjectItemInfo.ObjectItemType.Signal)
            {
                findSignal = true;
            }

            if (req_type == ObjectItemInfo.ObjectItemType.Any ||
                req_type == ObjectItemInfo.ObjectItemType.Speedlimit)
            {
                findSpeedpost = true;
            }

            Train.TCSubpathRoute usedRoute = routePath;

            // if routeIndex is not valid, build temp route from present position to first node or signal

            if (routeIndex < 0)
            {
                bool thisIsFreight = thisTrain != null ? thisTrain.Train.IsFreight : false;

                List<int> tempSections = ScanRoute(thisTrain.Train, thisPosition.TCSectionIndex,
                    thisPosition.TCOffset, thisPosition.TCDirection,
                    true, 200f, false, true, true, false, true, false, false, true, false, thisIsFreight);


                Train.TCSubpathRoute tempRoute = new Train.TCSubpathRoute();
                int prevSection = -2;

                foreach (int sectionIndex in tempSections)
                {
                    Train.TCRouteElement thisElement =
                        new Train.TCRouteElement(TrackCircuitList[Math.Abs(sectionIndex)],
                            sectionIndex > 0 ? 0 : 1, this, prevSection);
                    tempRoute.Add(thisElement);
                    prevSection = Math.Abs(sectionIndex);
                }
                usedRoute = tempRoute;
                routeIndex = 0;
            }

            // always find signal to check for signal at danger

            ObjectItemInfo.ObjectItemFindState signalState = ObjectItemInfo.ObjectItemFindState.None;

            TrackCircuitSignalItem nextSignal =
                Find_Next_Object_InRoute(usedRoute, routeIndex, routePosition,
                        maxDistance, MstsSignalFunction.NORMAL, thisTrain);

            signalState = nextSignal.SignalState;
            if (nextSignal.SignalState == ObjectItemInfo.ObjectItemFindState.Object)
            {
                signalDistance = nextSignal.SignalLocation;
                SignalObject foundSignal = nextSignal.SignalRef;
                if (foundSignal.this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP)
                {
                    signalState = ObjectItemInfo.ObjectItemFindState.PassedDanger;
                }
                else if (thisTrain != null && foundSignal.enabledTrain != thisTrain)
                {
                    signalState = ObjectItemInfo.ObjectItemFindState.PassedDanger;
                    nextSignal.SignalState = signalState;  // do not return OBJECT_FOUND - signal is not valid
                }

            }

            // look for speedpost only if required

            if (findSpeedpost)
            {
                TrackCircuitSignalItem nextSpeedpost =
                    Find_Next_Object_InRoute(usedRoute, routeIndex, routePosition,
                        maxDistance, MstsSignalFunction.SPEED, thisTrain);

                if (nextSpeedpost.SignalState == ObjectItemInfo.ObjectItemFindState.Object)
                {
                    speedpostDistance = nextSpeedpost.SignalLocation;
                    SignalObject foundSignal = nextSpeedpost.SignalRef;
                }


                if (signalDistance > 0 && speedpostDistance > 0)
                {
                    if (signalDistance < speedpostDistance)
                    {
                        if (findSignal)
                        {
                            foundItem = nextSignal;
                        }
                        else
                        {
                            foundItem = nextSpeedpost;
                            if (signalState == ObjectItemInfo.ObjectItemFindState.PassedDanger)
                            {
                                foundItem.SignalState = signalState;
                            }
                        }
                    }
                    else
                    {
                        foundItem = nextSpeedpost;
                    }
                }
                else if (signalDistance > 0)
                {
                    foundItem = nextSignal;
                }
                else if (speedpostDistance > 0)
                {
                    foundItem = nextSpeedpost;
                }
            }
            else if (findSignal)
            {
                foundItem = nextSignal;
            }


            ObjectItemInfo returnItem = null;

            if (foundItem == null)
            {
                returnItem = new ObjectItemInfo(ObjectItemInfo.ObjectItemFindState.None);
            }
            else if (foundItem.SignalState != ObjectItemInfo.ObjectItemFindState.Object)
            {
                returnItem = new ObjectItemInfo(foundItem.SignalState);
            }
            else
            {
                returnItem = new ObjectItemInfo(foundItem.SignalRef, foundItem.SignalLocation);
            }

            return (returnItem);
        }

        //
        //================================================================================================//
        //
        // Create Track Circuits
        //
#if ACTIVITY_EDITOR
        private void CreateTrackCircuits(TrItem[] TrItems, TrackNode[] trackNodes, TrackSectionsFile tsectiondat, ORRouteConfig orRouteConfig)
#else
        private void CreateTrackCircuits(TrItem[] TrItems, TrackNode[] trackNodes, TSectionDatFile tsectiondat)
#endif
        {

            //
            // Create dummy element as first to keep indexes equal
            //

            TrackCircuitList = new List<TrackCircuitSection>();
            TrackCircuitList.Add(new TrackCircuitSection(0, this));

            //
            // Create new default elements from existing base
            //

            for (int iNode = 1; iNode < trackNodes.Length; iNode++)
            {
                TrackNode trackNode = trackNodes[iNode];
                TrackCircuitSection defaultSection =
                    new TrackCircuitSection(trackNode, iNode, tsectiondat, this);
                TrackCircuitList.Add(defaultSection);
            }

            //
            // loop through original default elements
            // collect track items
            //

            int originalNodes = TrackCircuitList.Count;
            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                ProcessNodes(iNode, TrItems, trackNodes, tsectiondat);
            }

#if ACTIVITY_EDITOR
            //
            //  Loop through original default elements to complete the track items with the OR ones
            //

            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                List<TrackCircuitElement> elements = orRouteConfig.GetORItemForNode(iNode, trackNodes, tsectiondat);
                TrackCircuitList[iNode].CircuitItems.TrackCircuitElements = elements;
            }
#endif
            //
            // loop through original default elements
            // split on crossover items
            //

            originalNodes = TrackCircuitList.Count;
            int nextNode = originalNodes;
            foreach (KeyValuePair<int, CrossOverItem> CrossOver in CrossoverList)
            {
                nextNode = SplitNodesCrossover(CrossOver.Value, tsectiondat, nextNode);
            }

            //
            // loop through original default elements
            // split on normal signals
            //

            originalNodes = TrackCircuitList.Count;
            nextNode = originalNodes;

            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                nextNode = SplitNodesSignals(iNode, nextNode);
            }

#if ACTIVITY_EDITOR
            //
            // loop through original default elements
            // split on OR Elements
            //

            originalNodes = TrackCircuitList.Count;
            nextNode = originalNodes;

            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                nextNode = SplitNodesElements(iNode, nextNode);
            }
#endif
            //
            // loop through all items
            // perform link test
            //

            originalNodes = TrackCircuitList.Count;
            nextNode = originalNodes;
            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                nextNode = performLinkTest(iNode, nextNode);
            }

            //
            // loop through all items
            // reset active links
            // set fixed active links for none-junction links
            // set trailing junction flags
            //

            originalNodes = TrackCircuitList.Count;
            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                setActivePins(iNode);
            }

            //
            // Set cross-reference
            //

            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                setCrossReference(iNode, trackNodes);
            }
            for (int iNode = 1; iNode < originalNodes; iNode++)
            {
                setCrossReferenceCrossOver(iNode, trackNodes);
            }

            //
            // Set cross-reference for signals
            //

            for (int iNode = 0; iNode < TrackCircuitList.Count; iNode++)
            {
                setSignalCrossReference(iNode);
            }

            //
            // Set default next signal and fixed route information
            //

            for (int iSignal = 0; signalObjects != null && iSignal < signalObjects.Length; iSignal++)
            {
                SignalObject thisSignal = signalObjects[iSignal];
                if (thisSignal != null)
                {
                    thisSignal.setSignalDefaultNextSignal();
                }
            }
        }

        //================================================================================================//
        //
        // Print TC Information
        //


        void PrintTCBase(TrackNode[] trackNodes)
        {

            //
            // Test : print TrackCircuitList
            //

#if DEBUG_PRINT
            if (File.Exists(@"C:\temp\TCBase.txt"))
            {
                File.Delete(@"C:\temp\TCBase.txt");
            }

			var tcbb = new StringBuilder();
            for (var iNode = 0; iNode < TrackCircuitList.Count; iNode++)
            {
				var thisSection = TrackCircuitList[iNode];
				tcbb.AppendFormat("\nIndex : {0}\n", iNode);
				tcbb.Append("{\n");
				tcbb.AppendFormat("     Section    : {0}\n", thisSection.Index);
				tcbb.AppendFormat("     OrgSection : {0}\n", thisSection.OriginalIndex);
				tcbb.AppendFormat("     Type       : {0}\n", thisSection.CircuitType);

				tcbb.AppendFormat("     Pins (0,0) : {0} {1}\n", thisSection.Pins[0, 0].Direction, thisSection.Pins[0, 0].Link);
				tcbb.AppendFormat("     Pins (0,1) : {0} {1}\n", thisSection.Pins[0, 1].Direction, thisSection.Pins[0, 1].Link);
				tcbb.AppendFormat("     Pins (1,0) : {0} {1}\n", thisSection.Pins[1, 0].Direction, thisSection.Pins[1, 0].Link);
				tcbb.AppendFormat("     Pins (1,1) : {0} {1}\n", thisSection.Pins[1, 1].Direction, thisSection.Pins[1, 1].Link);

				tcbb.AppendFormat("     Active Pins (0,0) : {0} {1}\n", thisSection.ActivePins[0, 0].Direction, thisSection.ActivePins[0, 0].Link);
				tcbb.AppendFormat("     Active Pins (0,1) : {0} {1}\n", thisSection.ActivePins[0, 1].Direction, thisSection.ActivePins[0, 1].Link);
				tcbb.AppendFormat("     Active Pins (1,0) : {0} {1}\n", thisSection.ActivePins[1, 0].Direction, thisSection.ActivePins[1, 0].Link);
				tcbb.AppendFormat("     Active Pins (1,1) : {0} {1}\n", thisSection.ActivePins[1, 1].Direction, thisSection.ActivePins[1, 1].Link);

                if (thisSection.EndIsTrailingJunction[0])
                {
					tcbb.Append("     Trailing Junction : direction 0\n");
                }

                if (thisSection.EndIsTrailingJunction[1])
                {
					tcbb.Append("     Trailing Junction : direction 1\n");
                }

				tcbb.AppendFormat("     Length         : {0}\n", thisSection.Length);
				tcbb.AppendFormat("     OffsetLength 0 : {0}\n", thisSection.OffsetLength[0]);
				tcbb.AppendFormat("     OffsetLength 1 : {0}\n", thisSection.OffsetLength[1]);

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal && thisSection.CircuitItems != null)
                {
					tcbb.Append("\nSignals : \n");
					for (var iDirection = 0; iDirection <= 1; iDirection++)
                    {
                        if (thisSection.EndSignals[iDirection] != null)
                        {
							tcbb.AppendFormat("    End Signal {0} : {1}\n", iDirection, thisSection.EndSignals[iDirection].thisRef);
                        }

						for (var fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                        {
							var thisFN = (MstsSignalFunction)fntype;
							tcbb.AppendFormat("    Direction {0} - Function : {1} : \n", iDirection, thisFN);
							var thisSignalList = thisSection.CircuitItems.TrackCircuitSignals[iDirection, fntype];
							foreach (var thisItem in thisSignalList.TrackCircuitItem)
                            {
								var thisSignal = thisItem.SignalRef;
								var signalDistance = thisItem.SignalLocation;

                                if (thisSignal.WorldObject == null)
                                {
									tcbb.AppendFormat("         {0} = **UNKNOWN** at {1}\n", thisSignal.thisRef, signalDistance);
                                }
                                else
                                {
									tcbb.AppendFormat("         {0} = {1} at {2}\n", thisSignal.thisRef, thisSignal.WorldObject.SFileName, signalDistance);
                                }
                            }
							tcbb.Append("\n");
                        }
                    }

					tcbb.Append("\nSpeedposts : \n");
					for (var iDirection = 0; iDirection <= 1; iDirection++)
                    {
						tcbb.AppendFormat("    Direction {0}\n", iDirection);

						var thisSpeedpostList = thisSection.CircuitItems.TrackCircuitSpeedPosts[iDirection];
						foreach (var thisItem in thisSpeedpostList.TrackCircuitItem)
                        {
							var thisSpeedpost = thisItem.SignalRef;
							var speedpostDistance = thisItem.SignalLocation;

							var speedInfo = new ObjectItemInfo(thisSpeedpost, speedpostDistance);
							tcbb.AppendFormat("{0} = pass : {1} ; freight : {2} - at distance {3}\n", thisSpeedpost.thisRef, speedInfo.speed_passenger, speedInfo.speed_freight, speedpostDistance);
                        }

						tcbb.Append("\n");
                    }
                }
                else if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
					tcbb.AppendFormat("    Overlap : {0}\n", thisSection.Overlap);
                }

                if (thisSection.TunnelInfo != null && thisSection.TunnelInfo.Count > 0)
                {
                    tcbb.Append("\nTunnel Info : \n");
                    foreach (TrackCircuitSection.tunnelInfoData[] thisTunnelInfo in thisSection.TunnelInfo)
                    {
                        tcbb.AppendFormat("\nDirection 0 : Start : {0} ; End : {1} ; Length in TCS : {2} ; Overall length : {3} ; Tunnel offset : {4} \n",
                            thisTunnelInfo[0].TunnelStart, thisTunnelInfo[0].TunnelEnd, thisTunnelInfo[0].LengthInTCS, thisTunnelInfo[0].TotalLength, thisTunnelInfo[0].TCSStartOffset);
                        tcbb.AppendFormat("\nDirection 1 : Start : {0} ; End : {1} ; Length in TCS : {2} ; Overall length : {3} ; Tunnel offset : {4} \n",
                            thisTunnelInfo[1].TunnelStart, thisTunnelInfo[1].TunnelEnd, thisTunnelInfo[1].LengthInTCS, thisTunnelInfo[1].TotalLength, thisTunnelInfo[1].TCSStartOffset);
                    }
                }

				tcbb.Append("}\n");
            }

			tcbb.Append("\n\nCROSSOVERS\n\n");
			foreach (var CrossItem in CrossoverList)
            {
				var thisCross = CrossItem.Value;
				tcbb.AppendFormat("   Indices : {0} - {1}\n", thisCross.ItemIndex[0], thisCross.ItemIndex[1]);
				tcbb.AppendFormat("   Sections: {0} - {1}\n", thisCross.SectionIndex[0], thisCross.SectionIndex[1]);
				tcbb.Append("\n");
            }

			tcbb.Append("\n\nTRACK SECTIONS\n\n");
			foreach (var thisTrack in trackNodes)
            {
                if (thisTrack == null)
                {
                }
                else if (thisTrack.TCCrossReference == null)
                {
					tcbb.Append("   ERROR : no track circuit cross-reference \n");
                    Trace.TraceWarning("ERROR : Track Node without Track Circuit cross-reference");
                }
                else
                {
					var thisXRef = thisTrack.TCCrossReference;
					var thisSection = TrackCircuitList[thisXRef[0].Index];
					tcbb.AppendFormat("     Original node : {0}\n", thisSection.OriginalIndex);

					foreach (var thisReference in thisXRef)
                    {
						tcbb.AppendFormat("        Ref Index : {0} : " + "Length : {1} at : {2} - {3}\n", thisReference.Index, thisReference.Length, thisReference.OffsetLength[0], thisReference.OffsetLength[1]);
                    }
					tcbb.Append("\n");

                    if (thisXRef[thisXRef.Count - 1].OffsetLength[1] != 0)
                    {
						tcbb.Append(" >>> INVALID XREF\n");
                    }
                }
            }

			tcbb.Append("\n\n PLATFORMS \n --------- \n\n");

			foreach (var platformXRef in PlatformXRefList)
            {
				var thisPlatform = PlatformDetailsList[platformXRef.Value];

				tcbb.AppendFormat("Index {0} : Platform {1} [{2} ,{3}]\n", platformXRef.Key, platformXRef.Value, thisPlatform.PlatformReference[0], thisPlatform.PlatformReference[1]);
            }

			tcbb.Append("\n\n");

			for (var iPlatform = 0; iPlatform < PlatformDetailsList.Count; iPlatform++)
            {
				var thisPlatform = PlatformDetailsList[iPlatform];

				tcbb.AppendFormat("Platform : {0}\n", iPlatform);

				tcbb.AppendFormat("Name     : {0}\n", thisPlatform.Name);
				tcbb.AppendFormat("Time     : {0}\n", thisPlatform.MinWaitingTime);

				tcbb.AppendFormat("Sections : ");
				for (var iSection = 0; iSection < thisPlatform.TCSectionIndex.Count; iSection++)
                {
					tcbb.AppendFormat(" " + thisPlatform.TCSectionIndex[iSection]);
                }
				tcbb.AppendFormat("\n");

				tcbb.AppendFormat("Platform References    : {0} + {1}\n", thisPlatform.PlatformReference[0], thisPlatform.PlatformReference[1]);

				tcbb.AppendFormat("Section Offset : [0,0] : {0}\n", thisPlatform.TCOffset[0, 0]);
				tcbb.AppendFormat("                 [0,1] : {0}\n", thisPlatform.TCOffset[0, 1]);
				tcbb.AppendFormat("                 [1,0] : {0}\n", thisPlatform.TCOffset[1, 0]);
				tcbb.AppendFormat("                 [1,1] : {0}\n", thisPlatform.TCOffset[1, 1]);

				tcbb.AppendFormat("Length                 : {0}\n", thisPlatform.Length);

				tcbb.AppendFormat("Node Offset    : [0]   : {0}\n", thisPlatform.nodeOffset[0]);
				tcbb.AppendFormat("Node Offset    : [1]   : {0}\n", thisPlatform.nodeOffset[1]);

                if (thisPlatform.EndSignals[0] == -1)
                {
					tcbb.AppendFormat("End Signal     : [0]   : -None-\n");
                }
                else
                {
					tcbb.AppendFormat("End Signal     : [0]   : {0}\n", thisPlatform.EndSignals[0]);
					tcbb.AppendFormat("Distance               : {0}\n", thisPlatform.DistanceToSignals[0]);
                }
                if (thisPlatform.EndSignals[1] == -1)
                {
					tcbb.AppendFormat("End Signal     : [1]   : -None-\n");
                }
                else
                {
					tcbb.AppendFormat("End Signal     : [1]   : {0}\n", thisPlatform.EndSignals[1]);
					tcbb.AppendFormat("Distance               : {0}\n", thisPlatform.DistanceToSignals[1]);
                }

				tcbb.Append("\n");
            }
			File.AppendAllText(@"C:\temp\TCBase.txt", tcbb.ToString());
#endif
        }

        //================================================================================================//
        //
        // ProcessNodes
        //

        public void ProcessNodes(int iNode, TrItem[] TrItems, TrackNode[] trackNodes, TrackSectionsFile tsectiondat)
        {

            //
            // Check if original tracknode had trackitems
            //

            TrackCircuitSection thisCircuit = TrackCircuitList[iNode];
            TrackNode thisNode = trackNodes[thisCircuit.OriginalIndex];

            if (thisNode.TrVectorNode != null && thisNode.TrVectorNode.NoItemRefs > 0)
            {
                //
                // Create TDBtraveller at start of section to calculate distances
                //

                TrVectorSection firstSection = thisNode.TrVectorNode.TrVectorSections[0];
                Traveller TDBTrav = new Traveller(tsectiondat, trackNodes, thisNode,
                                firstSection.TileX, firstSection.TileZ,
                                firstSection.X, firstSection.Z, (Traveller.TravellerDirection)1);

                //
                // Process all items (do not split yet)
                //

                float[] lastDistance = new float[2] { -1.0f, -1.0f };
                for (int iRef = 0; iRef < thisNode.TrVectorNode.NoItemRefs; iRef++)
                {
                    int TDBRef = thisNode.TrVectorNode.TrItemRefs[iRef];
                    if (TrItems[TDBRef] != null)
                    {
                        lastDistance = InsertNode(thisCircuit, TrItems[TDBRef], TDBTrav, trackNodes, lastDistance);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // InsertNode
        //

        public float[] InsertNode(TrackCircuitSection thisCircuit, TrItem thisItem,
                        Traveller TDBTrav, TrackNode[] trackNodes, float[] lastDistance)
        {

            float[] newLastDistance = new float[2];
            lastDistance.CopyTo(newLastDistance, 0);

            //
            // Insert signal
            //

            if (thisItem.ItemType == TrItem.trItemType.trSIGNAL)
            {
                if (!Simulator.TimetableMode)
                {
                    try
                    {
                        SignalItem tryItem = (SignalItem)thisItem;
                    }
                    catch (Exception error)
                    {
                        Trace.TraceWarning(error.Message);
                        Trace.TraceWarning("Signal item not consistent with signal database");
                        return newLastDistance;
                    }
                }
                SignalItem sigItem = (SignalItem)thisItem;
                if (sigItem.SigObj >= 0)
                {
                    SignalObject thisSignal = SignalObjects[sigItem.SigObj];
                    if (!Simulator.TimetableMode && thisSignal == null)
                    {
                        Trace.TraceWarning("Signal item with TrItemID = {0} not consistent with signal database", sigItem.TrItemId);
                        return newLastDistance;
                    }
                    float signalDistance = thisSignal.DistanceTo(TDBTrav);
                    if (thisSignal.direction == 1)
                    {
                        signalDistance = thisCircuit.Length - signalDistance;
                    }

                    for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                    {
                        MstsSignalFunction[] reqfntype = new MstsSignalFunction[1];
                        reqfntype[0] = (MstsSignalFunction)fntype;

                        if (thisSignal.isSignalType(reqfntype))
                        {
                            TrackCircuitSignalItem thisTCItem =
                                    new TrackCircuitSignalItem(thisSignal, signalDistance);

                            int directionList = thisSignal.direction == 0 ? 1 : 0;
                            TrackCircuitSignalList thisSignalList =
                                    thisCircuit.CircuitItems.TrackCircuitSignals[directionList, fntype];

                            // if signal is SPEED type, insert in speedpost list
                            if (reqfntype[0] == MstsSignalFunction.SPEED)
                            {
                                thisSignalList = thisCircuit.CircuitItems.TrackCircuitSpeedPosts[directionList];
                            }

                            bool signalset = false;
                            foreach (TrackCircuitSignalItem inItem in thisSignalList.TrackCircuitItem)
                            {
                                if (inItem.SignalRef == thisSignal)
                                {
                                    signalset = true;
                                }
                            }

                            if (!signalset)
                            {
                                if (directionList == 0)
                                {
                                    thisSignalList.TrackCircuitItem.Insert(0, thisTCItem);
                                }
                                else
                                {
                                    thisSignalList.TrackCircuitItem.Add(thisTCItem);
                                }
                            }
                        }
                    }
                    newLastDistance[thisSignal.direction] = signalDistance;
                }
            }

        //
            // Insert speedpost
            //

            else if (thisItem.ItemType == TrItem.trItemType.trSPEEDPOST)
            {
                SpeedPostItem speedItem = (SpeedPostItem)thisItem;
                if (speedItem.SigObj >= 0)
                {
                    SignalObject thisSpeedpost = SignalObjects[speedItem.SigObj];
                    float speedpostDistance = thisSpeedpost.DistanceTo(TDBTrav);
                    if (thisSpeedpost.direction == 1)
                    {
                        speedpostDistance = thisCircuit.Length - speedpostDistance;
                    }

                    if (speedpostDistance == lastDistance[thisSpeedpost.direction]) // if at same position as last item
                    {
                        speedpostDistance = speedpostDistance + 0.001f;  // shift 1 mm so it will be found
                    }

                    TrackCircuitSignalItem thisTCItem =
                            new TrackCircuitSignalItem(thisSpeedpost, speedpostDistance);

                    int directionList = thisSpeedpost.direction == 0 ? 1 : 0;
                    TrackCircuitSignalList thisSignalList =
                            thisCircuit.CircuitItems.TrackCircuitSpeedPosts[directionList];

                    if (directionList == 0)
                    {
                        thisSignalList.TrackCircuitItem.Insert(0, thisTCItem);
                    }
                    else
                    {
                        thisSignalList.TrackCircuitItem.Add(thisTCItem);
                    }

                    newLastDistance[thisSpeedpost.direction] = speedpostDistance;
                }
            }

        //
            // Insert crossover in special crossover list
            //

            else if (thisItem.ItemType == TrItem.trItemType.trCROSSOVER)
            {
                CrossoverItem crossItem = (CrossoverItem)thisItem;

                float cdist = TDBTrav.DistanceTo(trackNodes[thisCircuit.OriginalIndex],
                crossItem.TileX, crossItem.TileZ,
                                crossItem.X, crossItem.Y, crossItem.Z);

                int thisId = (int)crossItem.TrItemId;
                int crossId = (int)crossItem.TrackNode;
                CrossOverItem exItem = null;

                // search in Dictionary for combined item //

                if (CrossoverList.ContainsKey(crossId))
                {
                    exItem = CrossoverList[crossId];
                    exItem.Position[1] = cdist;
                    exItem.SectionIndex[1] = thisCircuit.Index;
                }
                else
                {
                    exItem = new CrossOverItem();
                    exItem.SectionIndex[0] = thisCircuit.Index;
                    exItem.SectionIndex[1] = -1;

                    exItem.Position[0] = cdist;
                    exItem.ItemIndex[0] = thisId;
                    exItem.ItemIndex[1] = crossId;

                    exItem.TrackShape = crossItem.ShapeId;

                    CrossoverList.Add(thisId, exItem);
                }
            }

            return (newLastDistance);
        }

        //================================================================================================//
        //
        // Split on Signals
        //

        private int SplitNodesSignals(int thisNode, int nextNode)
        {
            int thisIndex = thisNode;
            int newIndex = -1;
            List<int> addIndex = new List<int>();

            //
            // in direction 0, check original item only
            // keep list of added items
            //

            TrackCircuitSection thisSection = TrackCircuitList[thisIndex];

            newIndex = -1;
            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
            {
                addIndex.Add(thisNode);

                List<TrackCircuitSignalItem> sectionSignals =
                         thisSection.CircuitItems.TrackCircuitSignals[0, (int)MstsSignalFunction.NORMAL].TrackCircuitItem;

                while (sectionSignals.Count > 0)
                {
                    TrackCircuitSignalItem thisSignal = sectionSignals[0];
                    sectionSignals.RemoveAt(0);

                    newIndex = nextNode;
                    nextNode++;

                    splitSection(thisIndex, newIndex, thisSection.Length - thisSignal.SignalLocation);
                    TrackCircuitSection newSection = TrackCircuitList[newIndex];
                    newSection.EndSignals[0] = thisSignal.SignalRef;
                    thisSection = TrackCircuitList[thisIndex];
                    addIndex.Add(newIndex);

                    // restore list (link is lost as item is replaced)
                    sectionSignals = thisSection.CircuitItems.TrackCircuitSignals[0, (int)MstsSignalFunction.NORMAL].TrackCircuitItem;
                }
            }

            //
            // in direction 1, check original item and all added items
            //

            foreach (int actIndex in addIndex)
            {
                thisIndex = actIndex;

                while (thisIndex > 0)
                {
                    thisSection = TrackCircuitList[thisIndex];

                    newIndex = -1;
                    if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
                    {

                        List<TrackCircuitSignalItem> sectionSignals =
                           thisSection.CircuitItems.TrackCircuitSignals[1, (int)MstsSignalFunction.NORMAL].TrackCircuitItem;

                        if (sectionSignals.Count > 0)
                        {
                            TrackCircuitSignalItem thisSignal = sectionSignals[0];
                            sectionSignals.RemoveAt(0);

                            newIndex = nextNode;
                            nextNode++;

                            splitSection(thisIndex, newIndex, thisSignal.SignalLocation);
                            TrackCircuitSection newSection = TrackCircuitList[newIndex];
                            newSection.EndSignals[0] = null;
                            thisSection = TrackCircuitList[thisIndex];
                            thisSection.EndSignals[1] = thisSignal.SignalRef;

                            // restore list (link is lost as item is replaced)
                            sectionSignals = thisSection.CircuitItems.TrackCircuitSignals[1, (int)MstsSignalFunction.NORMAL].TrackCircuitItem;
                        }
                    }
                    thisIndex = thisSection.CircuitItems.TrackCircuitSignals[1, (int)MstsSignalFunction.NORMAL].TrackCircuitItem.Count > 0 ? thisIndex : newIndex;
                }
            }

            return (nextNode);
        }

        //================================================================================================//
        //
        // Split CrossOvers
        //

        private int SplitNodesCrossover(CrossOverItem CrossOver,
                TrackSectionsFile tsectiondat, int nextNode)
        {
            bool processCrossOver = true;
            int sectionIndex0 = 0;
            int sectionIndex1 = 0;

            if (CrossOver.SectionIndex[0] < 0 || CrossOver.SectionIndex[1] < 0)
            {
                Trace.TraceWarning("Incomplete crossover : indices {0} and {1}",
                            CrossOver.ItemIndex[0], CrossOver.ItemIndex[1]);
                processCrossOver = false;
            }
            if (CrossOver.SectionIndex[0] == CrossOver.SectionIndex[1])
            {
                Trace.TraceWarning("Invalid crossover : indices {0} and {1} : equal section : {2}",
                            CrossOver.ItemIndex[0], CrossOver.ItemIndex[1],
                CrossOver.SectionIndex[0]);
                processCrossOver = false;
            }

            if (processCrossOver)
            {
                sectionIndex0 = getCrossOverSectionIndex(CrossOver, 0);
                sectionIndex1 = getCrossOverSectionIndex(CrossOver, 1);

                if (sectionIndex0 < 0 || sectionIndex1 < 0)
                {
                    processCrossOver = false;
                }
            }

            if (processCrossOver)
            {
                int newSection0 = nextNode;
                nextNode++;
                int newSection1 = nextNode;
                nextNode++;
                int jnSection = nextNode;
                nextNode++;

                splitSection(sectionIndex0, newSection0, CrossOver.Position[0]);
                splitSection(sectionIndex1, newSection1, CrossOver.Position[1]);

                addCrossoverJunction(sectionIndex0, newSection0, sectionIndex1, newSection1,
                                jnSection, CrossOver, tsectiondat);
            }

            return (nextNode);
        }

#if ACTIVITY_EDITOR
        //================================================================================================//
        //
        // Split on OR Elements
        //

        private int SplitNodesElements(int thisNode, int nextNode)
        {
            int thisIndex = thisNode;
            int newIndex = -1;
            List<int> addIndex = new List<int>();

            //
            // in direction 0, check original item only
            // keep list of added items
            //

            TrackCircuitSection thisSection = TrackCircuitList[thisIndex];

            newIndex = -1;
            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
            {
                addIndex.Add(thisNode);

                List<TrackCircuitElement> elements =
                         thisSection.CircuitItems.TrackCircuitElements;

                for (int idx = 0; idx < elements.Count; idx++)
                {
                    newIndex = nextNode;
                    nextNode++;

                    splitSection(thisIndex, newIndex, thisSection.Length - elements[idx].ElementLocation);
                    TrackCircuitSection newSection = TrackCircuitList[newIndex];
                    newSection.EndSignals[0] = null;
                    thisSection = TrackCircuitList[thisIndex];
                    addIndex.Add(newIndex);
                }
            }

            return (nextNode);
        }
#endif
        //================================================================================================//
        //
        // Get cross-over section index
        //

        private int getCrossOverSectionIndex(CrossOverItem CrossOver, int Index)
        {
            int sectionIndex = CrossOver.SectionIndex[Index];
            float position = CrossOver.Position[Index];
            TrackCircuitSection section = TrackCircuitList[sectionIndex];

            while (position > 0 && position > section.Length)
            // while (position > 0 && position > section.Length && section.OriginalIndex == firstSectionOriginalIndex)
            {
                int prevSection = sectionIndex;
                position = position - section.Length;
                CrossOver.Position[Index] = position;
                sectionIndex = section.Pins[1, 0].Link;

                if (sectionIndex > 0)
                {
                    section = TrackCircuitList[sectionIndex];
                    if (section.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                    {
                        if (section.Pins[0, 0].Link == prevSection)
                        {
                            sectionIndex = section.Pins[1, 0].Link;
                        }
                        else
                        {
                            sectionIndex = section.Pins[1, 1].Link;
                        }
                        section = TrackCircuitList[sectionIndex];
                    }
                }
                else
                {
                    position = -1;  // no position found //
                }
            }

            if (position < 0)
            {
                Trace.TraceWarning("Cannot locate CrossOver {0} in Section {1}",
                                CrossOver.ItemIndex[0], CrossOver.SectionIndex[0]);
                sectionIndex = -1;
            }

            return (sectionIndex);
        }

        //================================================================================================//
        //
        // Split section
        //

        private void splitSection(int orgSectionIndex, int newSectionIndex, float position)
        {
            TrackCircuitSection orgSection = TrackCircuitList[orgSectionIndex];
            TrackCircuitSection newSection = orgSection.CopyBasic(newSectionIndex);
            TrackCircuitSection replSection = orgSection.CopyBasic(orgSectionIndex);

            replSection.OriginalIndex = newSection.OriginalIndex = orgSection.OriginalIndex;
            replSection.CircuitType = newSection.CircuitType = TrackCircuitSection.TrackCircuitType.Normal;

            replSection.Length = position;
            newSection.Length = orgSection.Length - position;

#if DEBUG_REPORTS
            // check for invalid lengths - report and correct

            if (newSection.Length < 0)
            {
                Trace.TraceWarning("Invalid Length for new section {0}: length {1}, split on {2}",
                        newSection.Index, orgSection.Length, position);
                newSection.Length = 0.1f;
                replSection.Length -= 0.01f;  // take length off other part
            }
            if (replSection.Length < 0)
            {
                Trace.TraceWarning("Invalid Length for replacement section {0}: length {1}, split on {2}",
                        newSection.Index, orgSection.Length, position);
                replSection.Length = 0.1f;
                newSection.Length -= 0.01f;  // take length off other part
            }
#endif

            // take care of rounding errors

            if (newSection.Length < 0 || Math.Abs(newSection.Length) < 0.01f)
            {
                newSection.Length = 0.01f;
                replSection.Length -= 0.01f;  // take length off other part
            }
            if (replSection.Length < 0 || Math.Abs(replSection.Length) < 0.01f)
            {
                replSection.Length = 0.01f;
                newSection.Length -= 0.01f;  // take length off other part
            }

            // set lengths and offset

            replSection.OffsetLength[0] = orgSection.OffsetLength[0] + newSection.Length;
            replSection.OffsetLength[1] = orgSection.OffsetLength[1];

            newSection.OffsetLength[0] = orgSection.OffsetLength[0];
            newSection.OffsetLength[1] = orgSection.OffsetLength[1] + replSection.Length;

            // set new pins

            replSection.Pins[0, 0].Direction = orgSection.Pins[0, 0].Direction;
            replSection.Pins[0, 0].Link = orgSection.Pins[0, 0].Link;
            replSection.Pins[1, 0].Direction = 1;
            replSection.Pins[1, 0].Link = newSectionIndex;

            newSection.Pins[0, 0].Direction = 0;
            newSection.Pins[0, 0].Link = orgSectionIndex;
            newSection.Pins[1, 0].Direction = orgSection.Pins[1, 0].Direction;
            newSection.Pins[1, 0].Link = orgSection.Pins[1, 0].Link;

            // update pins on adjacent sections

            int refLinkIndex = newSection.Pins[1, 0].Link;
            int refLinkDirIndex = newSection.Pins[1, 0].Direction == 0 ? 1 : 0;
            TrackCircuitSection refLink = TrackCircuitList[refLinkIndex];
            if (refLink.Pins[refLinkDirIndex, 0].Link == orgSectionIndex)
            {
                refLink.Pins[refLinkDirIndex, 0].Link = newSectionIndex;
            }
            else if (refLink.Pins[refLinkDirIndex, 1].Link == orgSectionIndex)
            {
                refLink.Pins[refLinkDirIndex, 1].Link = newSectionIndex;
            }

            // copy signal information

            for (int itype = 0; itype < orgSection.CircuitItems.TrackCircuitSignals.GetLength(1); itype++)
            {
                TrackCircuitSignalList orgSigList = orgSection.CircuitItems.TrackCircuitSignals[0, itype];
                TrackCircuitSignalList replSigList = replSection.CircuitItems.TrackCircuitSignals[0, itype];
                TrackCircuitSignalList newSigList = newSection.CircuitItems.TrackCircuitSignals[0, itype];

                foreach (TrackCircuitSignalItem thisSignal in orgSigList.TrackCircuitItem)
                {
                    float sigLocation = thisSignal.SignalLocation;
                    if (sigLocation <= newSection.Length)
                    {
                        newSigList.TrackCircuitItem.Add(thisSignal);
                    }
                    else
                    {
                        thisSignal.SignalLocation -= newSection.Length;
                        replSigList.TrackCircuitItem.Add(thisSignal);
                    }
                }
            }

            for (int itype = 0; itype < orgSection.CircuitItems.TrackCircuitSignals.GetLength(1); itype++)
            {
                TrackCircuitSignalList orgSigList = orgSection.CircuitItems.TrackCircuitSignals[1, itype];
                TrackCircuitSignalList replSigList = replSection.CircuitItems.TrackCircuitSignals[1, itype];
                TrackCircuitSignalList newSigList = newSection.CircuitItems.TrackCircuitSignals[1, itype];

                foreach (TrackCircuitSignalItem thisSignal in orgSigList.TrackCircuitItem)
                {
                    float sigLocation = thisSignal.SignalLocation;
                    if (sigLocation > replSection.Length)
                    {
                        thisSignal.SignalLocation -= replSection.Length;
                        newSigList.TrackCircuitItem.Add(thisSignal);
                    }
                    else
                    {
                        replSigList.TrackCircuitItem.Add(thisSignal);
                    }
                }
            }

            // copy speedpost information

            TrackCircuitSignalList orgSpeedList = orgSection.CircuitItems.TrackCircuitSpeedPosts[0];
            TrackCircuitSignalList replSpeedList = replSection.CircuitItems.TrackCircuitSpeedPosts[0];
            TrackCircuitSignalList newSpeedList = newSection.CircuitItems.TrackCircuitSpeedPosts[0];

            foreach (TrackCircuitSignalItem thisSpeedpost in orgSpeedList.TrackCircuitItem)
            {
                float sigLocation = thisSpeedpost.SignalLocation;
                if (sigLocation < newSection.Length)
                {
                    newSpeedList.TrackCircuitItem.Add(thisSpeedpost);
                }
                else
                {
                    thisSpeedpost.SignalLocation -= newSection.Length;
                    replSpeedList.TrackCircuitItem.Add(thisSpeedpost);
                }
            }

            orgSpeedList = orgSection.CircuitItems.TrackCircuitSpeedPosts[1];
            replSpeedList = replSection.CircuitItems.TrackCircuitSpeedPosts[1];
            newSpeedList = newSection.CircuitItems.TrackCircuitSpeedPosts[1];

            foreach (TrackCircuitSignalItem thisSpeedpost in orgSpeedList.TrackCircuitItem)
            {
                float sigLocation = thisSpeedpost.SignalLocation;
                if (sigLocation > replSection.Length)
                {
                    thisSpeedpost.SignalLocation -= replSection.Length;
                    newSpeedList.TrackCircuitItem.Add(thisSpeedpost);
                }
                else
                {
                    replSpeedList.TrackCircuitItem.Add(thisSpeedpost);
                }
            }

            // copy milepost information

            foreach (TrackCircuitMilepost thisMilePost in orgSection.CircuitItems.MilePosts)
            {
                if (thisMilePost.MilepostLocation[0] > replSection.Length)
                {
                    thisMilePost.MilepostLocation[0] -= replSection.Length;
                    newSection.CircuitItems.MilePosts.Add(thisMilePost);
                }
                else
                {
                    thisMilePost.MilepostLocation[1] -= newSection.Length;
                    replSection.CircuitItems.MilePosts.Add(thisMilePost);
                }
            }

#if ACTIVITY_EDITOR
            //  copy TrackCircuitElements 

            foreach (TrackCircuitElement element in orgSection.CircuitItems.TrackCircuitElements)
            {
                if (element.ElementLocation > replSection.Length)
                {
                    element.ElementLocation -= replSection.Length;
                    newSection.CircuitItems.TrackCircuitElements.Add(element);
                }
                else
                {
                    element.ElementLocation -= newSection.Length;
                    replSection.CircuitItems.TrackCircuitElements.Add(element);
                }
            }
#endif
            // update list

            TrackCircuitList.RemoveAt(orgSectionIndex);
            TrackCircuitList.Insert(orgSectionIndex, replSection);
            TrackCircuitList.Add(newSection);
        }


        //================================================================================================//
        //
        // Add junction sections for Crossover
        //

        private void addCrossoverJunction(int leadSectionIndex0, int trailSectionIndex0,
                        int leadSectionIndex1, int trailSectionIndex1, int JnIndex,
                        CrossOverItem CrossOver, TrackSectionsFile tsectiondat)
        {
            TrackCircuitSection leadSection0 = TrackCircuitList[leadSectionIndex0];
            TrackCircuitSection leadSection1 = TrackCircuitList[leadSectionIndex1];
            TrackCircuitSection trailSection0 = TrackCircuitList[trailSectionIndex0];
            TrackCircuitSection trailSection1 = TrackCircuitList[trailSectionIndex1];
            TrackCircuitSection JnSection = new TrackCircuitSection(JnIndex, this);

            JnSection.OriginalIndex = leadSection0.OriginalIndex;
            JnSection.CircuitType = TrackCircuitSection.TrackCircuitType.Crossover;
            JnSection.Length = 0;

            leadSection0.Pins[1, 0].Link = JnIndex;
            leadSection1.Pins[1, 0].Link = JnIndex;
            trailSection0.Pins[0, 0].Link = JnIndex;
            trailSection1.Pins[0, 0].Link = JnIndex;

            JnSection.Pins[0, 0].Direction = 0;
            JnSection.Pins[0, 0].Link = leadSectionIndex0;
            JnSection.Pins[0, 1].Direction = 0;
            JnSection.Pins[0, 1].Link = leadSectionIndex1;
            JnSection.Pins[1, 0].Direction = 1;
            JnSection.Pins[1, 0].Link = trailSectionIndex0;
            JnSection.Pins[1, 1].Direction = 1;
            JnSection.Pins[1, 1].Link = trailSectionIndex1;

            if (tsectiondat.TrackShapes.ContainsKey(CrossOver.TrackShape))
            {
                JnSection.Overlap = tsectiondat.TrackShapes[CrossOver.TrackShape].ClearanceDistance;
            }
            else
            {
                JnSection.Overlap = 0;
            }

            JnSection.SignalsPassingRoutes = new List<int>();

            TrackCircuitList.Add(JnSection);
        }

        //================================================================================================//
        //
        // Check pin links
        //

        private int performLinkTest(int thisNode, int nextNode)
        {

            TrackCircuitSection thisSection = TrackCircuitList[thisNode];

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                for (int iPin = 0; iPin <= 1; iPin++)
                {
                    int linkedNode = thisSection.Pins[iDirection, iPin].Link;
                    int linkedDirection = thisSection.Pins[iDirection, iPin].Direction == 0 ? 1 : 0;

                    if (linkedNode > 0)
                    {
                        TrackCircuitSection linkedSection = TrackCircuitList[linkedNode];

                        bool linkfound = false;
                        bool doublelink = false;
                        int doublenode = -1;

                        for (int linkedPin = 0; linkedPin <= 1; linkedPin++)
                        {
                            if (linkedSection.Pins[linkedDirection, linkedPin].Link == thisNode)
                            {
                                linkfound = true;
                                if (linkedSection.ActivePins[linkedDirection, linkedPin].Link == -1)
                                {
                                    linkedSection.ActivePins[linkedDirection, linkedPin].Link = thisNode;
                                }
                                else
                                {
                                    doublelink = true;
                                    doublenode = linkedSection.ActivePins[linkedDirection, linkedPin].Link;
                                }
                            }
                        }

                        if (!linkfound)
                        {
                            Trace.TraceWarning("Ignored invalid track node {0} pin [{1},{2}] link to track node {3}", thisNode, iDirection, iPin, linkedNode);
                            int endNode = nextNode;
                            nextNode++;
                            insertEndNode(thisNode, iDirection, iPin, endNode);
                        }

                        if (doublelink)
                        {
                            Trace.TraceWarning("Ignored invalid track node {0} pin [{1},{2}] link to track node {3}; already linked to track node {4}", thisNode, iDirection, iPin, linkedNode, doublenode);
                            int endNode = nextNode;
                            nextNode++;
                            insertEndNode(thisNode, iDirection, iPin, endNode);
                        }
                    }
                    else if (linkedNode == 0)
                    {
                        Trace.TraceWarning("Ignored invalid track node {0} pin [{1},{2}] link to track node {3}", thisNode, iDirection, iPin, linkedNode);
                        int endNode = nextNode;
                        nextNode++;
                        insertEndNode(thisNode, iDirection, iPin, endNode);
                    }
                }
            }

            return (nextNode);
        }

        //================================================================================================//
        //
        // insert end node to capture database break
        //

        private void insertEndNode(int thisNode, int direction, int pin, int endNode)
        {

            TrackCircuitSection thisSection = TrackCircuitList[thisNode];
            TrackCircuitSection endSection = new TrackCircuitSection(endNode, this);

            endSection.CircuitType = TrackCircuitSection.TrackCircuitType.EndOfTrack;
            int endDirection = direction == 0 ? 1 : 0;
            int iDirection = thisSection.Pins[direction, pin].Direction == 0 ? 1 : 0;
            endSection.Pins[iDirection, 0].Direction = endDirection;
            endSection.Pins[iDirection, 0].Link = thisNode;

            thisSection.Pins[direction, pin].Link = endNode;

            TrackCircuitList.Add(endSection);
        }

        //================================================================================================//
        //
        // set active pins for non-junction links
        // set trailing link indications
        //

        private void setActivePins(int thisNode)
        {
            TrackCircuitSection thisSection = TrackCircuitList[thisNode];

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                for (int iPin = 0; iPin <= 1; iPin++)
                {
                    if (thisSection.Pins[iDirection, iPin].Link > 0)
                    {
                        TrackCircuitSection nextSection = null;

                        if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                        {
                            int nextIndex = thisSection.Pins[iDirection, iPin].Link;
                            nextSection = TrackCircuitList[nextIndex];

                            if (thisSection.Pins[iDirection, 1].Link > 0)    // Junction end
                            {
                                thisSection.ActivePins[iDirection, iPin].Direction =
                                    thisSection.Pins[iDirection, iPin].Direction;
                                thisSection.ActivePins[iDirection, iPin].Link = -1;
                            }
                            else
                            {
                                thisSection.ActivePins[iDirection, iPin].Direction =
                                    thisSection.Pins[iDirection, iPin].Direction;
                                thisSection.ActivePins[iDirection, iPin].Link =
                                    thisSection.Pins[iDirection, iPin].Link;
                            }
                        }
                        else if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                        {
                            int nextIndex = thisSection.Pins[iDirection, iPin].Link;
                            nextSection = TrackCircuitList[nextIndex];

                            thisSection.ActivePins[iDirection, iPin].Direction =
                                thisSection.Pins[iDirection, iPin].Direction;
                            thisSection.ActivePins[iDirection, iPin].Link = -1;
                        }
                        else
                        {
                            int nextIndex = thisSection.Pins[iDirection, iPin].Link;
                            nextSection = TrackCircuitList[nextIndex];

                            thisSection.ActivePins[iDirection, iPin].Direction =
                                thisSection.Pins[iDirection, iPin].Direction;
                            thisSection.ActivePins[iDirection, iPin].Link =
                                thisSection.Pins[iDirection, iPin].Link;
                        }


                        if (nextSection != null && nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                        {
                            thisSection.ActivePins[iDirection, iPin].Link = -1;
                            if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
                            {
                                thisSection.EndIsTrailingJunction[iDirection] = true;
                            }
                        }
                        else if (nextSection != null && nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                        {
                            int nextDirection = thisSection.Pins[iDirection, iPin].Direction == 0 ? 1 : 0;
                            //                          int nextDirection = thisSection.Pins[iDirection, iPin].Direction;
                            if (nextSection.Pins[nextDirection, 1].Link > 0)
                            {
                                thisSection.ActivePins[iDirection, iPin].Link = -1;
                                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
                                {
                                    thisSection.EndIsTrailingJunction[iDirection] = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // set cross-reference to tracknodes
        //

        private void setCrossReference(int thisNode, TrackNode[] trackNodes)
        {
            TrackCircuitSection thisSection = TrackCircuitList[thisNode];
            if (thisSection.OriginalIndex > 0 && thisSection.CircuitType != TrackCircuitSection.TrackCircuitType.Crossover)
            {
                TrackNode thisTrack = trackNodes[thisSection.OriginalIndex];
                float offset0 = thisSection.OffsetLength[0];
                float offset1 = thisSection.OffsetLength[1];

                TrackCircuitSectionXref newReference = new TrackCircuitSectionXref(thisSection.Index, thisSection.Length, thisSection.OffsetLength);

                bool inserted = false;

                if (thisTrack.TCCrossReference == null)
                {
                    thisTrack.TCCrossReference = new TrackCircuitXRefList();
                    TrackCircuitXRefList thisXRef = thisTrack.TCCrossReference;
                }
                else
                {
                    TrackCircuitXRefList thisXRef = thisTrack.TCCrossReference;
                    for (int iPart = 0; iPart < thisXRef.Count && !inserted; iPart++)
                    {
                        TrackCircuitSectionXref thisReference = thisXRef[iPart];
                        if (offset0 < thisReference.OffsetLength[0])
                        {
                            thisXRef.Insert(iPart, newReference);
                            inserted = true;
                        }
                        else if (offset1 > thisReference.OffsetLength[1])
                        {
                            thisXRef.Insert(iPart, newReference);
                            inserted = true;
                        }
                    }
                }

                if (!inserted)
                {
                    TrackCircuitXRefList thisXRef = thisTrack.TCCrossReference;
                    thisXRef.Add(newReference);
                }
            }
        }

        //================================================================================================//
        //
        // set cross-reference to tracknodes for CrossOver items
        //

        private void setCrossReferenceCrossOver(int thisNode, TrackNode[] trackNodes)
        {
            TrackCircuitSection thisSection = TrackCircuitList[thisNode];
            if (thisSection.OriginalIndex > 0 && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
            {
                for (int iPin = 0; iPin <= 1; iPin++)
                {
                    int prevIndex = thisSection.Pins[0, iPin].Link;
                    TrackCircuitSection prevSection = TrackCircuitList[prevIndex];

                    TrackCircuitSectionXref newReference = new TrackCircuitSectionXref(thisSection.Index, thisSection.Length, thisSection.OffsetLength);
                    TrackNode thisTrack = trackNodes[prevSection.OriginalIndex];
                    TrackCircuitXRefList thisXRef = thisTrack.TCCrossReference;

                    bool inserted = false;
                    for (int iPart = 0; iPart < thisXRef.Count && !inserted; iPart++)
                    {
                        TrackCircuitSectionXref thisReference = thisXRef[iPart];
                        if (thisReference.Index == prevIndex)
                        {
                            newReference.OffsetLength[0] = thisReference.OffsetLength[0];
                            newReference.OffsetLength[1] = thisReference.OffsetLength[1] + thisReference.Length;
                            thisXRef.Insert(iPart, newReference);
                            inserted = true;
                        }
                    }

                    if (!inserted)
                    {
                        Trace.TraceWarning("ERROR : cannot find XRef for leading track to crossover {0}",
                            thisNode);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Set trackcircuit cross reference for signal items
        //

        private void setSignalCrossReference(int thisNode)
        {

            TrackCircuitSection thisSection = TrackCircuitList[thisNode];

            // process end signals

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                SignalObject thisSignal = thisSection.EndSignals[iDirection];
                if (thisSignal != null)
                {
                    thisSignal.TCReference = thisNode;
                    thisSignal.TCOffset = thisSection.Length;
                    thisSignal.TCDirection = iDirection;

                    //                  int pinIndex = iDirection == 0 ? 1 : 0;
                    int pinIndex = iDirection;
                    thisSignal.TCNextTC = thisSection.Pins[pinIndex, 0].Link;
                    thisSignal.TCNextDirection = thisSection.Pins[pinIndex, 0].Direction;
                }
            }

            // process other signals - only set info if not already set

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                {
                    TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[iDirection, fntype];
                    foreach (TrackCircuitSignalItem thisItem in thisList.TrackCircuitItem)
                    {
                        SignalObject thisSignal = thisItem.SignalRef;

                        if (thisSignal.TCReference <= 0)
                        {
                            thisSignal.TCReference = thisNode;
                            thisSignal.TCOffset = thisItem.SignalLocation;
                            thisSignal.TCDirection = iDirection;
                        }
                    }
                }
            }


            // process speedposts

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSpeedPosts[iDirection];
                foreach (TrackCircuitSignalItem thisItem in thisList.TrackCircuitItem)
                {
                    SignalObject thisSignal = thisItem.SignalRef;

                    if (thisSignal.TCReference <= 0)
                    {
                        thisSignal.TCReference = thisNode;
                        thisSignal.TCOffset = thisItem.SignalLocation;
                        thisSignal.TCDirection = iDirection;
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Set physical switch
        //

        public void setSwitch(int nodeIndex, int switchPos, TrackCircuitSection thisSection)
        {
            if (MPManager.NoAutoSwitch()) return;
            TrackNode thisNode = trackDB.TrackNodes[nodeIndex];
            thisNode.TrJunctionNode.SelectedRoute = switchPos;
            thisSection.JunctionLastRoute = switchPos;
        }

        //================================================================================================//
        //
        // Node control track clearance update request
        //

        public void requestClearNode(Train.TrainRouted thisTrain, Train.TCSubpathRoute routePart)
        {
#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Request for clear node from train {0} at section {1} starting from {2}\n",
				thisTrain.Train.Number,
				thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex,
				thisTrain.Train.LastReservedSection[thisTrain.TrainRouteDirectionIndex]));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Request for clear node from train {0} at section {1} starting from {2}\n",
                    thisTrain.Train.Number,
                    thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex,
                    thisTrain.Train.LastReservedSection[thisTrain.TrainRouteDirectionIndex]));
            }

            // check if present clearance is beyond required maximum distance

            int sectionIndex = -1;
            Train.TCRouteElement thisElement = null;
            TrackCircuitSection thisSection = null;

            List<int> sectionsInRoute = new List<int>();

            float clearedDistanceM = 0.0f;
            Train.END_AUTHORITY endAuthority = Train.END_AUTHORITY.NO_PATH_RESERVED;
            int routeIndex = -1;
            float maxDistance = Math.Max(thisTrain.Train.AllowedMaxSpeedMpS * thisTrain.Train.maxTimeS, thisTrain.Train.minCheckDistanceM);

            int lastReserved = thisTrain.Train.LastReservedSection[thisTrain.TrainRouteDirectionIndex];
            int endListIndex = -1;

            bool furthestRouteCleared = false;

            Train.TCSubpathRoute thisRoute = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex];
            Train.TCPosition thisPosition = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex];

            // for loop detection, set occupied sections in sectionsInRoute list - but remove present position

            foreach (TrackCircuitSection occSection in thisTrain.Train.OccupiedTrack)
            {
                sectionsInRoute.Add(occSection.Index);
            }
            sectionsInRoute.Remove(thisPosition.TCSectionIndex);

            // check if last reserved on present route

            if (lastReserved > 0)
            {

                endListIndex = thisRoute.GetRouteIndex(lastReserved, thisPosition.RouteListIndex);

                // check if backward in route - if so, route is valid and obstacle is in present section

                if (endListIndex < 0)
                {
                    int prevListIndex = -1;
                    for (int iNode = thisPosition.RouteListIndex; iNode >= 0 && prevListIndex < 0; iNode--)
                    {
                        thisElement = thisRoute[iNode];
                        if (thisElement.TCSectionIndex == lastReserved)
                        {
                            prevListIndex = iNode;
                        }
                    }

                    if (prevListIndex < 0)     // section is really off route - perform request from present position
                    {
                        BreakDownRoute(thisPosition.TCSectionIndex, thisTrain);
                    }
                }
            }

            if (thisTrain.Train.CheckTrain)
            {
                if (endListIndex >= 0)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Index in route list : {0} = {1}\n",
                        endListIndex, thisRoute[endListIndex].TCSectionIndex));
                }
                else
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Index in route list : {0}\n",
                        endListIndex));
                }
            }

            // if section is (still) set, check if this is at maximum distance

            if (endListIndex >= 0)
            {
                routeIndex = endListIndex;
                clearedDistanceM = thisTrain.Train.GetDistanceToTrain(lastReserved, 0.0f);

                if (clearedDistanceM > maxDistance)
                {
                    endAuthority = Train.END_AUTHORITY.MAX_DISTANCE;
                    furthestRouteCleared = true;
                    if (thisTrain.Train.CheckTrain)
                    {
                        File.AppendAllText(@"C:\temp\checktrain.txt",
                            String.Format("Cleared Distance : {0} > Max Distance \n",
                            FormatStrings.FormatDistance(clearedDistanceM, true)));
                    }

                }
                else
                {
                    for (int iIndex = thisPosition.RouteListIndex + 1; iIndex < routeIndex; iIndex++)
                    {
                        sectionsInRoute.Add(thisRoute[iIndex].TCSectionIndex);
                    }
                }
            }
            else
            {
                routeIndex = thisPosition.RouteListIndex;   // obstacle is in present section
            }

            if (routeIndex < 0) return;//by JTang

            int lastRouteIndex = routeIndex;
            float offset = 0.0f;
            if (routeIndex == thisPosition.RouteListIndex)
            {
                offset = thisPosition.TCOffset;
            }

            // if authority type is loop and loop section is still occupied by train, no need for any checks

            if (thisTrain.Train.LoopSection >= 0)
            {
                thisSection = TrackCircuitList[thisTrain.Train.LoopSection];
                if (thisSection.CircuitState.ThisTrainOccupying(thisTrain.Train) ||
                    (thisSection.CircuitState.TrainReserved != null && thisSection.CircuitState.TrainReserved.Train == thisTrain.Train))
                {
                    furthestRouteCleared = true;
                    endAuthority = Train.END_AUTHORITY.LOOP;
                }
                else
                {
                    // update trains ValidRoute to avoid continuation at wrong entry
                    int rearIndex = thisTrain.Train.PresentPosition[1].RouteListIndex;
                    int nextIndex = routePart.GetRouteIndex(thisTrain.Train.LoopSection, rearIndex);
                    int firstIndex = routePart.GetRouteIndex(thisTrain.Train.LoopSection, 0);

                    if (firstIndex != nextIndex)
                    {
                        for (int iIndex = 0; iIndex < rearIndex; iIndex++)
                        {
                            thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][iIndex].TCSectionIndex = -1; // invalidate route upto loop point
                        }
                        routePart = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex];
                    }

                    thisTrain.Train.LoopSection = -1;
                }
            }

            // try to clear further ahead if required

            if (!furthestRouteCleared)
            {

                if (thisTrain.Train.CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Starting check from : Index in route list : {0} = {1}\n",
                        routeIndex, thisRoute[routeIndex].TCSectionIndex));
                }

                // check if train ahead still in last available section

                bool routeAvailable = true;
                thisSection = TrackCircuitList[routePart[routeIndex].TCSectionIndex];

                Dictionary<Train, float> trainAhead =
                        thisSection.TestTrainAhead(thisTrain.Train, thisPosition.TCOffset, thisPosition.TCDirection);

                if (thisTrain.Train.CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Train ahead in section {0} : {1}\n",
                        thisSection.Index, trainAhead.Count));
                }

                if (trainAhead.Count > 0)
                {
                    routeAvailable = false;
                    lastRouteIndex = routeIndex - 1;
                    if (thisTrain.Train.CheckTrain)
                    {
                        if (lastRouteIndex >= 0)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                            String.Format("Set last valid section : Index in route list : {0} = {1}\n",
                            lastRouteIndex, thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][lastRouteIndex].TCSectionIndex));
                        }
                        else
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt", "First Section in Route\n");
                        }
                    }
                }

                // train ahead has moved on, check next sections

                int startRouteIndex = routeIndex;

                while (routeIndex < routePart.Count && routeAvailable && !furthestRouteCleared)
                {
                    if (thisTrain.Train.CheckTrain)
                    {
                        File.AppendAllText(@"C:\temp\checktrain.txt",
                            String.Format("Checking : Index in route list : {0} = {1}\n",
                            routeIndex, thisRoute[routeIndex].TCSectionIndex));
                    }

                    thisElement = routePart[routeIndex];
                    sectionIndex = thisElement.TCSectionIndex;
                    thisSection = TrackCircuitList[sectionIndex];

                    // check if section is in loop

                    if (sectionsInRoute.Contains(thisSection.Index) ||
                        (routeIndex > startRouteIndex && sectionIndex == thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex))
                    {
                        endAuthority = Train.END_AUTHORITY.LOOP;
                        thisTrain.Train.LoopSection = thisSection.Index;
                        routeAvailable = false;

                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Section looped \n");
                        }
                    }

                    // check if section is available

                    else if (thisSection.GetSectionStateClearNode(thisTrain, thisElement.Direction, routePart))
                    {
                        lastReserved = thisSection.Index;
                        lastRouteIndex = routeIndex;
                        sectionsInRoute.Add(thisSection.Index);
                        clearedDistanceM += thisSection.Length - offset;
                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Section clear \n");
                        }

                        routeIndex++;
                        offset = 0.0f;

                        if (!thisSection.CircuitState.ThisTrainOccupying(thisTrain) &&
                            thisSection.CircuitState.TrainReserved == null)
                        {
                            thisSection.Reserve(thisTrain, routePart);
                        }

                        if (thisSection.EndSignals[thisElement.Direction] != null)
                        {
                            if (Simulator.TimetableMode || routeIndex < routePart.Count)
                            {
                                thisTrain.Train.SwitchToSignalControl(thisSection.EndSignals[thisElement.Direction]);
                            }
                            furthestRouteCleared = true;
                            if (thisTrain.Train.CheckTrain)
                            {
                                File.AppendAllText(@"C:\temp\checktrain.txt",
                                    String.Format("Has end signal : {0}\n",
                                    thisSection.EndSignals[thisElement.Direction].thisRef));
                            }
                        }

                        if (clearedDistanceM > thisTrain.Train.minCheckDistanceM &&
                                        clearedDistanceM > (thisTrain.Train.AllowedMaxSpeedMpS * thisTrain.Train.maxTimeS))
                        {
                            endAuthority = Train.END_AUTHORITY.MAX_DISTANCE;
                            furthestRouteCleared = true;
                        }


                    }
                    else
                    {
                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Section blocked \n");
                        }
                        lastRouteIndex = routeIndex - 1;
                        lastReserved = lastRouteIndex >= 0 ? routePart[lastRouteIndex].TCSectionIndex : -1;
                        routeAvailable = false;
                    }
                }
            }

            // if not cleared to max distance or looped, determine reason

            if (!furthestRouteCleared && lastRouteIndex > 0 && endAuthority != Train.END_AUTHORITY.LOOP)
            {

                thisElement = routePart[lastRouteIndex];
                sectionIndex = thisElement.TCSectionIndex;
                thisSection = TrackCircuitList[sectionIndex];

                if (thisTrain.Train.CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Last section cleared in route list : {0} = {1}\n",
                        lastRouteIndex, thisRoute[lastRouteIndex].TCSectionIndex));
                }
                // end of track reached

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
                {
                    endAuthority = Train.END_AUTHORITY.END_OF_TRACK;
                    furthestRouteCleared = true;
                    if (thisTrain.Train.CheckTrain)
                    {
                        File.AppendAllText(@"C:\temp\checktrain.txt",
                            "End of track \n");
                    }
                }

                // end of path reached

                if (!furthestRouteCleared)
                {
                    if (lastRouteIndex >= (routePart.Count - 1))
                    {
                        endAuthority = Train.END_AUTHORITY.END_OF_PATH;
                        furthestRouteCleared = true;
                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "End of path \n");
                        }
                    }
                }
            }

            // check if next section is switch held against train

            if (!furthestRouteCleared && lastRouteIndex < (routePart.Count - 1))
            {
                Train.TCRouteElement nextElement = routePart[lastRouteIndex + 1];
                sectionIndex = nextElement.TCSectionIndex;
                thisSection = TrackCircuitList[sectionIndex];
                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                    thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    if (!thisSection.IsAvailable(thisTrain))
                    {
                        endAuthority = Train.END_AUTHORITY.RESERVED_SWITCH;
                        furthestRouteCleared = true;
                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Reserved Switch \n");
                        }
                    }
                }
            }

            // check if next section is occupied by stationary train or train moving in similar direction
            // if so calculate distance to end of train
            // only allowed for NORMAL sections and if not looped

            if (!furthestRouteCleared && lastRouteIndex < (routePart.Count - 1) && endAuthority != Train.END_AUTHORITY.LOOP)
            {
                Train.TCRouteElement nextElement = routePart[lastRouteIndex + 1];
                int reqDirection = nextElement.Direction;
                int revDirection = nextElement.Direction == 0 ? 1 : 0;

                sectionIndex = nextElement.TCSectionIndex;
                thisSection = TrackCircuitList[sectionIndex];

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal &&
                           thisSection.CircuitState.HasOtherTrainsOccupying(thisTrain))
                {
                    if (thisSection.CircuitState.HasOtherTrainsOccupying(revDirection, false, thisTrain))
                    {
                        endAuthority = Train.END_AUTHORITY.TRAIN_AHEAD;
                        furthestRouteCleared = true;
                        if (thisTrain.Train.CheckTrain)
                        {
                            File.AppendAllText(@"C:\temp\checktrain.txt",
                                "Train Ahead \n");
                        }
                    }
                    // check for train further ahead and determine distance to train
                    Dictionary<Train, float> trainAhead =
                                            thisSection.TestTrainAhead(thisTrain.Train, offset, reqDirection);

                    if (trainAhead.Count > 0)
                    {
                        foreach (KeyValuePair<Train, float> thisTrainAhead in trainAhead)  // there is only one value
                        {
                            endAuthority = Train.END_AUTHORITY.TRAIN_AHEAD;
                            clearedDistanceM += thisTrainAhead.Value;
                            furthestRouteCleared = true;
                            if (thisTrain.Train.CheckTrain)
                            {
                                File.AppendAllText(@"C:\temp\checktrain.txt",
                                    "Train Ahead \n");
                            }
                        }
                    }
                }
                else if (thisSection.GetSectionStateClearNode(thisTrain, thisElement.Direction, routePart))
                {
                    endAuthority = Train.END_AUTHORITY.END_OF_AUTHORITY;
                    furthestRouteCleared = true;
                }
            }

            if (routeIndex >= routePart.Count)
            {
                endAuthority = Train.END_AUTHORITY.END_OF_AUTHORITY;
            }

            // update train details

            thisTrain.Train.EndAuthorityType[thisTrain.TrainRouteDirectionIndex] = endAuthority;
            thisTrain.Train.LastReservedSection[thisTrain.TrainRouteDirectionIndex] = lastReserved;
            thisTrain.Train.DistanceToEndNodeAuthorityM[thisTrain.TrainRouteDirectionIndex] = clearedDistanceM;
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Returned : \n    State : {0}\n    Dist  : {1}\n    Sect  : {2}\n",
                    endAuthority, FormatStrings.FormatDistance(clearedDistanceM, true), lastReserved));
            }
        }

        //================================================================================================//
        //
        // Break down reserved route
        //

        public void BreakDownRoute(int firstSectionIndex, Train.TrainRouted reqTrain)
        {
            if (firstSectionIndex < 0)
                return; // no route to break down

            TrackCircuitSection firstSection = TrackCircuitList[firstSectionIndex];
            Train.TrainRouted thisTrain = firstSection.CircuitState.TrainReserved;

            // if occupied by train - skip actions and proceed to next section

            if (!firstSection.CircuitState.ThisTrainOccupying(reqTrain))
            {

                // if not reserved - no further route ahead

                if (thisTrain == null)
                {
                    return;
                }

                if (thisTrain != reqTrain)
                {
                    return;   // section reserved for other train - stop action
                }

                // unreserve first section

                firstSection.UnreserveTrain(thisTrain, true);
            }

            // check which direction to go

            TrackCircuitSection nextSection = null;
            int nextDirection = 0;

            for (int iPinLink = 0; iPinLink <= 1; iPinLink++)
            {
                for (int iPinIndex = 0; iPinIndex <= 1; iPinIndex++)
                {
                    int trySectionIndex = firstSection.Pins[iPinLink, iPinIndex].Link;
                    if (trySectionIndex > 0)
                    {
                        TrackCircuitSection trySection = TrackCircuitList[trySectionIndex];
                        if (trySection.CircuitState.TrainReserved != null && reqTrain != null && trySection.CircuitState.TrainReserved.Train == reqTrain.Train)
                        {
                            nextSection = trySection;
                            nextDirection = firstSection.Pins[iPinLink, iPinIndex].Direction;
                        }
                    }
                }
            }

            // run back through all reserved sections

            while (nextSection != null)
            {
                nextSection.UnreserveTrain(reqTrain, true);
                TrackCircuitSection thisSection = nextSection;
                nextSection = null;

                // try to find next section using active links

                TrackCircuitSection trySection = null;

                int iPinLink = nextDirection;
                for (int iPinIndex = 0; iPinIndex <= 1; iPinIndex++)
                {
                    int trySectionIndex = thisSection.ActivePins[iPinLink, iPinIndex].Link;
                    if (trySectionIndex > 0)
                    {
                        trySection = TrackCircuitList[trySectionIndex];
                        if (trySection.CircuitState.TrainReserved != null && reqTrain != null && trySection.CircuitState.TrainReserved.Train == reqTrain.Train)
                        {
                            nextSection = trySection;
                            nextDirection = thisSection.ActivePins[iPinLink, iPinIndex].Direction;
                        }
                    }
                }

                // not found, then try possible links

                for (int iPinIndex = 0; iPinIndex <= 1; iPinIndex++)
                {
                    int trySectionIndex = thisSection.Pins[iPinLink, iPinIndex].Link;
                    if (trySectionIndex > 0)
                    {
                        trySection = TrackCircuitList[trySectionIndex];
                        if (trySection.CircuitState.TrainReserved != null && reqTrain != null && trySection.CircuitState.TrainReserved.Train == reqTrain.Train)
                        {
                            nextSection = trySection;
                            nextDirection = thisSection.Pins[iPinLink, iPinIndex].Direction;
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Break down reserved route using route list
        //

        public void BreakDownRouteList(Train.TCSubpathRoute reqRoute, int firstRouteIndex, Train.TrainRouted reqTrain)
        {
            for (int iindex = reqRoute.Count - 1; iindex >= 0 && iindex >= firstRouteIndex; iindex--)
            {
                TrackCircuitSection thisSection = TrackCircuitList[reqRoute[iindex].TCSectionIndex];
                if (!thisSection.CircuitState.ThisTrainOccupying(reqTrain.Train))
                {
                    thisSection.RemoveTrain(reqTrain.Train, true);
                }
                else
                {
                    SignalObject thisSignal = thisSection.EndSignals[reqRoute[iindex].Direction];
                    if (thisSignal != null)
                    {
                        thisSignal.ResetSignal(false);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Build temp route for train
        // Used for trains without path (eg stationary constists), manual operation
        //

        public Train.TCSubpathRoute BuildTempRoute(Train thisTrain,
                int firstSectionIndex, float firstOffset, int firstDirection,
                float routeLength, bool overrideManualSwitchState, bool autoAlign, bool stopAtFacingSignal)
        {
            bool honourManualSwitchState = !overrideManualSwitchState;
            List<int> sectionList = ScanRoute(thisTrain, firstSectionIndex, firstOffset, firstDirection,
                    true, routeLength, honourManualSwitchState, autoAlign, stopAtFacingSignal, false, true, false, false, false, false, false);
            Train.TCSubpathRoute tempRoute = new Train.TCSubpathRoute();
            int lastIndex = -1;

            foreach (int nextSectionIndex in sectionList)
            {
                int curDirection = nextSectionIndex < 0 ? 1 : 0;
                int thisSectionIndex = nextSectionIndex < 0 ? -nextSectionIndex : nextSectionIndex;
                TrackCircuitSection thisSection = TrackCircuitList[thisSectionIndex];

                Train.TCRouteElement thisElement = new Train.TCRouteElement(thisSection, curDirection, this, lastIndex);
                tempRoute.Add(thisElement);
                lastIndex = thisSectionIndex;
            }

            // set pin references for junction sections
            for (int iElement = 0; iElement < tempRoute.Count - 1; iElement++) // do not process last element as next element is required
            {
                Train.TCRouteElement thisElement = tempRoute[iElement];
                TrackCircuitSection thisSection = TrackCircuitList[thisElement.TCSectionIndex];

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                {
                    if (thisElement.OutPin[0] == 1) // facing switch
                    {
                        thisElement.OutPin[1] = thisSection.Pins[1, 0].Link == tempRoute[iElement + 1].TCSectionIndex ? 0 : 1;
                    }
                }
            }

            return (tempRoute);
        }

        //================================================================================================//
        //
        // Follow default route for train
        // Use for :
        //   - build temp list for trains without route (eg stat objects)
        //   - build list for train under Manual control
        //   - build list of sections when train slip backward
        //   - search signal or speedpost ahead or at the rear of the train (either in facing or backward direction)
        //
        // Search ends :
        //   - if required object is found
        //   - if required length is covered
        //   - if valid path only is requested and unreserved section is found (variable thisTrain required)
        //   - end of track
        //   - looped track
        //
        // Returned is list of sections, with positive no. indicating direction 0 and negative no. indicating direction 1
        // If signal or speedpost is required, list will contain index of required item (>0 facing direction, <0 backing direction)
        //

        public List<int> ScanRoute(Train thisTrain, int firstSectionIndex, float firstOffset, int firstDirection, bool forward,
                float routeLength, bool honourManualSwitch, bool autoAlign, bool stopAtFacingSignal, bool reservedOnly, bool returnSections,
                bool searchFacingSignal, bool searchBackwardSignal, bool searchFacingSpeedpost, bool searchBackwardSpeedpost,
                bool isFreight)
        {

            int sectionIndex = firstSectionIndex;

            int lastIndex = -2;   // set to values not encountered for pin links
            int thisIndex = sectionIndex;

            float offset = firstOffset;
            int curDirection = firstDirection;
            int nextDirection = curDirection;

            TrackCircuitSection thisSection = TrackCircuitList[sectionIndex];

            float coveredLength = firstOffset;
            if (forward || (firstDirection == 1 && !forward))
            {
                coveredLength = thisSection.Length - firstOffset;
            }

            bool endOfRoute = false;
            List<int> foundItems = new List<int>();
            List<int> foundObject = new List<int>();

            while (!endOfRoute)
            {

                // check looped

                int routedIndex = curDirection == 0 ? thisIndex : -thisIndex;
                if (foundItems.Contains(thisIndex) || foundItems.Contains(-thisIndex))
                {
                    break;
                }

                // add section
                foundItems.Add(routedIndex);

                // set length, pin index and opp direction

                int oppDirection = curDirection == 0 ? 1 : 0;

                int outPinIndex = forward ? curDirection : oppDirection;
                int inPinIndex = outPinIndex == 0 ? 1 : 0;

                // check all conditions and objects as required

                if (stopAtFacingSignal && thisSection.EndSignals[curDirection] != null)           // stop at facing signal
                {
                    endOfRoute = true;
                }

                if (searchFacingSignal && thisSection.EndSignals[curDirection] != null)           // search facing signal
                {
                    foundObject.Add(thisSection.EndSignals[curDirection].thisRef);
                    endOfRoute = true;
                }

                // search facing speedpost
                if (searchFacingSpeedpost && thisSection.CircuitItems.TrackCircuitSpeedPosts[curDirection].TrackCircuitItem.Count > 0)
                {
                    List<TrackCircuitSignalItem> thisItemList = thisSection.CircuitItems.TrackCircuitSpeedPosts[curDirection].TrackCircuitItem;

                    if (forward)
                    {
                        for (int iObject = 0; iObject < thisItemList.Count && !endOfRoute; iObject++)
                        {
                            TrackCircuitSignalItem thisItem = thisItemList[iObject];

                            SignalObject thisSpeedpost = thisItem.SignalRef;
                            ObjectSpeedInfo speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);

                            if ((isFreight && speed_info.speed_freight > 0) || (!isFreight && speed_info.speed_pass > 0))
                            {
                                if (thisItem.SignalLocation > offset)
                                {
                                    foundObject.Add(thisItem.SignalRef.thisRef);
                                    endOfRoute = true;
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int iObject = thisItemList.Count - 1; iObject >= 0 && !endOfRoute; iObject--)
                        {
                            TrackCircuitSignalItem thisItem = thisItemList[iObject];

                            SignalObject thisSpeedpost = thisItem.SignalRef;
                            ObjectSpeedInfo speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);

                            if ((isFreight && speed_info.speed_freight > 0) || (!isFreight && speed_info.speed_pass > 0))
                            {
                                if (offset == 0 || thisItem.SignalLocation < offset)
                                {
                                    foundObject.Add(thisItem.SignalRef.thisRef);
                                    endOfRoute = true;
                                }
                            }
                        }
                    }
                }

                // search backward speedpost
                if (searchBackwardSpeedpost && thisSection.CircuitItems.TrackCircuitSpeedPosts[oppDirection].TrackCircuitItem.Count > 0)
                {
                    List<TrackCircuitSignalItem> thisItemList = thisSection.CircuitItems.TrackCircuitSpeedPosts[oppDirection].TrackCircuitItem;

                    if (forward)
                    {
                        for (int iObject = thisItemList.Count - 1; iObject >= 0 && !endOfRoute; iObject--)
                        {
                            TrackCircuitSignalItem thisItem = thisItemList[iObject];

                            SignalObject thisSpeedpost = thisItem.SignalRef;
                            ObjectSpeedInfo speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);

                            if ((isFreight && speed_info.speed_freight > 0) || (!isFreight && speed_info.speed_pass > 0))
                            {
                                if (thisItem.SignalLocation < thisSection.Length - offset)
                                {
                                    endOfRoute = true;
                                    foundObject.Add(-(thisItem.SignalRef.thisRef));
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int iObject = 0; iObject < thisItemList.Count - 1 && !endOfRoute; iObject++)
                        {
                            TrackCircuitSignalItem thisItem = thisItemList[iObject];

                            SignalObject thisSpeedpost = thisItem.SignalRef;
                            ObjectSpeedInfo speed_info = thisSpeedpost.this_lim_speed(MstsSignalFunction.SPEED);

                            if ((isFreight && speed_info.speed_freight > 0) || (!isFreight && speed_info.speed_pass > 0))
                            {
                                if (offset == 0 || thisItem.SignalLocation > thisSection.Length - offset)
                                {
                                    endOfRoute = true;
                                    foundObject.Add(-(thisItem.SignalRef.thisRef));
                                }
                            }
                        }
                    }
                }

                // move to next section
                // follow active links if set, otherwise default links (=0)

                int nextIndex = -1;
                switch (thisSection.CircuitType)
                {
                    case TrackCircuitSection.TrackCircuitType.Crossover:
                        if (thisSection.Pins[inPinIndex, 0].Link == lastIndex)
                        {
                            nextIndex = thisSection.Pins[outPinIndex, 0].Link;
                            nextDirection = thisSection.Pins[outPinIndex, 0].Direction;
                        }
                        else if (thisSection.Pins[inPinIndex, 1].Link == lastIndex)
                        {
                            nextIndex = thisSection.Pins[outPinIndex, 1].Link;
                            nextDirection = thisSection.Pins[outPinIndex, 1].Direction;
                        }
                        break;

                    case TrackCircuitSection.TrackCircuitType.Junction:
                        if (thisSection.ActivePins[outPinIndex, 0].Link > 0)
                        {
                            nextIndex = thisSection.ActivePins[outPinIndex, 0].Link;
                            nextDirection = thisSection.ActivePins[outPinIndex, 0].Direction;
                        }
                        else if (thisSection.ActivePins[outPinIndex, 1].Link > 0)
                        {
                            nextIndex = thisSection.ActivePins[outPinIndex, 1].Link;
                            nextDirection = thisSection.ActivePins[outPinIndex, 1].Direction;
                        }
                        else if (honourManualSwitch && thisSection.JunctionSetManual >= 0)
                        {
                            nextIndex = thisSection.Pins[outPinIndex, thisSection.JunctionSetManual].Link;
                            nextDirection = thisSection.Pins[outPinIndex, thisSection.JunctionSetManual].Direction;
                        }
                        else if (!reservedOnly)
                        {
                            nextIndex = thisSection.Pins[outPinIndex, thisSection.JunctionLastRoute].Link;
                            nextDirection = thisSection.Pins[outPinIndex, thisSection.JunctionLastRoute].Direction;
                        }
                        break;

                    case TrackCircuitSection.TrackCircuitType.EndOfTrack:
                        break;

                    default:
                        nextIndex = thisSection.Pins[outPinIndex, 0].Link;
                        nextDirection = thisSection.Pins[outPinIndex, 0].Direction;

                        TrackCircuitSection nextSection = TrackCircuitList[nextIndex];

                        // if next section is junction : check if locked against AI and if auto-alignment allowed
                        // switchable end of switch is always pin direction 1
                        if (nextSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                        {
                            int nextPinDirection = nextDirection == 0 ? 1 : 0;
                            int nextPinIndex = nextSection.Pins[(nextDirection == 0 ? 1 : 0), 0].Link == thisIndex ? 0 : 1;
                            if (nextPinDirection == 1 && nextSection.JunctionLastRoute != nextPinIndex)
                            {
                                if (nextSection.AILock && thisTrain != null && (thisTrain.TrainType == Train.TRAINTYPE.AI
                                    || thisTrain.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING))
                                {
                                    endOfRoute = true;
                                }

                                if (!autoAlign)
                                {
                                    endOfRoute = true;
                                }
                            }
                        }

                        break;
                }

                if (nextIndex < 0)
                {
                    endOfRoute = true;
                }
                else
                {
                    lastIndex = thisIndex;
                    thisIndex = nextIndex;
                    thisSection = TrackCircuitList[thisIndex];
                    curDirection = forward ? nextDirection : nextDirection == 0 ? 1 : 0;
                    oppDirection = curDirection == 0 ? 1 : 0;

                    if (searchBackwardSignal && thisSection.EndSignals[oppDirection] != null)
                    {
                        endOfRoute = true;
                        foundObject.Add(-(thisSection.EndSignals[oppDirection].thisRef));
                    }
                }

                if (!endOfRoute)
                {
                    offset = 0.0f;

                    if (thisTrain != null && reservedOnly)
                    {
                        TrackCircuitState thisState = thisSection.CircuitState;

                        if (!thisState.TrainOccupy.ContainsTrain(thisTrain) &&
                            (thisState.TrainReserved != null && thisState.TrainReserved.Train != thisTrain))
                        {
                            endOfRoute = true;
                        }
                    }
                }

                if (!endOfRoute && routeLength > 0)
                {
                    endOfRoute = (coveredLength > routeLength);
                    coveredLength += thisSection.Length;
                }

            }

            if (returnSections)
            {
                return (foundItems);
            }
            else
            {
                return (foundObject);
            }
        }

        //================================================================================================//
        //
        // Process Platforms
        //

        private void ProcessPlatforms(Dictionary<int, int> platformList, TrItem[] TrItems,
                TrackNode[] trackNodes)
        {
            foreach (KeyValuePair<int, int> thisPlatformIndex in platformList)
            {
                int thisPlatformDetailsIndex;

                // get platform item

                int thisIndex = thisPlatformIndex.Key;

                var thisPlatform = TrItems[thisIndex] is PlatformItem ? (PlatformItem)TrItems[thisIndex] : new PlatformItem((SidingItem)TrItems[thisIndex]);

                TrackNode thisNode = trackNodes[thisPlatformIndex.Value];

                // check if entry already created for related entry

                int relatedIndex = (int)thisPlatform.LinkedPlatformItemId;

                PlatformDetails thisDetails;
                int refIndex;
                bool splitPlatform = false;

                // get related platform details

                if (PlatformXRefList.ContainsKey(relatedIndex))
                {
                    thisPlatformDetailsIndex = PlatformXRefList[relatedIndex];
                    thisDetails = PlatformDetailsList[thisPlatformDetailsIndex];
                    PlatformXRefList.Add(thisIndex, thisPlatformDetailsIndex);
                    refIndex = 1;
                }

        // create new platform details

                else
                {
                    thisDetails = new PlatformDetails(thisIndex);
                    PlatformDetailsList.Add(thisDetails);
                    thisPlatformDetailsIndex = PlatformDetailsList.Count - 1;
                    PlatformXRefList.Add(thisIndex, thisPlatformDetailsIndex);
                    refIndex = 0;
                }

                // set station reference
                if (StationXRefList.ContainsKey(thisPlatform.Station))
                {
                    List<int> XRefList = StationXRefList[thisPlatform.Station];
                    XRefList.Add(thisPlatformDetailsIndex);
                }
                else
                {
                    List<int> XRefList = new List<int>();
                    XRefList.Add(thisPlatformDetailsIndex);
                    StationXRefList.Add(thisPlatform.Station, XRefList);
                }

                // get tracksection

                int TCSectionIndex = -1;
                int TCXRefIndex = -1;

                for (int iXRef = thisNode.TCCrossReference.Count - 1; iXRef >= 0 && TCSectionIndex < 0; iXRef--)
                {
                    if (thisPlatform.SData1 <
                     (thisNode.TCCrossReference[iXRef].OffsetLength[1] + thisNode.TCCrossReference[iXRef].Length))
                    {
                        TCSectionIndex = thisNode.TCCrossReference[iXRef].Index;
                        TCXRefIndex = iXRef;
                    }
                }

                if (TCSectionIndex < 0)
                {
                    Trace.TraceInformation("Cannot locate TCSection for platform {0}", thisIndex);
                    TCSectionIndex = thisNode.TCCrossReference[0].Index;
                    TCXRefIndex = 0;
                }

                // if first entry, set tracksection

                if (refIndex == 0)
                {
                    thisDetails.TCSectionIndex.Add(TCSectionIndex);
                }

        // if second entry, test if equal - if not, build list

                else
                {
                    if (TCSectionIndex != thisDetails.TCSectionIndex[0])
                    {
                        int firstXRef = -1;
                        for (int iXRef = thisNode.TCCrossReference.Count - 1; iXRef >= 0 && firstXRef < 0; iXRef--)
                        {
                            if (thisNode.TCCrossReference[iXRef].Index == thisDetails.TCSectionIndex[0])
                            {
                                firstXRef = iXRef;
                            }
                        }

                        if (firstXRef < 0)  // platform is split by junction !!!
                        {
                            ResolveSplitPlatform(ref thisDetails, TCSectionIndex, thisPlatform, thisNode,
                                    TrItems, trackNodes);
                            splitPlatform = true;
                            Trace.TraceInformation("Platform split by junction at " + thisDetails.Name);
                        }
                        else if (TCXRefIndex < firstXRef)
                        {
                            thisDetails.TCSectionIndex.Clear();
                            for (int iXRef = TCXRefIndex; iXRef <= firstXRef; iXRef++)
                            {
                                thisDetails.TCSectionIndex.Add(thisNode.TCCrossReference[iXRef].Index);
                            }
                        }
                        else
                        {
                            thisDetails.TCSectionIndex.Clear();
                            for (int iXRef = firstXRef; iXRef <= TCXRefIndex; iXRef++)
                            {
                                thisDetails.TCSectionIndex.Add(thisNode.TCCrossReference[iXRef].Index);
                            }
                        }
                    }
                }

                // set details (if not split platform)

                if (!splitPlatform)
                {
                    TrackCircuitSection thisSection = TrackCircuitList[TCSectionIndex];

                    thisDetails.PlatformReference[refIndex] = thisIndex;
                    thisDetails.nodeOffset[refIndex] = thisPlatform.SData1;
                    thisDetails.TCOffset[refIndex, 1] = thisPlatform.SData1 - thisSection.OffsetLength[1];
                    thisDetails.TCOffset[refIndex == 1 ? 0 : 1, 0] = thisSection.Length - thisDetails.TCOffset[refIndex, 1];
                }

                if (refIndex == 0)
                {
                    thisDetails.Name = String.Copy(thisPlatform.Station);
                    thisDetails.MinWaitingTime = thisPlatform.PlatformMinWaitingTime;
                    thisDetails.NumPassengersWaiting = (int)thisPlatform.PlatformNumPassengersWaiting;
                }
                else if (!splitPlatform)
                {
                    thisDetails.Length = Math.Abs(thisDetails.nodeOffset[1] - thisDetails.nodeOffset[0]);
                }

                // check if direction correct, else swap 0 - 1 entries for offsets etc.

                if (refIndex == 1 && thisDetails.nodeOffset[1] < thisDetails.nodeOffset[0] && !splitPlatform)
                {
                    float tf;
                    tf = thisDetails.nodeOffset[0];
                    thisDetails.nodeOffset[0] = thisDetails.nodeOffset[1];
                    thisDetails.nodeOffset[1] = tf;

                    for (int iDir = 0; iDir <= 1; iDir++)
                    {
                        tf = thisDetails.TCOffset[iDir, 0];
                        thisDetails.TCOffset[iDir, 0] = thisDetails.TCOffset[iDir, 1];
                        thisDetails.TCOffset[iDir, 1] = tf;
                    }
                }

                // search for end signals

                thisNode = trackNodes[TrackCircuitList[thisDetails.TCSectionIndex[0]].OriginalIndex];

                if (refIndex == 1)
                {
                    float distToSignal = 0.0f;
                    float offset = thisDetails.TCOffset[1, 0];
                    int lastSection = thisDetails.TCSectionIndex[thisDetails.TCSectionIndex.Count - 1];
                    int lastSectionXRef = -1;

                    for (int iXRef = 0; iXRef < thisNode.TCCrossReference.Count; iXRef++)
                    {
                        if (lastSection == thisNode.TCCrossReference[iXRef].Index)
                        {
                            lastSectionXRef = iXRef;
                            break;
                        }
                    }

                    for (int iXRef = lastSectionXRef; iXRef < thisNode.TCCrossReference.Count; iXRef++)
                    {
                        int sectionIndex = thisNode.TCCrossReference[iXRef].Index;
                        TrackCircuitSection thisSection = TrackCircuitList[sectionIndex];

                        distToSignal += thisSection.Length - offset;
                        offset = 0.0f;

                        if (thisSection.EndSignals[0] != null)
                        {
                            // end signal is always valid in timetable mode
                            if (Simulator.TimetableMode)
                            {
                                thisDetails.EndSignals[0] = thisSection.EndSignals[0].thisRef;
                                thisDetails.DistanceToSignals[0] = distToSignal;
                            }
                            // end signal is only valid if it has no fixed route in activity mode
                            else
                            {
                                if (!thisSection.EndSignals[0].hasFixedRoute)
                                {
                                    thisDetails.EndSignals[0] = thisSection.EndSignals[0].thisRef;
                                    thisDetails.DistanceToSignals[0] = distToSignal;
                                }
                            }
                            break;
                        }
                    }

                    distToSignal = 0.0f;
                    offset = thisDetails.TCOffset[1, 1];
                    int firstSection = thisDetails.TCSectionIndex[0];
                    int firstSectionXRef = lastSectionXRef;

                    if (lastSection != firstSection)
                    {
                        for (int iXRef = 0; iXRef < thisNode.TCCrossReference.Count; iXRef++)
                        {
                            if (firstSection == thisNode.TCCrossReference[iXRef].Index)
                            {
                                firstSectionXRef = iXRef;
                                break;
                            }
                        }
                    }

                    for (int iXRef = firstSectionXRef; iXRef >= 0; iXRef--)
                    {
                        int sectionIndex = thisNode.TCCrossReference[iXRef].Index;
                        TrackCircuitSection thisSection = TrackCircuitList[sectionIndex];

                        distToSignal += thisSection.Length - offset;
                        offset = 0.0f;

                        if (thisSection.EndSignals[1] != null)
                        {
                            if (Simulator.TimetableMode)
                            {
                                thisDetails.EndSignals[1] = thisSection.EndSignals[1].thisRef;
                                thisDetails.DistanceToSignals[1] = distToSignal;
                            }
                            else
                            {
                                if (!thisSection.EndSignals[1].hasFixedRoute)
                                {
                                    thisDetails.EndSignals[1] = thisSection.EndSignals[1].thisRef;
                                    thisDetails.DistanceToSignals[1] = distToSignal;
                                }
                            }
                            break;
                        }

                    }
                }

                // set section crossreference


                if (refIndex == 1)
                {
                    foreach (int sectionIndex in thisDetails.TCSectionIndex)
                    {
                        TrackCircuitSection thisSection = TrackCircuitList[sectionIndex];
                        thisSection.PlatformIndex.Add(thisPlatformDetailsIndex);
                    }
                }
            }

            if (!Simulator.TimetableMode && Simulator.Activity != null &&
                Simulator.Activity.Tr_Activity.Tr_Activity_File.PlatformNumPassengersWaiting != null)

            // Override .tdb NumPassengersWaiting info with .act NumPassengersWaiting info if any available
            {
                int overriddenPlatformDetailsIndex;
                foreach (PlatformData platformData in Simulator.Activity.Tr_Activity.Tr_Activity_File.PlatformNumPassengersWaiting.PlatformDataList)
                {
                    overriddenPlatformDetailsIndex = PlatformDetailsList.FindIndex(platformDetails => (platformDetails.PlatformReference[0] == platformData.Id) || (platformDetails.PlatformReference[1] == platformData.Id));
                    if (overriddenPlatformDetailsIndex >= 0) PlatformDetailsList[overriddenPlatformDetailsIndex].NumPassengersWaiting = platformData.PassengerCount;
                    else Trace.TraceWarning("Platform referenced in .act file with TrItemId {0} not present in .tdb file ", platformData.Id);
                }
            }

        }// ProcessPlatforms

        //================================================================================================//
        //
        // Resolve split platforms
        //

        public void ResolveSplitPlatform(ref PlatformDetails thisDetails, int secondSectionIndex,
                PlatformItem secondPlatform, TrackNode secondNode,
                    TrItem[] TrItems, TrackNode[] trackNodes)
        {
            // get all positions related to tile of first platform item

            PlatformItem firstPlatform = (TrItems[thisDetails.PlatformReference[0]] is PlatformItem) ?
                    (PlatformItem)TrItems[thisDetails.PlatformReference[0]] :
                    new PlatformItem((SidingItem)TrItems[thisDetails.PlatformReference[0]]);

            int firstSectionIndex = thisDetails.TCSectionIndex[0];
            TrackCircuitSection thisSection = TrackCircuitList[firstSectionIndex];
            TrackNode firstNode = trackNodes[thisSection.OriginalIndex];

            // first platform
            int TileX1 = firstPlatform.TileX;
            int TileZ1 = firstPlatform.TileZ;
            float X1 = firstPlatform.X;
            float Z1 = firstPlatform.Z;

            // start node position
            int TS1TileX = firstNode.TrVectorNode.TrVectorSections[0].TileX;
            int TS1TileZ = firstNode.TrVectorNode.TrVectorSections[0].TileZ;
            float TS1X = firstNode.TrVectorNode.TrVectorSections[0].X;
            float TS1Z = firstNode.TrVectorNode.TrVectorSections[0].Z;

            float TS1Xc = TS1X + (TS1TileX - TileX1) * 2048;
            float TS1Zc = TS1Z + (TS1TileZ - TileZ1) * 2048;

            // second platform
            int TileX2 = secondPlatform.TileX;
            int TileZ2 = secondPlatform.TileZ;
            float X2 = secondPlatform.X;
            float Z2 = secondPlatform.Z;

            float X2c = X2 + (TileX2 - TileX1) * 2048;
            float Z2c = Z2 + (TileZ2 - TileZ1) * 2048;

            int TS2TileX = secondNode.TrVectorNode.TrVectorSections[0].TileX;
            int TS2TileZ = secondNode.TrVectorNode.TrVectorSections[0].TileZ;
            float TS2X = secondNode.TrVectorNode.TrVectorSections[0].X;
            float TS2Z = secondNode.TrVectorNode.TrVectorSections[0].Z;

            float TS2Xc = TS2X + (TS2TileX - TileX1) * 2048;
            float TS2Zc = TS2Z + (TS2TileZ - TileZ1) * 2048;

            // determine if 2nd platform is towards end or begin of tracknode - use largest delta for check

            float dXplatform = X2c - X1;
            float dXnode = TS1Xc - X1;
            float dZplatform = Z2c - Z1;
            float dZnode = TS1Zc - Z1;

            float dplatform = Math.Abs(dXplatform) > Math.Abs(dZplatform) ? dXplatform : dZplatform;
            float dnode = Math.Abs(dXplatform) > Math.Abs(dXplatform) ? dXnode : dZnode;  // use same delta direction!

            // if towards begin : build list of sections from start

            List<int> PlSections1 = new List<int>();
            bool reqSectionFound = false;
            float totalLength1 = 0;
            int direction1 = 0;

            if (Math.Sign(dplatform) == Math.Sign(dnode))
            {
                for (int iXRef = firstNode.TCCrossReference.Count - 1; iXRef >= 0 && !reqSectionFound; iXRef--)
                {
                    int thisIndex = firstNode.TCCrossReference[iXRef].Index;
                    PlSections1.Add(thisIndex);
                    totalLength1 += TrackCircuitList[thisIndex].Length;
                    reqSectionFound = (thisIndex == firstSectionIndex);
                }
                totalLength1 -= thisDetails.TCOffset[1, 0];  // correct for offset
            }
            else
            {
                for (int iXRef = 0; iXRef < firstNode.TCCrossReference.Count && !reqSectionFound; iXRef++)
                {
                    int thisIndex = firstNode.TCCrossReference[iXRef].Index;
                    PlSections1.Add(thisIndex);
                    totalLength1 += TrackCircuitList[thisIndex].Length;
                    reqSectionFound = (thisIndex == firstSectionIndex);
                    direction1 = 1;
                }
                totalLength1 -= thisDetails.TCOffset[0, 1];  // correct for offset
            }

            // determine if 1st platform is towards end or begin of tracknode - use largest delta for check

            dXplatform = X1 - X2c;
            dXnode = TS2Xc - X2c;
            dZplatform = Z1 - Z2c;
            dZnode = TS2Zc - Z2c;

            dplatform = Math.Abs(dXplatform) > Math.Abs(dZplatform) ? dXplatform : dZplatform;
            dnode = Math.Abs(dXplatform) > Math.Abs(dXplatform) ? dXnode : dZnode;  // use same delta direction!

            // if towards begin : build list of sections from start

            List<int> PlSections2 = new List<int>();
            reqSectionFound = false;
            float totalLength2 = 0;
            int direction2 = 0;

            if (Math.Sign(dplatform) == Math.Sign(dnode))
            {
                for (int iXRef = secondNode.TCCrossReference.Count - 1; iXRef >= 0 && !reqSectionFound; iXRef--)
                {
                    int thisIndex = secondNode.TCCrossReference[iXRef].Index;
                    PlSections2.Add(thisIndex);
                    totalLength2 += TrackCircuitList[thisIndex].Length;
                    reqSectionFound = (thisIndex == secondSectionIndex);
                }
                totalLength2 -= (TrackCircuitList[secondSectionIndex].Length - secondPlatform.SData1);
            }
            else
            {
                for (int iXRef = 0; iXRef < secondNode.TCCrossReference.Count && !reqSectionFound; iXRef++)
                {
                    int thisIndex = secondNode.TCCrossReference[iXRef].Index;
                    PlSections2.Add(thisIndex);
                    totalLength2 += TrackCircuitList[thisIndex].Length;
                    reqSectionFound = (thisIndex == secondSectionIndex);
                    direction2 = 1;
                }
                totalLength2 -= secondPlatform.SData1; // correct for offset
            }

            // use largest part

            thisDetails.TCSectionIndex.Clear();

            if (totalLength1 > totalLength2)
            {
                foreach (int thisIndex in PlSections1)
                {
                    thisDetails.TCSectionIndex.Add(thisIndex);
                }

                thisDetails.Length = totalLength1;

                if (direction1 == 0)
                {
                    thisDetails.nodeOffset[0] = 0.0f;
                    thisDetails.nodeOffset[1] = firstPlatform.SData1;
                    thisDetails.TCOffset[0, 0] = TrackCircuitList[PlSections1[PlSections1.Count - 1]].Length - totalLength1;
                    for (int iSection = 0; iSection < PlSections1.Count - 2; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] += TrackCircuitList[PlSections1[iSection]].Length;
                    }
                    thisDetails.TCOffset[0, 1] = 0.0f;
                    thisDetails.TCOffset[1, 0] = TrackCircuitList[PlSections1[0]].Length;
                    thisDetails.TCOffset[1, 1] = firstPlatform.SData1;
                    for (int iSection = 0; iSection < PlSections1.Count - 2; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] -= TrackCircuitList[PlSections1[iSection]].Length;
                    }
                }
                else
                {
                    thisDetails.nodeOffset[0] = firstPlatform.SData1;
                    thisDetails.nodeOffset[1] = thisDetails.nodeOffset[0] + totalLength1;
                    thisDetails.TCOffset[0, 0] = 0.0f;
                    thisDetails.TCOffset[0, 1] = TrackCircuitList[PlSections1[0]].Length - totalLength1;
                    for (int iSection = 1; iSection < PlSections1.Count - 1; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] += TrackCircuitList[PlSections1[iSection]].Length;
                    }
                    thisDetails.TCOffset[1, 0] = totalLength1;
                    for (int iSection = 1; iSection < PlSections1.Count - 1; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] -= TrackCircuitList[PlSections1[iSection]].Length;
                    }
                    thisDetails.TCOffset[1, 1] = TrackCircuitList[PlSections1[PlSections1.Count - 1]].Length;
                }
            }
            else
            {
                foreach (int thisIndex in PlSections2)
                {
                    thisDetails.TCSectionIndex.Add(thisIndex);
                }

                thisDetails.Length = totalLength2;

                if (direction2 == 0)
                {
                    thisDetails.nodeOffset[0] = 0.0f;
                    thisDetails.nodeOffset[1] = secondPlatform.SData1;
                    thisDetails.TCOffset[0, 0] = TrackCircuitList[PlSections2.Count - 1].Length - totalLength2;
                    for (int iSection = 0; iSection < PlSections2.Count - 2; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] += TrackCircuitList[PlSections2[iSection]].Length;
                    }
                    thisDetails.TCOffset[0, 1] = 0.0f;
                    thisDetails.TCOffset[1, 0] = TrackCircuitList[PlSections2[0]].Length;
                    thisDetails.TCOffset[1, 1] = secondPlatform.SData1;
                    for (int iSection = 0; iSection < PlSections2.Count - 2; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] -= TrackCircuitList[PlSections2[iSection]].Length;
                    }
                }
                else
                {
                    thisDetails.nodeOffset[0] = secondPlatform.SData1;
                    thisDetails.nodeOffset[1] = thisDetails.nodeOffset[0] + totalLength2;
                    thisDetails.TCOffset[0, 0] = 0.0f;
                    thisDetails.TCOffset[0, 1] = TrackCircuitList[PlSections2[0]].Length - totalLength2;
                    for (int iSection = 1; iSection < PlSections2.Count - 1; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] += TrackCircuitList[PlSections2[iSection]].Length;
                    }
                    thisDetails.TCOffset[1, 0] = totalLength2;
                    for (int iSection = 1; iSection < PlSections2.Count - 1; iSection++)
                    {
                        thisDetails.TCOffset[0, 0] -= TrackCircuitList[PlSections2[iSection]].Length;
                    }
                    thisDetails.TCOffset[1, 1] = TrackCircuitList[PlSections2[PlSections2.Count - 1]].Length;
                }
            }
        }


        //================================================================================================//
        //
        // Remove all deadlock path references for specified train
        //

        public void RemoveDeadlockPathReferences(int trainnumber)
        {
            foreach (KeyValuePair<int, DeadlockInfo> deadlockElement in DeadlockInfoList)
            {
                DeadlockInfo deadlockInfo = deadlockElement.Value;
                if (deadlockInfo.TrainSubpathIndex.ContainsKey(trainnumber))
                {
                    Dictionary<int, int> subpathRef = deadlockInfo.TrainSubpathIndex[trainnumber];
                    foreach (KeyValuePair<int, int> pathRef in subpathRef)
                    {
                        int routeIndex = pathRef.Value;
                        List<int> pathReferences = deadlockInfo.TrainReferences[routeIndex];
                        foreach (int pathReference in pathReferences)
                        {
                            deadlockInfo.AvailablePathList[pathReference].AllowedTrains.Remove(trainnumber);
                        }
                        deadlockInfo.TrainReferences.Remove(routeIndex);
                        deadlockInfo.TrainOwnPath.Remove(routeIndex);
                        deadlockInfo.TrainLengthFit.Remove(routeIndex);
                    }
                    deadlockInfo.TrainSubpathIndex.Remove(trainnumber);
                }
            }
        }

        //================================================================================================//
        //
        // Remove all deadlock path references for specified train
        //

        public void ReallocateDeadlockPathReferences(int oldnumber, int newnumber)
        {
            foreach (KeyValuePair<int, DeadlockInfo> deadlockElement in DeadlockInfoList)
            {
                DeadlockInfo deadlockInfo = deadlockElement.Value;
                if (deadlockInfo.TrainSubpathIndex.ContainsKey(oldnumber))
                {
                    Dictionary<int, int> subpathRef = deadlockInfo.TrainSubpathIndex[oldnumber];
                    foreach (KeyValuePair<int, int> pathRef in subpathRef)
                    {
                        int routeIndex = pathRef.Value;
                        List<int> pathReferences = deadlockInfo.TrainReferences[routeIndex];
                        foreach (int pathReference in pathReferences)
                        {
                            deadlockInfo.AvailablePathList[pathReference].AllowedTrains.Remove(oldnumber);
                            deadlockInfo.AvailablePathList[pathReference].AllowedTrains.Add(newnumber);
                        }
                    }
                    deadlockInfo.TrainSubpathIndex.Add(newnumber, subpathRef);
                    deadlockInfo.TrainSubpathIndex.Remove(oldnumber);
                }
            }
        }

        //================================================================================================//
        //
        // ProcessTunnels
        // Process tunnel sections and add info to TrackCircuitSections
        //

        public void ProcessTunnels()
        {
            // loop through tracknodes
            foreach (TrackNode thisNode in trackDB.TrackNodes)
            {
                if (thisNode != null && thisNode.TrVectorNode != null)
                {
                    bool inTunnel = false;
                    List<float[]> tunnelInfo = new List<float[]>();
                    List<int> tunnelPaths = new List<int>();
                    float[] lastTunnel = null;
                    float totalLength = 0f;
                    int numPaths = -1;

                    // loop through all sections in node
                    TrVectorNode thisVNode = thisNode.TrVectorNode;
                    foreach (TrVectorSection thisSection in thisVNode.TrVectorSections)
                    {
                        if (!tsectiondat.TrackSections.ContainsKey(thisSection.SectionIndex))
                        {
                            continue;  // missing track section
                        }

                        float thisLength = 0f;
                        Orts.Formats.Msts.TrackSection TS = tsectiondat.TrackSections[thisSection.SectionIndex];

                        // determine length
                        if (TS.SectionCurve != null)
                        {
                            thisLength =
                                    MathHelper.ToRadians(Math.Abs(TS.SectionCurve.Angle)) * TS.SectionCurve.Radius;
                        }
                        else
                        {
                            thisLength = TS.SectionSize.Length;

                        }

                        // check tunnel shape

                        bool tunnelShape = false;
                        int shapePaths = 0;

                        if (tsectiondat.TrackShapes.ContainsKey(thisSection.ShapeIndex))
                        {
                            TrackShape thisShape = tsectiondat.TrackShapes[thisSection.ShapeIndex];
                            tunnelShape = thisShape.TunnelShape;
                            shapePaths = Convert.ToInt32(thisShape.NumPaths);
                        }

                        if (tunnelShape)
                        {
                            numPaths = numPaths < 0 ? shapePaths : Math.Min(numPaths, shapePaths);
                            if (inTunnel)
                            {
                                lastTunnel[1] += thisLength;
                            }
                            else
                            {
                                lastTunnel = new float[2];
                                lastTunnel[0] = totalLength;
                                lastTunnel[1] = thisLength;
                                inTunnel = true;
                            }
                        }
                        else if (inTunnel)
                        {
                            tunnelInfo.Add(lastTunnel);
                            tunnelPaths.Add(numPaths);
                            inTunnel = false;
                            numPaths = -1;
                        }
                        totalLength += thisLength;
                    }

                    // add last tunnel item
                    if (inTunnel)
                    {
                        tunnelInfo.Add(lastTunnel);
                        tunnelPaths.Add(numPaths);
                    }

                    // add tunnel info to TrackCircuitSections

                    if (tunnelInfo.Count > 0)
                    {
                        bool TCSInTunnel = false;
                        float[] tunnelData = tunnelInfo[0];
                        float processedLength = 0;

                        for (int iXRef = thisNode.TCCrossReference.Count - 1; iXRef >= 0; iXRef--)
                        {
                            TrackCircuitSectionXref TCSXRef = thisNode.TCCrossReference[iXRef];
                            // forward direction
                            float TCSStartOffset = TCSXRef.OffsetLength[1];
                            float TCSLength = TCSXRef.Length;
                            TrackCircuitSection thisTCS = TrackCircuitList[TCSXRef.Index];

                            // if tunnel starts in TCS
                            while (tunnelData != null && tunnelData[0] <= (TCSStartOffset + TCSLength))
                            {
                                TrackCircuitSection.tunnelInfoData[] TCSTunnelData = new TrackCircuitSection.tunnelInfoData[2];
                                float tunnelStart = 0;
                                TCSTunnelData[0].numTunnelPaths = tunnelPaths[0];
                                TCSTunnelData[1].numTunnelPaths = tunnelPaths[0];

                                // if in tunnel, set start in tunnel and check end
                                if (TCSInTunnel)
                                {
                                    TCSTunnelData[1].TunnelStart = -1;
                                    TCSTunnelData[1].TCSStartOffset = processedLength;
                                }
                                else
                                // else start new tunnel
                                {
                                    TCSTunnelData[1].TunnelStart = tunnelData[0] - TCSStartOffset;
                                    tunnelStart = TCSTunnelData[1].TunnelStart;
                                    TCSTunnelData[1].TCSStartOffset = -1;
                                }

                                if ((TCSStartOffset + TCSLength) >= (tunnelData[0] + tunnelData[1]))  // tunnel end is in this section
                                {
                                    TCSInTunnel = false;
                                    TCSTunnelData[1].TunnelEnd = tunnelStart + tunnelData[1] - processedLength;

                                    TCSTunnelData[1].LengthInTCS = TCSTunnelData[1].TunnelEnd - tunnelStart;
                                    TCSTunnelData[1].TotalLength = tunnelData[1];

                                    processedLength = 0;

                                    if (thisTCS.TunnelInfo == null) thisTCS.TunnelInfo = new List<TrackCircuitSection.tunnelInfoData[]>();
                                    thisTCS.TunnelInfo.Add(TCSTunnelData);

                                    if (tunnelInfo.Count >= 2)
                                    {
                                        tunnelInfo.RemoveAt(0);
                                        tunnelData = tunnelInfo[0];
                                        tunnelPaths.RemoveAt(0);
                                    }
                                    else
                                    {
                                        tunnelData = null;
                                        break;  // no more tunnels to process
                                    }
                                }
                                else
                                {
                                    TCSInTunnel = true;

                                    TCSTunnelData[1].TunnelEnd = -1;
                                    TCSTunnelData[1].LengthInTCS = TCSLength - tunnelStart;
                                    TCSTunnelData[1].TotalLength = tunnelData[1];

                                    processedLength += (TCSLength - tunnelStart);

                                    if (thisTCS.TunnelInfo == null) thisTCS.TunnelInfo = new List<TrackCircuitSection.tunnelInfoData[]>();
                                    thisTCS.TunnelInfo.Add(TCSTunnelData);
                                    break;  // cannot add more tunnels to section
                                }
                            }
                            // derive tunnel data for other direction
                            if (thisTCS.TunnelInfo != null)
                            {
                                foreach (TrackCircuitSection.tunnelInfoData[] thisTunnelInfo in thisTCS.TunnelInfo)
                                {
                                    thisTunnelInfo[0].TunnelStart = thisTunnelInfo[1].TunnelEnd < 0 ? -1 : thisTCS.Length - thisTunnelInfo[1].TunnelEnd;
                                    thisTunnelInfo[0].TunnelEnd = thisTunnelInfo[1].TunnelStart < 0 ? -1 : thisTCS.Length - thisTunnelInfo[1].TunnelStart;
                                    thisTunnelInfo[0].LengthInTCS = thisTunnelInfo[1].LengthInTCS;
                                    thisTunnelInfo[0].TotalLength = thisTunnelInfo[1].TotalLength;

                                    if (thisTunnelInfo[0].TunnelStart >= 0)
                                    {
                                        thisTunnelInfo[0].TCSStartOffset = -1;
                                    }
                                    else if (thisTunnelInfo[1].TCSStartOffset < 0)
                                    {
                                        thisTunnelInfo[0].TCSStartOffset = thisTunnelInfo[0].TotalLength - thisTunnelInfo[0].LengthInTCS;
                                    }
                                    else
                                    {
                                        thisTunnelInfo[0].TCSStartOffset = thisTunnelInfo[0].TotalLength - thisTunnelInfo[1].TCSStartOffset - thisTCS.Length;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // ProcessTroughs
        // Process trough sections and add info to TrackCircuitSections
        //

        public void ProcessTroughs()
        {
            // loop through tracknodes
            foreach (TrackNode thisNode in trackDB.TrackNodes)
            {
                if (thisNode != null && thisNode.TrVectorNode != null)
                {
                    bool overTrough = false;
                    List<float[]> troughInfo = new List<float[]>();
                    List<int> troughPaths = new List<int>();
                    float[] lastTrough = null;
                    float totalLength = 0f;
                    int numPaths = -1;

                    // loop through all sections in node
                    TrVectorNode thisVNode = thisNode.TrVectorNode;
                    foreach (TrVectorSection thisSection in thisVNode.TrVectorSections)
                    {
                        if (!tsectiondat.TrackSections.ContainsKey(thisSection.SectionIndex))
                        {
                            continue;  // missing track section
                        }

                        float thisLength = 0f;
                        Orts.Formats.Msts.TrackSection TS = tsectiondat.TrackSections[thisSection.SectionIndex];

                        // determine length
                        if (TS.SectionCurve != null)
                        {
                            thisLength =
                                    MathHelper.ToRadians(Math.Abs(TS.SectionCurve.Angle)) * TS.SectionCurve.Radius;
                        }
                        else
                        {
                            thisLength = TS.SectionSize.Length;

                        }

                        // check trough shape

                        bool troughShape = false;
                        int shapePaths = 0;

                        if (tsectiondat.TrackShapes.ContainsKey(thisSection.ShapeIndex))
                        {
                            TrackShape thisShape = tsectiondat.TrackShapes[thisSection.ShapeIndex];
                            troughShape = thisShape.FileName.EndsWith("Wtr.s") || thisShape.FileName.EndsWith("wtr.s");
                            shapePaths = Convert.ToInt32(thisShape.NumPaths);
                        }

                        if (troughShape)
                        {
                            numPaths = numPaths < 0 ? shapePaths : Math.Min(numPaths, shapePaths);
                            if (overTrough)
                            {
                                lastTrough[1] += thisLength;
                            }
                            else
                            {
                                lastTrough = new float[2];
                                lastTrough[0] = totalLength;
                                lastTrough[1] = thisLength;
                                overTrough = true;
                            }
                        }
                        else if (overTrough)
                        {
                            troughInfo.Add(lastTrough);
                            troughPaths.Add(numPaths);
                            overTrough = false;
                            numPaths = -1;
                        }
                        totalLength += thisLength;
                    }

                    // add last tunnel item
                    if (overTrough)
                    {
                        troughInfo.Add(lastTrough);
                        troughPaths.Add(numPaths);
                    }

                    // add tunnel info to TrackCircuitSections

                    if (troughInfo.Count > 0)
                    {
                        bool TCSOverTrough = false;
                        float[] troughData = troughInfo[0];
                        float processedLength = 0;

                        for (int iXRef = thisNode.TCCrossReference.Count - 1; iXRef >= 0; iXRef--)
                        {
                            TrackCircuitSectionXref TCSXRef = thisNode.TCCrossReference[iXRef];
                            // forward direction
                            float TCSStartOffset = TCSXRef.OffsetLength[1];
                            float TCSLength = TCSXRef.Length;
                            TrackCircuitSection thisTCS = TrackCircuitList[TCSXRef.Index];

                            // if trough starts in TCS
                            while (troughData != null && troughData[0] <= (TCSStartOffset + TCSLength))
                            {
                                TrackCircuitSection.troughInfoData[] TCSTroughData = new TrackCircuitSection.troughInfoData[2];
                                float troughStart = 0;

                                // if in trough, set start in trough and check end
                                if (TCSOverTrough)
                                {
                                    TCSTroughData[1].TroughStart = -1;
                                    TCSTroughData[1].TCSStartOffset = processedLength;
                                }
                                else
                                // else start new trough
                                {
                                    TCSTroughData[1].TroughStart = troughData[0] - TCSStartOffset;
                                    troughStart = TCSTroughData[1].TroughStart;
                                    TCSTroughData[1].TCSStartOffset = -1;
                                }

                                if ((TCSStartOffset + TCSLength) >= (troughData[0] + troughData[1]))  // trough end is in this section
                                {
                                    TCSOverTrough = false;
                                    TCSTroughData[1].TroughEnd = troughStart + troughData[1] - processedLength;

                                    TCSTroughData[1].LengthInTCS = TCSTroughData[1].TroughEnd - troughStart;
                                    TCSTroughData[1].TotalLength = troughData[1];

                                    processedLength = 0;

                                    if (thisTCS.TroughInfo == null) thisTCS.TroughInfo = new List<TrackCircuitSection.troughInfoData[]>();
                                    thisTCS.TroughInfo.Add(TCSTroughData);

                                    if (troughInfo.Count >= 2)
                                    {
                                        troughInfo.RemoveAt(0);
                                        troughData = troughInfo[0];
                                        troughPaths.RemoveAt(0);
                                    }
                                    else
                                    {
                                        troughData = null;
                                        break;  // no more troughs to process
                                    }
                                }
                                else
                                {
                                    TCSOverTrough = true;

                                    TCSTroughData[1].TroughEnd = -1;
                                    TCSTroughData[1].LengthInTCS = TCSLength - troughStart;
                                    TCSTroughData[1].TotalLength = troughData[1];

                                    processedLength += (TCSLength - troughStart);

                                    if (thisTCS.TroughInfo == null) thisTCS.TroughInfo = new List<TrackCircuitSection.troughInfoData[]>();
                                    thisTCS.TroughInfo.Add(TCSTroughData);
                                    break;  // cannot add more troughs to section
                                }
                            }
                            // derive trough data for other direction
                            if (thisTCS.TroughInfo != null)
                            {
                                foreach (TrackCircuitSection.troughInfoData[] thisTroughInfo in thisTCS.TroughInfo)
                                {
                                    thisTroughInfo[0].TroughStart = thisTroughInfo[1].TroughEnd < 0 ? -1 : thisTCS.Length - thisTroughInfo[1].TroughEnd;
                                    thisTroughInfo[0].TroughEnd = thisTroughInfo[1].TroughStart < 0 ? -1 : thisTCS.Length - thisTroughInfo[1].TroughStart;
                                    thisTroughInfo[0].LengthInTCS = thisTroughInfo[1].LengthInTCS;
                                    thisTroughInfo[0].TotalLength = thisTroughInfo[1].TotalLength;

                                    if (thisTroughInfo[0].TroughStart >= 0)
                                    {
                                        thisTroughInfo[0].TCSStartOffset = -1;
                                    }
                                    else if (thisTroughInfo[1].TCSStartOffset < 0)
                                    {
                                        thisTroughInfo[0].TCSStartOffset = thisTroughInfo[0].TotalLength - thisTroughInfo[0].LengthInTCS;
                                    }
                                    else
                                    {
                                        thisTroughInfo[0].TCSStartOffset = thisTroughInfo[0].TotalLength - thisTroughInfo[1].TCSStartOffset - thisTCS.Length;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Find Train
        // Find train in list using number, to restore reference after restore
        //

        public static Train FindTrain(int number, List<Train> trains)
        {
            foreach (Train thisTrain in trains)
            {
                if (thisTrain.Number == number)
                {
                    return (thisTrain);
                }
            }

            return (null);
        }

        //================================================================================================//
        //
        // Request set switch
        // Manual request to set switch, either from train or direct from node
        //

        public static bool RequestSetSwitch(Train thisTrain, Direction direction)
        {
            if (thisTrain.ControlMode == Train.TRAIN_CONTROL.MANUAL)
            {
                return (thisTrain.ProcessRequestManualSetSwitch(direction));
            }
            else if (thisTrain.ControlMode == Train.TRAIN_CONTROL.EXPLORER)
            {
                return (thisTrain.ProcessRequestExplorerSetSwitch(direction));
            }
            return (false);
        }

        public bool RequestSetSwitch(TrackNode switchNode)
        {
            return RequestSetSwitch(switchNode.TCCrossReference[0].Index);
        }

        public bool RequestSetSwitch(int trackCircuitIndex)
        {
            TrackCircuitSection switchSection = TrackCircuitList[trackCircuitIndex];
            Train thisTrain = switchSection.CircuitState.TrainReserved == null ? null : switchSection.CircuitState.TrainReserved.Train;
            bool switchReserved = (switchSection.CircuitState.SignalReserved >= 0 || switchSection.CircuitState.TrainClaimed.Count > 0);
            bool switchSet = false;

            // set physical state

            if (switchReserved)
            {
                switchSet = false;
            }

            else if (!switchSection.CircuitState.HasTrainsOccupying() && thisTrain == null)
            {
                switchSection.JunctionSetManual = switchSection.JunctionLastRoute == 0 ? 1 : 0;
                setSwitch(switchSection.OriginalIndex, switchSection.JunctionSetManual, switchSection);
                switchSet = true;
            }

            // if switch reserved by manual train then notify train

            else if (thisTrain != null && thisTrain.ControlMode == Train.TRAIN_CONTROL.MANUAL)
            {
                switchSection.JunctionSetManual = switchSection.JunctionLastRoute == 0 ? 1 : 0;
                switchSet = thisTrain.ProcessRequestManualSetSwitch(switchSection.Index);
            }
            else if (thisTrain != null && thisTrain.ControlMode == Train.TRAIN_CONTROL.EXPLORER)
            {
                switchSection.JunctionSetManual = switchSection.JunctionLastRoute == 0 ? 1 : 0;
                switchSet = thisTrain.ProcessRequestExplorerSetSwitch(switchSection.Index);
            }

            return (switchSet);
        }

        //only used by MP to manually set a switch to a desired position
        public bool RequestSetSwitch(TrackNode switchNode, int desiredState)
        {
            TrackCircuitSection switchSection = TrackCircuitList[switchNode.TCCrossReference[0].Index];
            bool switchReserved = (switchSection.CircuitState.SignalReserved >= 0 || switchSection.CircuitState.TrainClaimed.Count > 0);
            bool switchSet = false;

            if (trackDB.TrackNodes[switchSection.OriginalIndex].TrJunctionNode.SelectedRoute == desiredState) return (false);
            // set physical state

            if (!MPManager.IsServer()) if (switchReserved) return (false);
            //this should not be enforced in MP, as a train may need to be allowed to go out of the station from the side line

            if (!switchSection.CircuitState.HasTrainsOccupying())
            {
                switchSection.JunctionSetManual = desiredState;
                trackDB.TrackNodes[switchSection.OriginalIndex].TrJunctionNode.SelectedRoute = switchSection.JunctionSetManual;
                switchSection.JunctionLastRoute = switchSection.JunctionSetManual;
                switchSet = true;
                /*if (switchSection.SignalsPassingRoutes != null)
                {
                    foreach (var thisSignalIndex in switchSection.SignalsPassingRoutes)
                    {
                        var signal = switchSection.signalRef.SignalObjects[thisSignalIndex];
                        if (signal != null) signal.ResetRoute(switchSection.Index);
                    }
                    switchSection.SignalsPassingRoutes.Clear();
                }*/
                var temptrains = Simulator.Trains.ToArray();

                foreach (var t in temptrains)
                {
                    try
                    {
                        t.ProcessRequestExplorerSetSwitch(switchSection.Index);
                    }
                    catch { }
                }
            }
            return (switchSet);
        }

        //================================================================================================//

    }// class Signals

    //================================================================================================//
    //
    // class TrackCircuitSection
    //
    //================================================================================================//
    //
    // Class for track circuit and train control
    //

    public class TrackCircuitSection
    {
        public enum TrackCircuitType
        {
            Normal,
            Junction,
            Crossover,
            EndOfTrack,
            Empty,
        }

        // Properties Index, Length and OffsetLength come from TrackCircuitSectionXref

        public int Index;                                         // Index of TCS                           //
        public float Length;                                      // Length of Section                      //
        public float[] OffsetLength = new float[2];               // Offset length in original tracknode    //
        public Signals signalRef;                                 // reference to Signals class             //
        public int OriginalIndex;                                 // original TDB section index             //
        public TrackCircuitType CircuitType;                      // type of section                        //

        public TrPin[,] Pins = new TrPin[2, 2];                   // next sections                          //
        public TrPin[,] ActivePins = new TrPin[2, 2];             // active next sections                   //
        public bool[] EndIsTrailingJunction = new bool[2];        // next section is trailing jn            //

        public int JunctionDefaultRoute = -1;                     // jn default route, value is out-pin      //
        public int JunctionLastRoute = -1;                        // jn last route, value is out-pin         //
        public int JunctionSetManual = -1;                        // jn set manual, value is out-pin         //
        public bool AILock;                                       // jn is locked agains AI trains           //
        public List<int> SignalsPassingRoutes;                    // list of signals reading passed junction //

        public SignalObject[] EndSignals = new SignalObject[2];   // signals at either end      //

        public double Overlap;                                    // overlap for junction nodes //
        public List<int> PlatformIndex = new List<int>();         // platforms along section    //

        public TrackCircuitItems CircuitItems;                    // all items                  //
        public TrackCircuitState CircuitState;                    // normal states              //

        // old style deadlock definitions
        public Dictionary<int, List<int>> DeadlockTraps;          // deadlock traps             //
        public List<int> DeadlockActives;                         // list of trains with active deadlock traps //
        public List<int> DeadlockAwaited;                         // train is waiting for deadlock to clear //

        // new style deadlock definitions
        public int DeadlockReference;                             // index of deadlock to related deadlockinfo object for boundary //
        public Dictionary<int, int> DeadlockBoundaries;           // list of boundaries and path index to boundary for within deadlock //

        // tunnel data
        public struct tunnelInfoData
        {
            public float TunnelStart;                             // start position of tunnel : -1 if start is in tunnel
            public float TunnelEnd;                               // end position of tunnel : -1 if end is in tunnel
            public float LengthInTCS;                             // length of tunnel within this TCS
            public float TotalLength;                             // total length of tunnel
            public float TCSStartOffset;                          // offset in tunnel of start of this TCS : -1 if tunnel start in this TCS
            public int numTunnelPaths;                            // number of paths through tunnel
        }

        public List<tunnelInfoData[]> TunnelInfo = null;          // full tunnel info data

        // trough data

        public struct troughInfoData
        {
            public float TroughStart;                             // start position of trough : -1 if start is in trough
            public float TroughEnd;                               // end position of trough : -1 if end is in trough
            public float LengthInTCS;                             // length of trough within this TCS
            public float TotalLength;                             // total length of trough
            public float TCSStartOffset;                          // offset in trough of start of this TCS : -1 if trough start in this TCS
        }

        public List<troughInfoData[]> TroughInfo = null;          // full trough info data

        //================================================================================================//
        //
        // Constructor
        //


        public TrackCircuitSection(TrackNode thisNode, int orgINode,
                        TrackSectionsFile tsectiondat, Signals thisSignals)
        {

            //
            // Copy general info
            //

            signalRef = thisSignals;

            Index = orgINode;
            OriginalIndex = orgINode;

            if (thisNode.TrEndNode)
            {
                CircuitType = TrackCircuitType.EndOfTrack;
            }
            else if (thisNode.TrJunctionNode != null)
            {
                CircuitType = TrackCircuitType.Junction;
            }
            else
            {
                CircuitType = TrackCircuitType.Normal;
            }


            //
            // Preset pins, then copy pin info
            //

            for (int direction = 0; direction < 2; direction++)
            {
                for (int pin = 0; pin < 2; pin++)
                {
                    Pins[direction, pin] = new TrPin() { Direction = -1, Link = -1 };
                    ActivePins[direction, pin] = new TrPin() { Direction = -1, Link = -1 };
                }
            }

            int PinNo = 0;
            for (int pin = 0; pin < Math.Min(thisNode.Inpins, Pins.GetLength(1)); pin++)
            {
                Pins[0, pin] = thisNode.TrPins[PinNo].Copy();
                PinNo++;
            }
            if (PinNo < thisNode.Inpins) PinNo = (int)thisNode.Inpins;
            for (int pin = 0; pin < Math.Min(thisNode.Outpins, Pins.GetLength(1)); pin++)
            {
                Pins[1, pin] = thisNode.TrPins[PinNo].Copy();
                PinNo++;
            }


            //
            // preset no end signals
            // preset no trailing junction
            //

            for (int direction = 0; direction < 2; direction++)
            {
                EndSignals[direction] = null;
                EndIsTrailingJunction[direction] = false;
            }

            //
            // Preset length and offset
            // If section index not in tsectiondat, set length to 0.
            //

            float totalLength = 0.0f;

            if (thisNode.TrVectorNode != null && thisNode.TrVectorNode.TrVectorSections != null)
            {
                foreach (TrVectorSection thisSection in thisNode.TrVectorNode.TrVectorSections)
                {
                    float thisLength = 0.0f;

                    if (tsectiondat.TrackSections.ContainsKey(thisSection.SectionIndex))
                    {
                        Orts.Formats.Msts.TrackSection TS = tsectiondat.TrackSections[thisSection.SectionIndex];

                        if (TS.SectionCurve != null)
                        {
                            thisLength =
                                    MathHelper.ToRadians(Math.Abs(TS.SectionCurve.Angle)) *
                                    TS.SectionCurve.Radius;
                        }
                        else
                        {
                            thisLength = TS.SectionSize.Length;

                        }
                    }

                    totalLength += thisLength;
                }
            }

            Length = totalLength;

            for (int direction = 0; direction < 2; direction++)
            {
                OffsetLength[direction] = 0;
            }

            //
            // set signal list for junctions
            //

            if (CircuitType == TrackCircuitType.Junction)
            {
                SignalsPassingRoutes = new List<int>();
            }
            else
            {
                SignalsPassingRoutes = null;
            }

            // for Junction nodes, obtain default route
            // set switch to default route
            // copy overlap (if set)

            if (CircuitType == TrackCircuitType.Junction)
            {
                uint trackShapeIndex = thisNode.TrJunctionNode.ShapeIndex;
                try
                {
                    TrackShape trackShape = tsectiondat.TrackShapes[trackShapeIndex];
                    JunctionDefaultRoute = (int)trackShape.MainRoute;

                    Overlap = trackShape.ClearanceDistance;
                }
                catch (Exception)
                {
                    Trace.TraceWarning("Missing TrackShape in tsection.dat : " + trackShapeIndex);
                    JunctionDefaultRoute = 0;
                    Overlap = 0;
                }

                JunctionLastRoute = JunctionDefaultRoute;
                signalRef.setSwitch(OriginalIndex, JunctionLastRoute, this);
            }

            //
            // Create circuit items
            //

            CircuitItems = new TrackCircuitItems();
            CircuitState = new TrackCircuitState();

            DeadlockTraps = new Dictionary<int, List<int>>();
            DeadlockActives = new List<int>();
            DeadlockAwaited = new List<int>();

            DeadlockReference = -1;
            DeadlockBoundaries = null;
        }

        //================================================================================================//
        //
        // Constructor for empty entries
        //

        public TrackCircuitSection(int INode, Signals thisSignals)
        {

            signalRef = thisSignals;

            Index = INode;
            OriginalIndex = -1;
            CircuitType = TrackCircuitType.Empty;

            for (int iDir = 0; iDir < 2; iDir++)
            {
                EndIsTrailingJunction[iDir] = false;
                EndSignals[iDir] = null;
                OffsetLength[iDir] = 0;
            }

            for (int iDir = 0; iDir < 2; iDir++)
            {
                for (int pin = 0; pin < 2; pin++)
                {
                    Pins[iDir, pin] = new TrPin() { Direction = -1, Link = -1 };
                    ActivePins[iDir, pin] = new TrPin() { Direction = -1, Link = -1 };
                }
            }

            CircuitItems = new TrackCircuitItems();
            CircuitState = new TrackCircuitState();

            DeadlockTraps = new Dictionary<int, List<int>>();
            DeadlockActives = new List<int>();
            DeadlockAwaited = new List<int>();

            DeadlockReference = -1;
            DeadlockBoundaries = null;
        }

        //================================================================================================//
        //
        // Restore
        //

        public void Restore(Simulator simulator, BinaryReader inf)
        {
            ActivePins[0, 0].Link = inf.ReadInt32();
            ActivePins[0, 0].Direction = inf.ReadInt32();
            ActivePins[1, 0].Link = inf.ReadInt32();
            ActivePins[1, 0].Direction = inf.ReadInt32();
            ActivePins[0, 1].Link = inf.ReadInt32();
            ActivePins[0, 1].Direction = inf.ReadInt32();
            ActivePins[1, 1].Link = inf.ReadInt32();
            ActivePins[1, 1].Direction = inf.ReadInt32();

            JunctionSetManual = inf.ReadInt32();
            JunctionLastRoute = inf.ReadInt32();
            AILock = inf.ReadBoolean();

            CircuitState.Restore(simulator, inf);

            // if physical junction, throw switch

            if (CircuitType == TrackCircuitType.Junction)
            {
                signalRef.setSwitch(OriginalIndex, JunctionLastRoute, this);
            }

            int deadlockTrapsCount = inf.ReadInt32();
            for (int iDeadlock = 0; iDeadlock < deadlockTrapsCount; iDeadlock++)
            {
                int deadlockKey = inf.ReadInt32();
                int deadlockListCount = inf.ReadInt32();
                List<int> deadlockList = new List<int>();

                for (int iDeadlockInfo = 0; iDeadlockInfo < deadlockListCount; iDeadlockInfo++)
                {
                    int deadlockDetail = inf.ReadInt32();
                    deadlockList.Add(deadlockDetail);
                }
                DeadlockTraps.Add(deadlockKey, deadlockList);
            }

            int deadlockActivesCount = inf.ReadInt32();
            for (int iDeadlockActive = 0; iDeadlockActive < deadlockActivesCount; iDeadlockActive++)
            {
                int deadlockActiveDetails = inf.ReadInt32();
                DeadlockActives.Add(deadlockActiveDetails);
            }

            int deadlockWaitCount = inf.ReadInt32();
            for (int iDeadlockWait = 0; iDeadlockWait < deadlockWaitCount; iDeadlockWait++)
            {
                int deadlockWaitDetails = inf.ReadInt32();
                DeadlockAwaited.Add(deadlockWaitDetails);
            }

            DeadlockReference = inf.ReadInt32();

            DeadlockBoundaries = null;
            int deadlockBoundariesAvailable = inf.ReadInt32();
            if (deadlockBoundariesAvailable > 0)
            {
                DeadlockBoundaries = new Dictionary<int, int>();
                for (int iInfo = 0; iInfo <= deadlockBoundariesAvailable - 1; iInfo++)
                {
                    int boundaryInfo = inf.ReadInt32();
                    int pathInfo = inf.ReadInt32();
                    DeadlockBoundaries.Add(boundaryInfo, pathInfo);
                }
            }
        }

        //================================================================================================//
        //
        // Save
        //

        public void Save(BinaryWriter outf)
        {
            outf.Write(ActivePins[0, 0].Link);
            outf.Write(ActivePins[0, 0].Direction);
            outf.Write(ActivePins[1, 0].Link);
            outf.Write(ActivePins[1, 0].Direction);
            outf.Write(ActivePins[0, 1].Link);
            outf.Write(ActivePins[0, 1].Direction);
            outf.Write(ActivePins[1, 1].Link);
            outf.Write(ActivePins[1, 1].Direction);

            outf.Write(JunctionSetManual);
            outf.Write(JunctionLastRoute);
            outf.Write(AILock);

            CircuitState.Save(outf);

            outf.Write(DeadlockTraps.Count);
            foreach (KeyValuePair<int, List<int>> thisTrap in DeadlockTraps)
            {
                outf.Write(thisTrap.Key);
                outf.Write(thisTrap.Value.Count);

                foreach (int thisDeadlockRef in thisTrap.Value)
                {
                    outf.Write(thisDeadlockRef);
                }
            }

            outf.Write(DeadlockActives.Count);
            foreach (int thisDeadlockActive in DeadlockActives)
            {
                outf.Write(thisDeadlockActive);
            }

            outf.Write(DeadlockAwaited.Count);
            foreach (int thisDeadlockWait in DeadlockAwaited)
            {
                outf.Write(thisDeadlockWait);
            }

            outf.Write(DeadlockReference);

            if (DeadlockBoundaries == null)
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(DeadlockBoundaries.Count);
                foreach (KeyValuePair<int, int> thisInfo in DeadlockBoundaries)
                {
                    outf.Write(thisInfo.Key);
                    outf.Write(thisInfo.Value);
                }
            }
        }

        //================================================================================================//
        //
        // Copy basic info only
        //

        public TrackCircuitSection CopyBasic(int INode)
        {
            TrackCircuitSection newSection = new TrackCircuitSection(INode, this.signalRef);

            newSection.OriginalIndex = this.OriginalIndex;
            newSection.CircuitType = this.CircuitType;

            newSection.EndSignals[0] = this.EndSignals[0];
            newSection.EndSignals[1] = this.EndSignals[1];

            newSection.Length = this.Length;

            Array.Copy(this.OffsetLength, newSection.OffsetLength, this.OffsetLength.Length);

            return (newSection);
        }

        //================================================================================================//
        //
        // Check if set for train
        //

        public bool IsSet(Train.TrainRouted thisTrain, bool claim_is_valid)   // using routed train
        {

            // if train in this section, return true; if other train in this section, return false

            if (CircuitState.ThisTrainOccupying(thisTrain))
            {
                return (true);
            }

            // check reservation

            if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train == thisTrain.Train)
            {
                return (true);
            }

            // check claim if claim is valid as state

            if (CircuitState.TrainClaimed.Count > 0 && claim_is_valid)
            {
                return (CircuitState.TrainClaimed.PeekTrain() == thisTrain.Train);
            }

            // section is not yet set for this train

            return (false);
        }

        public bool IsSet(Train thisTrain, bool claim_is_valid)    // using unrouted train
        {
            if (IsSet(thisTrain.routedForward, claim_is_valid))
            {
                return (true);
            }
            else
            {
                return (IsSet(thisTrain.routedBackward, claim_is_valid));
            }
        }

        //================================================================================================//
        //
        // Check available state for train
        //

        public bool IsAvailable(Train.TrainRouted thisTrain)    // using routed train
        {

            // if train in this section, return true; if other train in this section, return false
            // check if train is in section in expected direction - otherwise return false

            if (CircuitState.ThisTrainOccupying(thisTrain))
            {
                return (true);
            }

            if (CircuitState.HasOtherTrainsOccupying(thisTrain))
            {
                return (false);
            }

            // check reservation

            if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train == thisTrain.Train)
            {
                return (true);
            }

            if (!signalRef.Simulator.TimetableMode && thisTrain.Train.TrainType == Train.TRAINTYPE.AI_NOTSTARTED)
            {
                if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train != thisTrain.Train)
                {
                    ClearSectionsOfTrainBehind(CircuitState.TrainReserved, this);
                }
            }
            else if (!signalRef.Simulator.TimetableMode &&
                thisTrain.Train.IsPlayerDriven && thisTrain.Train.ControlMode != Train.TRAIN_CONTROL.MANUAL && thisTrain.Train.DistanceTravelledM == 0.0 &&
                thisTrain.Train.TCRoute != null && thisTrain.Train.ValidRoute[0] != null && thisTrain.Train.TCRoute.activeSubpath == 0) // We are at initial placement
            // Check if section is under train, and therefore can be unreserved from other trains
            {
                int thisRouteIndex = thisTrain.Train.ValidRoute[0].GetRouteIndex(Index, 0);
                if ((thisRouteIndex <= thisTrain.Train.PresentPosition[0].RouteListIndex && Index >= thisTrain.Train.PresentPosition[1].RouteListIndex) ||
                    (thisRouteIndex >= thisTrain.Train.PresentPosition[0].RouteListIndex && Index <= thisTrain.Train.PresentPosition[1].RouteListIndex))
                {
                    if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train != thisTrain.Train)
                    {
                        Train.TrainRouted trainRouted = CircuitState.TrainReserved;
                        ClearSectionsOfTrainBehind(trainRouted, this);
                        if (trainRouted.Train.TrainType == Train.TRAINTYPE.AI || trainRouted.Train.TrainType == Train.TRAINTYPE.AI_PLAYERHOSTING)
                            ((AITrain)trainRouted.Train).ResetActions(true);
                    }
                }
            }
            else if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train != thisTrain.Train)
            {
                return (false);
            }

            // check signal reservation

            if (CircuitState.SignalReserved >= 0)
            {
                return (false);
            }

            // check claim

            if (CircuitState.TrainClaimed.Count > 0)
            {
                return (CircuitState.TrainClaimed.PeekTrain() == thisTrain.Train);
            }

            // check deadlock trap

            if (DeadlockTraps.ContainsKey(thisTrain.Train.Number))
            {
                if (!DeadlockAwaited.Contains(thisTrain.Train.Number))
                    DeadlockAwaited.Add(thisTrain.Train.Number); // train is waiting for deadlock to clear
                return (false);
            }
            // check deadlock is in use - only if train has valid route

            if (thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex] != null)
            {

                int routeElementIndex = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex].GetRouteIndex(Index, 0);
                if (routeElementIndex >= 0)
                {
                    Train.TCRouteElement thisElement = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][routeElementIndex];

                    // check for deadlock awaited at end of passing loop - path based deadlock processing
                    if (!signalRef.UseLocationPassingPaths)
                    {
                        // if deadlock is allready awaited set available to false to keep one track open
                        if (thisElement.StartAlternativePath != null)
                        {
                            TrackCircuitSection endSection = signalRef.TrackCircuitList[thisElement.StartAlternativePath[1]];
                            if (endSection.CheckDeadlockAwaited(thisTrain.Train.Number))
                            {
                                return (false);
                            }
                        }
                    }

                    // check on available paths through deadlock area - location based deadlock processing
                    else
                    {
                        if (DeadlockReference >= 0 && thisElement.FacingPoint)
                        {
#if DEBUG_DEADLOCK
                            File.AppendAllText(@"C:\Temp\deadlock.txt",
                                "\n **** Check IfAvailable for section " + Index.ToString() + " for train : " + thisTrain.Train.Number.ToString() + "\n");
#endif
                            DeadlockInfo sectionDeadlockInfo = signalRef.DeadlockInfoList[DeadlockReference];
                            List<int> pathAvail = sectionDeadlockInfo.CheckDeadlockPathAvailability(this, thisTrain.Train);
#if DEBUG_DEADLOCK
                            File.AppendAllText(@"C:\Temp\deadlock.txt", "\nReturned no. of available paths : " + pathAvail.Count.ToString() + "\n");
                            File.AppendAllText(@"C:\Temp\deadlock.txt", "****\n\n");
#endif
                            if (pathAvail.Count <= 0) return (false);
                        }
                    }
                }
            }

            // section is clear

            return (true);
        }

        public bool IsAvailable(Train thisTrain)    // using unrouted train
        {
            if (IsAvailable(thisTrain.routedForward))
            {
                return (true);
            }
            else
            {
                return (IsAvailable(thisTrain.routedBackward));
            }
        }

        //================================================================================================//
        //
        // Reserve : set reserve state
        //

        public void Reserve(Train.TrainRouted thisTrain, Train.TCSubpathRoute thisRoute)
        {

#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Reserve section {0} for train {1}\n",
				this.Index,
				thisTrain.Train.Number));
#endif
#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\temp\deadlock.txt",
                String.Format("Reserve section {0} for train {1}\n",
                this.Index,
                thisTrain.Train.Number));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Reserve section {0} for train {1}\n",
                    this.Index,
                    thisTrain.Train.Number));
            }

            Train.TCRouteElement thisElement;

            if (!CircuitState.ThisTrainOccupying(thisTrain.Train))
            {
                CircuitState.TrainReserved = thisTrain;
            }

            // remove from claim or deadlock claim

            if (CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {
                CircuitState.TrainClaimed = removeFromQueue(CircuitState.TrainClaimed, thisTrain);
            }

            // get element in routepath to find required alignment

            int thisIndex = -1;

            for (int iElement = 0; iElement < thisRoute.Count && thisIndex < 0; iElement++)
            {
                thisElement = thisRoute[iElement];
                if (thisElement.TCSectionIndex == Index)
                {
                    thisIndex = iElement;
                }
            }

            // if junction or crossover, align pins
            // also reset manual set (path will have followed setting)

            if (CircuitType == TrackCircuitType.Junction || CircuitType == TrackCircuitType.Crossover)
            {
                // set active pins for leading section

                JunctionSetManual = -1;  // reset manual setting (will have been honoured in route definition if applicable)

                int leadSectionIndex = -1;
                if (thisIndex > 0)
                {
                    thisElement = thisRoute[thisIndex - 1];
                    leadSectionIndex = thisElement.TCSectionIndex;

                    alignSwitchPins(leadSectionIndex);
                }

                // set active pins for trailing section

                int trailSectionIndex = -1;
                if (thisIndex <= thisRoute.Count - 2)
                {
                    thisElement = thisRoute[thisIndex + 1];
                    trailSectionIndex = thisElement.TCSectionIndex;

                    alignSwitchPins(trailSectionIndex);
                }

                // reset signals which routed through this junction

                foreach (int thisSignalIndex in SignalsPassingRoutes)
                {
                    SignalObject thisSignal = signalRef.SignalObjects[thisSignalIndex];
                    thisSignal.ResetRoute(Index);
                }
                SignalsPassingRoutes.Clear();
            }

            // enable all signals along section in direction of train
            // do not enable those signals who are part of NORMAL signal

            if (thisIndex < 0) return; //Added by JTang
            thisElement = thisRoute[thisIndex];
            int direction = thisElement.Direction;

            for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
            {
                TrackCircuitSignalList thisSignalList = CircuitItems.TrackCircuitSignals[direction, fntype];
                foreach (TrackCircuitSignalItem thisItem in thisSignalList.TrackCircuitItem)
                {
                    SignalObject thisSignal = thisItem.SignalRef;
                    if (!thisSignal.isSignalNormal())
                    {
                        thisSignal.enabledTrain = thisTrain;
                    }
                }
            }

            // set deadlock trap if required

            if (thisTrain.Train.DeadlockInfo.ContainsKey(Index))
            {
                SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[Index]);
            }

            // if start of alternative route, set deadlock keys for other end
            // check using path based deadlock processing

            if (!signalRef.UseLocationPassingPaths)
            {
                if (thisElement != null && thisElement.StartAlternativePath != null)
                {
                    TrackCircuitSection endSection = signalRef.TrackCircuitList[thisElement.StartAlternativePath[1]];

                    // no deadlock yet active
                    if (thisTrain.Train.DeadlockInfo.ContainsKey(endSection.Index))
                    {
                        endSection.SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[endSection.Index]);
                    }
                    else if (endSection.DeadlockTraps.ContainsKey(thisTrain.Train.Number) && !endSection.DeadlockAwaited.Contains(thisTrain.Train.Number))
                    {
                        endSection.DeadlockAwaited.Add(thisTrain.Train.Number);
                    }
                }
            }
            // search for path using location based deadlock processing

            else
            {
                if (thisElement != null && thisElement.FacingPoint && DeadlockReference >= 0)
                {
                    DeadlockInfo sectionDeadlockInfo = signalRef.DeadlockInfoList[DeadlockReference];
                    if (sectionDeadlockInfo.HasTrainAndSubpathIndex(thisTrain.Train.Number, thisTrain.Train.TCRoute.activeSubpath))
                    {
                        int trainAndSubpathIndex = sectionDeadlockInfo.GetTrainAndSubpathIndex(thisTrain.Train.Number, thisTrain.Train.TCRoute.activeSubpath);
                        int availableRoute = sectionDeadlockInfo.TrainReferences[trainAndSubpathIndex][0];
                        int endSectionIndex = sectionDeadlockInfo.AvailablePathList[availableRoute].EndSectionIndex;
                        TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];

                        // no deadlock yet active - do not set deadlock if train has wait within deadlock section
                        if (thisTrain.Train.DeadlockInfo.ContainsKey(endSection.Index))
                        {
                            if (!signalRef.Simulator.TimetableMode || !thisTrain.Train.HasActiveWait(Index, endSection.Index))
                            {
                                endSection.SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[endSection.Index]);
                            }
                        }
                        else if (endSection.DeadlockTraps.ContainsKey(thisTrain.Train.Number) && !endSection.DeadlockAwaited.Contains(thisTrain.Train.Number))
                        {
                            endSection.DeadlockAwaited.Add(thisTrain.Train.Number);
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // insert Claim
        //

        public void Claim(Train.TrainRouted thisTrain)
        {
            if (!CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {

#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Claim section {0} for train {1}\n",
				this.Index,
				thisTrain.Train.Number));
#endif
#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\temp\deadlock.txt",
                    String.Format("Claim section {0} for train {1}\n",
                    this.Index,
                    thisTrain.Train.Number));
#endif
                if (thisTrain.Train.CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        String.Format("Claim section {0} for train {1}\n",
                        this.Index,
                        thisTrain.Train.Number));
                }

                CircuitState.TrainClaimed.Enqueue(thisTrain);
            }

            // set deadlock trap if required

            if (thisTrain.Train.DeadlockInfo.ContainsKey(Index))
            {
                SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[Index]);
            }
        }

        //================================================================================================//
        //
        // insert pre-reserve
        //

        public void PreReserve(Train.TrainRouted thisTrain)
        {
            if (!CircuitState.TrainPreReserved.ContainsTrain(thisTrain))
            {
                CircuitState.TrainPreReserved.Enqueue(thisTrain);
            }
        }

        //================================================================================================//
        //
        // set track occupied
        //

        public void SetOccupied(Train.TrainRouted thisTrain)
        {
            SetOccupied(thisTrain, Convert.ToInt32(thisTrain.Train.DistanceTravelledM));
        }


        public void SetOccupied(Train.TrainRouted thisTrain, int reqDistanceTravelledM)
        {
#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Occupy section {0} for train {1}\n",
				this.Index,
				thisTrain.Train.Number));
#endif
#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\temp\deadlock.txt",
                String.Format("Occupy section {0} for train {1}\n",
                this.Index,
                thisTrain.Train.Number));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Occupy section {0} for train {1}\n",
                    this.Index,
                    thisTrain.Train.Number));
            }

            int routeIndex = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex].GetRouteIndex(Index, thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex == 0 ? 1 : 0].RouteListIndex);
            int direction = routeIndex < 0 ? 0 : thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][routeIndex].Direction;
            CircuitState.TrainOccupy.Add(thisTrain, direction);
            thisTrain.Train.OccupiedTrack.Add(this);

            // clear all reservations
            CircuitState.TrainReserved = null;
            CircuitState.SignalReserved = -1;

            if (CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {
                CircuitState.TrainClaimed = removeFromQueue(CircuitState.TrainClaimed, thisTrain);
            }

            if (CircuitState.TrainPreReserved.ContainsTrain(thisTrain))
            {
                CircuitState.TrainPreReserved = removeFromQueue(CircuitState.TrainPreReserved, thisTrain);
            }

            float distanceToClear = reqDistanceTravelledM + Length + thisTrain.Train.standardOverlapM;

            // add to clear list of train

            if (CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
            {
                if (Pins[direction, 1].Link >= 0)  // facing point
                {
                    if (Overlap > 0)
                    {
                        distanceToClear = reqDistanceTravelledM + Length + Convert.ToSingle(Overlap) + thisTrain.Train.standardOverlapM;
                    }
                    else
                    {
                        distanceToClear = reqDistanceTravelledM + Length + thisTrain.Train.junctionOverlapM;
                    }
                }
                else
                {
                    distanceToClear = reqDistanceTravelledM + Length + thisTrain.Train.standardOverlapM;
                }
            }

            else if (CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
            {
                if (Overlap > 0)
                {
                    distanceToClear = reqDistanceTravelledM + Length + Convert.ToSingle(Overlap) + thisTrain.Train.standardOverlapM;
                }
                else
                {
                    distanceToClear = reqDistanceTravelledM + Length + thisTrain.Train.junctionOverlapM;
                }
            }

            Train.TCPosition presentFront = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex];
            int reverseDirectionIndex = thisTrain.TrainRouteDirectionIndex == 0 ? 1 : 0;
            Train.TCPosition presentRear = thisTrain.Train.PresentPosition[reverseDirectionIndex];

            // correct offset if position direction is not equal to route direction
            float frontOffset = presentFront.TCOffset;
            if (presentFront.RouteListIndex >= 0 &&
                presentFront.TCDirection != thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][presentFront.RouteListIndex].Direction)
                frontOffset = Length - frontOffset;

            float rearOffset = presentRear.TCOffset;
            if (presentRear.RouteListIndex >= 0 &&
                presentRear.TCDirection != thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex][presentRear.RouteListIndex].Direction)
                rearOffset = Length - rearOffset;

            if (presentFront.TCSectionIndex == Index)
            {
                distanceToClear += thisTrain.Train.Length - frontOffset;
            }
            else if (presentRear.TCSectionIndex == Index)
            {
                distanceToClear -= rearOffset;
            }
            else
            {
                distanceToClear += thisTrain.Train.Length;
            }

            // make sure items are cleared in correct sequence
            float? lastDistance = thisTrain.Train.requiredActions.GetLastClearingDistance();
            if (lastDistance.HasValue && lastDistance > distanceToClear)
            {
                distanceToClear = lastDistance.Value;
            }

            thisTrain.Train.requiredActions.InsertAction(new Train.ClearSectionItem(distanceToClear, Index));

            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    "Set clear action : section : " + Index + " : distance to clear : " + distanceToClear + "\n");
            }

            // set deadlock trap if required

            if (thisTrain.Train.DeadlockInfo.ContainsKey(Index))
            {
                SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[Index]);
            }

            // check for deadlock trap if taking alternative path

            if (thisTrain.Train.TCRoute != null && thisTrain.Train.TCRoute.activeAltpath >= 0)
            {
                Train.TCSubpathRoute altRoute = thisTrain.Train.TCRoute.TCAlternativePaths[thisTrain.Train.TCRoute.activeAltpath];
                Train.TCRouteElement startElement = altRoute[0];
                if (Index == startElement.TCSectionIndex)
                {
                    TrackCircuitSection endSection = signalRef.TrackCircuitList[altRoute[altRoute.Count - 1].TCSectionIndex];

                    // set deadlock trap for next section

                    if (thisTrain.Train.DeadlockInfo.ContainsKey(endSection.Index))
                    {
                        endSection.SetDeadlockTrap(thisTrain.Train, thisTrain.Train.DeadlockInfo[endSection.Index]);
                    }
                }
            }
        }

        //================================================================================================//
        //
        // clear track occupied
        //

        // routed train
        public void ClearOccupied(Train.TrainRouted thisTrain, bool resetEndSignal)
        {

#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Clear section {0} for train {1}\n",
				this.Index,
				thisTrain.Train.Number));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Clear section {0} for train {1}\n",
                    this.Index,
                    thisTrain.Train.Number));
            }

            if (CircuitState.TrainOccupy.ContainsTrain(thisTrain))
            {
                CircuitState.TrainOccupy.RemoveTrain(thisTrain);
                thisTrain.Train.OccupiedTrack.Remove(this);
            }

            RemoveTrain(thisTrain, false);   // clear occupy first to prevent loop, next clear all hanging references

            ClearDeadlockTrap(thisTrain.Train.Number); // clear deadlock traps

            // if signal at either end is still enabled for this train, reset the signal

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                if (EndSignals[iDirection] != null)
                {
                    SignalObject endSignal = EndSignals[iDirection];
                    if (endSignal.enabledTrain == thisTrain && resetEndSignal)
                    {
                        endSignal.resetSignalEnabled();
                    }
                }

                // disable all signals along section if enabled for this train

                for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                {
                    TrackCircuitSignalList thisSignalList = CircuitItems.TrackCircuitSignals[iDirection, fntype];
                    foreach (TrackCircuitSignalItem thisItem in thisSignalList.TrackCircuitItem)
                    {
                        SignalObject thisSignal = thisItem.SignalRef;
                        if (thisSignal.enabledTrain == thisTrain)
                        {
                            thisSignal.resetSignalEnabled();
                        }
                    }
                }
            }

            // if section is Junction or Crossover, reset active pins

            if (CircuitType == TrackCircuitType.Junction || CircuitType == TrackCircuitType.Crossover)
            {
                deAlignSwitchPins();

                // reset signals which routed through this junction

                foreach (int thisSignalIndex in SignalsPassingRoutes)
                {
                    SignalObject thisSignal = signalRef.SignalObjects[thisSignalIndex];
                    thisSignal.ResetRoute(Index);
                }
                SignalsPassingRoutes.Clear();
            }

            // reset manual junction setting if train is in manual mode

            if (thisTrain.Train.ControlMode == Train.TRAIN_CONTROL.MANUAL && CircuitType == TrackCircuitType.Junction && JunctionSetManual >= 0)
            {
                JunctionSetManual = -1;
            }

            // if no longer occupied and pre-reserved not empty, promote first entry of prereserved

            if (CircuitState.TrainOccupy.Count <= 0 && CircuitState.TrainPreReserved.Count > 0)
            {
                Train.TrainRouted nextTrain = CircuitState.TrainPreReserved.Dequeue();
                Train.TCSubpathRoute RoutePart = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex];

                Reserve(nextTrain, RoutePart);
            }

        }

        // unrouted train
        public void ClearOccupied(Train thisTrain, bool resetEndSignal)
        {
            ClearOccupied(thisTrain.routedForward, resetEndSignal); // forward
            ClearOccupied(thisTrain.routedBackward, resetEndSignal);// backward
        }

        // only reset occupied state - use in case of reversal or mode change when train has not actually moved
        // routed train
        public void ResetOccupied(Train.TrainRouted thisTrain)
        {

            if (CircuitState.TrainOccupy.ContainsTrain(thisTrain))
            {
                CircuitState.TrainOccupy.RemoveTrain(thisTrain);
                thisTrain.Train.OccupiedTrack.Remove(this);

                if (thisTrain.Train.CheckTrain)
                {
                    File.AppendAllText(@"C:\temp\checktrain.txt",
                        "Reset Occupy for section : " + Index + "\n");
                }
            }

        }

        // unrouted train
        public void ResetOccupied(Train thisTrain)
        {
            ResetOccupied(thisTrain.routedForward); // forward
            ResetOccupied(thisTrain.routedBackward);// backward
        }

        //================================================================================================//
        //
        // Remove train from section
        //

        // routed train
        public void RemoveTrain(Train.TrainRouted thisTrain, bool resetEndSignal)
        {
#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Remove train from section {0} for train {1}\n",
				this.Index,
				thisTrain.Train.Number));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Remove train from section {0} for train {1}\n",
                    this.Index,
                    thisTrain.Train.Number));
            }

            if (CircuitState.ThisTrainOccupying(thisTrain))
            {
                ClearOccupied(thisTrain, resetEndSignal);
            }

            if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train == thisTrain.Train)
            {
                CircuitState.TrainReserved = null;
                ClearOccupied(thisTrain, resetEndSignal);    // call clear occupy to reset signals and switches //
            }

            if (CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {
                CircuitState.TrainClaimed = removeFromQueue(CircuitState.TrainClaimed, thisTrain);
            }

            if (CircuitState.TrainPreReserved.ContainsTrain(thisTrain))
            {
                CircuitState.TrainPreReserved = removeFromQueue(CircuitState.TrainPreReserved, thisTrain);
            }
        }


        // unrouted train
        public void RemoveTrain(Train thisTrain, bool resetEndSignal)
        {
            RemoveTrain(thisTrain.routedForward, resetEndSignal);
            RemoveTrain(thisTrain.routedBackward, resetEndSignal);
        }

        //================================================================================================//
        //
        // Remove train reservations from section
        //

        public void UnreserveTrain(Train.TrainRouted thisTrain, bool resetEndSignal)
        {
            if (CircuitState.TrainReserved != null && CircuitState.TrainReserved.Train == thisTrain.Train)
            {
                CircuitState.TrainReserved = null;
                ClearOccupied(thisTrain, resetEndSignal);    // call clear occupy to reset signals and switches //
            }

            if (CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {
                CircuitState.TrainClaimed = removeFromQueue(CircuitState.TrainClaimed, thisTrain);
            }

            if (CircuitState.TrainPreReserved.ContainsTrain(thisTrain))
            {
                CircuitState.TrainPreReserved = removeFromQueue(CircuitState.TrainPreReserved, thisTrain);
            }
        }

        //================================================================================================//
        //
        // Remove train clain from section
        //

        public void UnclaimTrain(Train.TrainRouted thisTrain)
        {
            if (CircuitState.TrainClaimed.ContainsTrain(thisTrain))
            {
                CircuitState.TrainClaimed = removeFromQueue(CircuitState.TrainClaimed, thisTrain);
            }
        }

        //================================================================================================//
        //
        // Remove all reservations from section if signal not enabled for train
        //

        public void Unreserve()
        {
            CircuitState.SignalReserved = -1;
        }

        //================================================================================================//
        //
        // Remove claims from sections for reversed trains
        //

        public void ClearReversalClaims(Train.TrainRouted thisTrain)
        {
            // check if any trains have claimed this section
            List<Train.TrainRouted> claimedTrains = new List<Train.TrainRouted>();

            // get list of trains with claims on this section
            foreach (Train.TrainRouted claimingTrain in CircuitState.TrainClaimed)
            {
                claimedTrains.Add(claimingTrain);
            }
            foreach (Train.TrainRouted claimingTrain in claimedTrains)
            {
                UnclaimTrain(claimingTrain);
            }
            // get train route
            Train.TCSubpathRoute usedRoute = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex];
            int routeIndex = usedRoute.GetRouteIndex(Index, 0);

            // run down route and clear all claims for found trains, until end 
            for (int iRouteIndex = routeIndex; iRouteIndex <= usedRoute.Count - 1 && (claimedTrains.Count > 0); iRouteIndex++)
            {
                TrackCircuitSection nextSection = signalRef.TrackCircuitList[usedRoute[iRouteIndex].TCSectionIndex];

                for (int iTrain = claimedTrains.Count - 1; iTrain >= 0; iTrain--)
                {
                    Train.TrainRouted claimingTrain = claimedTrains[iTrain];

                    if (nextSection.CircuitState.TrainClaimed.ContainsTrain(claimingTrain))
                    {
                        nextSection.UnclaimTrain(claimingTrain);
                    }
                    else
                    {
                        claimedTrains.Remove(claimingTrain);
                    }
                }

                nextSection.Claim(thisTrain);
            }
        }

        //================================================================================================//
        //
        // Remove specified train from queue
        //

        static TrainQueue removeFromQueue(TrainQueue thisQueue, Train.TrainRouted thisTrain)
        {
            List<Train.TrainRouted> tempList = new List<Train.TrainRouted>();
            TrainQueue newQueue = new TrainQueue();

            // extract trains from queue and store in list - this will revert the order!
            // do not store train which is to be removed

            int queueCount = thisQueue.Count;
            while (queueCount > 0)
            {
                Train.TrainRouted queueTrain = thisQueue.Dequeue();
                if (thisTrain == null || queueTrain.Train != thisTrain.Train)
                {
                    tempList.Add(queueTrain);
                }
                queueCount = thisQueue.Count;
            }

            // restore the order by requeing

            foreach (Train.TrainRouted queueTrain in tempList)
            {
                newQueue.Enqueue(queueTrain);
            }

            return (newQueue);
        }

        //================================================================================================//
        //
        // align pins switch or crossover
        //

        public void alignSwitchPins(int linkedSectionIndex)
        {
            int alignDirection = -1;  // pin direction for leading section
            int alignLink = -1;       // link index for leading section

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                for (int iLink = 0; iLink <= 1; iLink++)
                {
                    if (Pins[iDirection, iLink].Link == linkedSectionIndex)
                    {
                        alignDirection = iDirection;
                        alignLink = iLink;
                    }
                }
            }

            if (alignDirection >= 0)
            {
                ActivePins[alignDirection, 0].Link = -1;
                ActivePins[alignDirection, 1].Link = -1;

                ActivePins[alignDirection, alignLink].Link =
                        Pins[alignDirection, alignLink].Link;
                ActivePins[alignDirection, alignLink].Direction =
                        Pins[alignDirection, alignLink].Direction;

                TrackCircuitSection linkedSection = signalRef.TrackCircuitList[linkedSectionIndex];
                for (int iDirection = 0; iDirection <= 1; iDirection++)
                {
                    for (int iLink = 0; iLink <= 1; iLink++)
                    {
                        if (linkedSection.Pins[iDirection, iLink].Link == Index)
                        {
                            linkedSection.ActivePins[iDirection, iLink].Link = Index;
                            linkedSection.ActivePins[iDirection, iLink].Direction =
                                    linkedSection.Pins[iDirection, iLink].Direction;
                        }
                    }
                }
            }

            // if junction, align physical switch

            if (CircuitType == TrackCircuitType.Junction)
            {
                int switchPos = -1;
                if (ActivePins[1, 0].Link != -1)
                    switchPos = 0;
                if (ActivePins[1, 1].Link != -1)
                    switchPos = 1;

                if (switchPos >= 0)
                {
                    signalRef.setSwitch(OriginalIndex, switchPos, this);
                }
            }
        }

        //================================================================================================//
        //
        // de-align active switch pins
        //

        public void deAlignSwitchPins()
        {
            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                if (Pins[iDirection, 1].Link > 0)     // active switchable end
                {
                    for (int iLink = 0; iLink <= 1; iLink++)
                    {
                        int activeLink = Pins[iDirection, iLink].Link;
                        int activeDirection = Pins[iDirection, iLink].Direction == 0 ? 1 : 0;
                        ActivePins[iDirection, iLink].Link = -1;

                        TrackCircuitSection linkSection = signalRef.TrackCircuitList[activeLink];
                        linkSection.ActivePins[activeDirection, 0].Link = -1;
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Get section state for request clear node
        //

        public bool GetSectionStateClearNode(Train.TrainRouted thisTrain, int elementDirection, Train.TCSubpathRoute routePart)
        {
            bool returnValue = false;

            if (signalRef.Simulator.TimetableMode)
            {
                returnValue = getSectionState(thisTrain, elementDirection, SignalObject.InternalBlockstate.Reserved, routePart, -1) <= SignalObject.InternalBlockstate.Reservable;
            }
            else
            {
                returnValue = IsAvailable(thisTrain);
            }

            return (returnValue);
        }

        //================================================================================================//
        //
        // Get state of single section
        //

        // check for train
        public SignalObject.InternalBlockstate getSectionState(Train.TrainRouted thisTrain, int direction,
                        SignalObject.InternalBlockstate passedBlockstate, Train.TCSubpathRoute thisRoute, int signalIndex)
        {
            SignalObject.InternalBlockstate thisBlockstate;
            SignalObject.InternalBlockstate localBlockstate = SignalObject.InternalBlockstate.Reservable;  // default value
            bool stateSet = false;

            TrackCircuitState thisState = CircuitState;

            // track occupied - check speed and direction - only for normal sections

            if (thisTrain != null && thisState.TrainOccupy.ContainsTrain(thisTrain))
            {
                localBlockstate = SignalObject.InternalBlockstate.Reserved;  // occupied by own train counts as reserved
                stateSet = true;
            }
            else if (thisState.HasTrainsOccupying(direction, true))
            {
                {
                    localBlockstate = SignalObject.InternalBlockstate.OccupiedSameDirection;
                    stateSet = true;
                }
            }
            else
            {
                int reqDirection = direction == 0 ? 1 : 0;
                if (thisState.HasTrainsOccupying(reqDirection, false))
                {
                    localBlockstate = SignalObject.InternalBlockstate.OccupiedOppositeDirection;
                    stateSet = true;
                }
            }

            // for junctions or cross-overs, check route selection

            if (CircuitType == TrackCircuitType.Junction || CircuitType == TrackCircuitType.Crossover)
            {
                if (thisState.HasTrainsOccupying())    // there is a train on the switch
                {
                    if (thisRoute == null)  // no route from signal - always report switch blocked
                    {
                        localBlockstate = SignalObject.InternalBlockstate.Blocked;
                        stateSet = true;
                    }
                    else
                    {
                        int reqPinIndex = -1;
                        for (int iPinIndex = 0; iPinIndex <= 1 && reqPinIndex < 0; iPinIndex++)
                        {
                            if (Pins[iPinIndex, 1].Link > 0)
                                reqPinIndex = iPinIndex;  // switchable end
                        }

                        int switchEnd = -1;
                        for (int iSwitch = 0; iSwitch <= 1; iSwitch++)
                        {
                            int nextSectionIndex = Pins[reqPinIndex, iSwitch].Link;
                            int routeListIndex = thisRoute == null ? -1 : thisRoute.GetRouteIndex(nextSectionIndex, 0);
                            if (routeListIndex >= 0)
                                switchEnd = iSwitch;  // required exit
                        }
                        if (switchEnd < 0 || ActivePins[reqPinIndex, switchEnd].Link < 0) // no free exit available or switch misaligned
                        {
                            localBlockstate = SignalObject.InternalBlockstate.Blocked;
                            stateSet = true;
                        }
                    }
                }
            }

            // track reserved - check direction

            if (thisState.TrainReserved != null && thisTrain != null && !stateSet)
            {
                Train.TrainRouted reservedTrain = thisState.TrainReserved;
                if (reservedTrain.Train == thisTrain.Train)
                {
                    localBlockstate = SignalObject.InternalBlockstate.Reserved;
                    stateSet = true;
                }
                else
                {
                    if (MPManager.IsMultiPlayer())
                    {
                        var reservedTrainStillThere = false;
                        foreach (var s in this.EndSignals)
                        {
                            if (s != null && s.enabledTrain != null && s.enabledTrain.Train == reservedTrain.Train) reservedTrainStillThere = true;
                        }

                        if (reservedTrainStillThere == true && reservedTrain.Train.GetDistanceToTrain(this.Index, 0.0f) > 0)
                            localBlockstate = SignalObject.InternalBlockstate.ReservedOther;
                        else
                        {
                            //if (reservedTrain.Train.RearTDBTraveller.DistanceTo(this.
                            thisState.TrainReserved = thisTrain;
                            localBlockstate = SignalObject.InternalBlockstate.Reserved;
                        }
                    }
                    else
                        localBlockstate = SignalObject.InternalBlockstate.ReservedOther;
                }
            }

            // signal reserved - reserved for other

            if (thisState.SignalReserved >= 0 && thisState.SignalReserved != signalIndex)
            {
                localBlockstate = SignalObject.InternalBlockstate.ReservedOther;
                stateSet = true;
            }

            // track claimed

            if (!stateSet && thisTrain != null && thisState.TrainClaimed.Count > 0 && thisState.TrainClaimed.PeekTrain() != thisTrain.Train)
            {
                localBlockstate = SignalObject.InternalBlockstate.Open;
                stateSet = true;
            }

            // wait condition

            if (thisTrain != null)
            {
                bool waitRequired = thisTrain.Train.CheckWaitCondition(Index);

                if ((!stateSet || localBlockstate < SignalObject.InternalBlockstate.ForcedWait) && waitRequired)
                {
                    localBlockstate = SignalObject.InternalBlockstate.ForcedWait;
                    thisTrain.Train.ClaimState = false; // claim not allowed for forced wait
                }
            }

            // deadlock trap - may not set deadlock if wait is active 

            if (thisTrain != null && localBlockstate != SignalObject.InternalBlockstate.ForcedWait && DeadlockTraps.ContainsKey(thisTrain.Train.Number))
            {
                localBlockstate = SignalObject.InternalBlockstate.Blocked;
                stateSet = true;
                if (!DeadlockAwaited.Contains(thisTrain.Train.Number))
                    DeadlockAwaited.Add(thisTrain.Train.Number);
            }

            thisBlockstate = localBlockstate > passedBlockstate ? localBlockstate : passedBlockstate;

            return (thisBlockstate);
        }


        //================================================================================================//
        //
        // Test if train ahead and calculate distance to that train (front or rear depending on direction)
        //

        public Dictionary<Train, float> TestTrainAhead(Train thisTrain, float offset, int direction)
        {
            Train trainFound = null;
            float distanceTrainAheadM = Length + 1.0f; // ensure train is always within section

            List<Train.TrainRouted> trainsInSection = CircuitState.TrainsOccupying();

            // remove own train
            if (thisTrain != null)
            {
                for (int iindex = trainsInSection.Count - 1; iindex >= 0; iindex--)
                {
                    if (trainsInSection[iindex].Train == thisTrain)
                        trainsInSection.RemoveAt(iindex);
                }
            }

            // search for trains in section
            foreach (Train.TrainRouted nextTrain in trainsInSection)
            {
                int nextTrainRouteIndex = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex].GetRouteIndex(Index, 0);
                if (nextTrainRouteIndex >= 0)
                {
                    Train.TCPosition nextFront = nextTrain.Train.PresentPosition[nextTrain.TrainRouteDirectionIndex];
                    int reverseDirection = nextTrain.TrainRouteDirectionIndex == 0 ? 1 : 0;
                    Train.TCPosition nextRear = nextTrain.Train.PresentPosition[reverseDirection];

                    Train.TCRouteElement thisElement = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex][nextTrainRouteIndex];
                    if (thisElement.Direction == direction) // same direction, so if the train is in front we're looking at the rear of the train
                    {
                        if (nextRear.TCSectionIndex == Index) // rear of train is in same section
                        {
                            float thisTrainDistanceM = nextRear.TCOffset;

                            if (thisTrainDistanceM < distanceTrainAheadM && nextRear.TCOffset >= offset) // train is nearest train and in front
                            {
                                distanceTrainAheadM = thisTrainDistanceM;
                                trainFound = nextTrain.Train;
                            }
                            else if (nextRear.TCOffset < offset && nextRear.TCOffset + nextTrain.Train.Length > offset) // our end is in the middle of the train
                            {
                                distanceTrainAheadM = offset; // set distance to 0 (offset is deducted later)
                                trainFound = nextTrain.Train;
                            }
                        }
                        else
                        {
                            int nextRouteFrontIndex = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex].GetRouteIndex(nextFront.TCSectionIndex, 0);
                            int nextRouteRearIndex = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex].GetRouteIndex(nextRear.TCSectionIndex, 0);

                            if (nextRouteRearIndex < nextTrainRouteIndex)
                            {
                                if (nextRouteFrontIndex > nextTrainRouteIndex) // train spans section, so we're in the middle of it - return 0
                                {
                                    distanceTrainAheadM = offset; // set distance to 0 (offset is deducted later)
                                    trainFound = nextTrain.Train;
                                } // otherwise train is not in front, so don't use it
                            }
                            else  // if index is greater, train has moved on
                            {
                                // check if still ahead of us

                                if (thisTrain != null && thisTrain.ValidRoute != null)
                                {
                                    int lastSectionIndex = thisTrain.ValidRoute[0].GetRouteIndex(nextRear.TCSectionIndex, thisTrain.PresentPosition[0].RouteListIndex);
                                    if (lastSectionIndex >= thisTrain.PresentPosition[0].RouteListIndex)
                                    {
                                        distanceTrainAheadM = Length;  // offset is deducted later
                                        for (int isection = nextTrainRouteIndex + 1; isection <= nextRear.RouteListIndex - 1; isection++)
                                        {
                                            distanceTrainAheadM += signalRef.TrackCircuitList[nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex][isection].TCSectionIndex].Length;
                                        }
                                        distanceTrainAheadM += nextTrain.Train.PresentPosition[1].TCOffset;
                                        trainFound = nextTrain.Train;
                                    }
                                }
                            }
                        }
                    }
                    else // reverse direction, so we're looking at the front - use section length - offset as position
                    {
                        float thisTrainOffset = Length - nextFront.TCOffset;
                        if (nextFront.TCSectionIndex == Index)  // front of train in section
                        {
                            float thisTrainDistanceM = thisTrainOffset;

                            if (thisTrainDistanceM < distanceTrainAheadM && thisTrainOffset >= offset) // train is nearest train and in front
                            {
                                distanceTrainAheadM = thisTrainDistanceM;
                                trainFound = nextTrain.Train;
                            }
                            // extra test : if front is beyond other train but rear is not, train is considered to be still in front (at distance = offset)
                            // this can happen in pre-run mode due to large interval
                            if (thisTrain != null && thisTrainDistanceM < distanceTrainAheadM && thisTrainOffset < offset)
                            {
                                if ((!signalRef.Simulator.TimetableMode && thisTrainOffset >= (offset - nextTrain.Train.Length)) ||
                                    (signalRef.Simulator.TimetableMode && thisTrainOffset >= (offset - thisTrain.Length)))
                                {
                                    distanceTrainAheadM = offset;
                                    trainFound = nextTrain.Train;
                                }
                            }
                        }
                        else
                        {
                            int nextRouteFrontIndex = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex].GetRouteIndex(nextFront.TCSectionIndex, 0);
                            int nextRouteRearIndex = nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex].GetRouteIndex(nextRear.TCSectionIndex, 0);

                            if (nextRouteFrontIndex < nextTrainRouteIndex)
                            {
                                if (nextRouteRearIndex > nextTrainRouteIndex)  // train spans section so we're in the middle of it
                                {
                                    distanceTrainAheadM = offset; // set distance to 0 (offset is deducted later)
                                    trainFound = nextTrain.Train;
                                } // else train is not in front of us
                            }
                            else  // if index is greater, train has moved on - return section length minus offset
                            {
                                // check if still ahead of us
                                if (thisTrain != null && thisTrain.ValidRoute != null)
                                {
                                    int lastSectionIndex = thisTrain.ValidRoute[0].GetRouteIndex(nextRear.TCSectionIndex, thisTrain.PresentPosition[0].RouteListIndex);
                                    if (lastSectionIndex > thisTrain.PresentPosition[0].RouteListIndex)
                                    {
                                        distanceTrainAheadM = Length;  // offset is deducted later
                                        for (int isection = nextTrainRouteIndex + 1; isection <= nextRear.RouteListIndex - 1; isection++)
                                        {
                                            distanceTrainAheadM += signalRef.TrackCircuitList[nextTrain.Train.ValidRoute[nextTrain.TrainRouteDirectionIndex][isection].TCSectionIndex].Length;
                                        }
                                        distanceTrainAheadM += nextTrain.Train.PresentPosition[1].TCOffset;
                                        trainFound = nextTrain.Train;
                                    }
                                }
                            }
                        }

                    }
                }
                else
                {
                    distanceTrainAheadM = offset; // train is off its route - assume full section occupied, offset is deducted later //
                    trainFound = nextTrain.Train;
                }
            }

            Dictionary<Train, float> result = new Dictionary<Train, float>();
            if (trainFound != null)
                if (distanceTrainAheadM >= offset) // train is indeed ahead
                {
                    result.Add(trainFound, (distanceTrainAheadM - offset));
                }
            return (result);
        }

        //================================================================================================//
        //
        // Get next active link
        //

        public TrPin GetNextActiveLink(int direction, int lastIndex)
        {

            // Crossover

            if (CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
            {
                int inPinIndex = direction == 0 ? 1 : 0;
                if (Pins[inPinIndex, 0].Link == lastIndex)
                {
                    return (ActivePins[direction, 0]);
                }
                else if (Pins[inPinIndex, 1].Link == lastIndex)
                {
                    return (ActivePins[direction, 1]);
                }
                else
                {
                    TrPin dummyPin = new TrPin() { Direction = -1, Link = -1 };
                    return (dummyPin);
                }
            }

            // All other sections

            if (ActivePins[direction, 0].Link > 0)
            {
                return (ActivePins[direction, 0]);
            }

            return (ActivePins[direction, 1]);
        }

        //================================================================================================//
        //
        // Get distance between objects
        //

        public float GetDistanceBetweenObjects(int startSectionIndex, float startOffset, int startDirection,
            int endSectionIndex, float endOffset)
        {
            int thisSectionIndex = startSectionIndex;
            int direction = startDirection;

            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

            float distanceM = 0.0f;
            int lastIndex = -2;  // set to non-occuring value

            while (thisSectionIndex != endSectionIndex && thisSectionIndex > 0)
            {
                distanceM += thisSection.Length;
                TrPin nextLink = thisSection.GetNextActiveLink(direction, lastIndex);

                lastIndex = thisSectionIndex;
                thisSectionIndex = nextLink.Link;
                direction = nextLink.Direction;

                if (thisSectionIndex > 0)
                {
                    thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                    if (thisSectionIndex == startSectionIndex)  // loop found - return distance found sofar
                    {
                        distanceM -= startOffset;
                        return (distanceM);
                    }
                }
            }

            // use found distance, correct for begin and end offset

            if (thisSectionIndex == endSectionIndex)
            {
                distanceM += endOffset - startOffset;
                return (distanceM);
            }

            return (-1.0f);
        }

        //================================================================================================//
        //
        // Check if train can be placed in section
        //

        public bool CanPlaceTrain(Train thisTrain, float offset, float trainLength)
        {

            if (!IsAvailable(thisTrain))
            {
                if (CircuitState.TrainReserved != null ||
                CircuitState.TrainClaimed.Count > 0)
                {
                    return (false);
                }

                if (DeadlockTraps.ContainsKey(thisTrain.Number))
                {
                    return (false);  // prevent deadlock
                }

                if (CircuitType != TrackCircuitType.Normal) // other than normal and not clear - return false
                {
                    return (false);
                }

                if (offset == 0 && trainLength > Length) // train spans section
                {
                    return (false);
                }

                // get other trains in section

                Dictionary<Train, float> trainInfo = new Dictionary<Train, float>();
                float offsetFromStart = offset;

                // test train ahead of rear end (for non-placed trains, always use direction 0)

                if (thisTrain.PresentPosition[1].TCSectionIndex == Index)
                {
                    trainInfo = TestTrainAhead(thisTrain,
                            offsetFromStart, thisTrain.PresentPosition[1].TCDirection); // rear end in this section, use offset
                }
                else
                {
                    offsetFromStart = 0.0f;
                    trainInfo = TestTrainAhead(thisTrain,
                            0.0f, thisTrain.PresentPosition[1].TCDirection); // test from start
                }

                if (trainInfo.Count > 0)
                {
                    foreach (KeyValuePair<Train, float> trainAhead in trainInfo)
                    {
                        if (trainAhead.Value < trainLength) // train ahead not clear
                        {
                            return (false);
                        }
                        else
                        {
                            var trainPosition = trainAhead.Key.PresentPosition[trainAhead.Key.MUDirection == Direction.Forward ? 0 : 1];
                            if (trainPosition.TCSectionIndex == Index && trainAhead.Key.SpeedMpS > 0 && trainPosition.TCDirection != thisTrain.PresentPosition[0].TCDirection)
                            {
                                return (false);   // train is moving towards us
                            }
                        }
                    }
                }

                // test train behind of front end

                int revDirection = thisTrain.PresentPosition[0].TCDirection == 0 ? 1 : 0;
                if (thisTrain.PresentPosition[0].TCSectionIndex == Index)
                {
                    float offsetFromEnd = Length - (trainLength + offsetFromStart);
                    trainInfo = TestTrainAhead(thisTrain, offsetFromEnd, revDirection); // test remaining length
                }
                else
                {
                    trainInfo = TestTrainAhead(thisTrain, 0.0f, revDirection); // test full section
                }

                if (trainInfo.Count > 0)
                {
                    foreach (KeyValuePair<Train, float> trainAhead in trainInfo)
                    {
                        if (trainAhead.Value < trainLength) // train behind not clear
                        {
                            return (false);
                        }
                    }
                }

            }

            return (true);
        }

        //================================================================================================//
        //
        // Set deadlock trap for all trains which deadlock from this section at begin section
        //

        public void SetDeadlockTrap(Train thisTrain, List<Dictionary<int, int>> thisDeadlock)
        {
            foreach (Dictionary<int, int> deadlockInfo in thisDeadlock)
            {
                foreach (KeyValuePair<int, int> deadlockDetails in deadlockInfo)
                {
                    int otherTrainNumber = deadlockDetails.Key;
                    Train otherTrain = thisTrain.GetOtherTrainByNumber(deadlockDetails.Key);

                    int endSectionIndex = deadlockDetails.Value;

                    // check if endsection still in path
                    if (thisTrain.ValidRoute[0].GetRouteIndex(endSectionIndex, thisTrain.PresentPosition[0].RouteListIndex) >= 0)
                    {
                        TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];

                        // if other section allready set do not set deadlock
                        if (otherTrain != null && endSection.IsSet(otherTrain, true))
                            break;

                        if (DeadlockTraps.ContainsKey(thisTrain.Number))
                        {
                            List<int> thisTrap = DeadlockTraps[thisTrain.Number];
                            if (thisTrap.Contains(otherTrainNumber))
                                break;  // cannot set deadlock for train which has deadlock on this end
                        }

                        if (endSection.DeadlockTraps.ContainsKey(otherTrainNumber))
                        {
                            if (!endSection.DeadlockTraps[otherTrainNumber].Contains(thisTrain.Number))
                            {
                                endSection.DeadlockTraps[otherTrainNumber].Add(thisTrain.Number);
                            }
                        }
                        else
                        {
                            List<int> deadlockList = new List<int>();
                            deadlockList.Add(thisTrain.Number);
                            endSection.DeadlockTraps.Add(otherTrainNumber, deadlockList);
                        }

                        if (!endSection.DeadlockActives.Contains(thisTrain.Number))
                        {
                            endSection.DeadlockActives.Add(thisTrain.Number);
                        }
                    }
                }
            }
        }
        //================================================================================================//
        //
        // Set deadlock trap for individual train at end section
        //

        public void SetDeadlockTrap(int thisTrainNumber, int otherTrainNumber)
        {

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt",
                    "\n **** Set deadlock " + Index + " for train : " + thisTrainNumber.ToString() + " with train :  " + otherTrainNumber.ToString() + "\n");
#endif

            if (DeadlockTraps.ContainsKey(otherTrainNumber))
            {
                if (!DeadlockTraps[otherTrainNumber].Contains(thisTrainNumber))
                {
                    DeadlockTraps[otherTrainNumber].Add(thisTrainNumber);
                }
            }
            else
            {
                List<int> deadlockList = new List<int>();
                deadlockList.Add(thisTrainNumber);
                DeadlockTraps.Add(otherTrainNumber, deadlockList);
            }

            if (!DeadlockActives.Contains(thisTrainNumber))
            {
                DeadlockActives.Add(thisTrainNumber);
            }
        }

        //================================================================================================//
        //
        // Clear deadlock trap
        //

        public void ClearDeadlockTrap(int thisTrainNumber)
        {
            List<int> deadlocksCleared = new List<int>();

            if (DeadlockActives.Contains(thisTrainNumber))
            {

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt",
                    "\n **** Clearing deadlocks " + Index + " for train : " + thisTrainNumber.ToString() + "\n");
#endif

                foreach (KeyValuePair<int, List<int>> thisDeadlock in DeadlockTraps)
                {
                    if (thisDeadlock.Value.Contains(thisTrainNumber))
                    {
                        thisDeadlock.Value.Remove(thisTrainNumber);
                        if (thisDeadlock.Value.Count <= 0)
                        {
                            deadlocksCleared.Add(thisDeadlock.Key);
                        }
                    }
                }
                DeadlockActives.Remove(thisTrainNumber);
            }

            foreach (int deadlockKey in deadlocksCleared)
            {
                DeadlockTraps.Remove(deadlockKey);
            }

            DeadlockAwaited.Remove(thisTrainNumber);

#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\Temp\deadlock.txt",
                "\n **** \n");
#endif

        }

        //================================================================================================//
        //
        // Check if train is waiting for deadlock
        //

        public bool CheckDeadlockAwaited(int trainNumber)
        {
            int totalCount = DeadlockAwaited.Count;
            if (DeadlockAwaited.Contains(trainNumber))
                totalCount--;
            return (totalCount > 0);
        }

        //================================================================================================//
        //
        // Clear track sections from train behind
        //

        public void ClearSectionsOfTrainBehind(Train.TrainRouted trainRouted, TrackCircuitSection startTCSectionIndex)
        {
            int startindex = 0;
            startTCSectionIndex.UnreserveTrain(trainRouted, true);
            for (int iindex = 0; iindex < trainRouted.Train.ValidRoute[0].Count; iindex++)
            {
                if (startTCSectionIndex == signalRef.TrackCircuitList[trainRouted.Train.ValidRoute[0][iindex].TCSectionIndex])
                {
                    startindex = iindex + 1;
                    break;
                }
            }

            for (int iindex = startindex; iindex < trainRouted.Train.ValidRoute[0].Count; iindex++)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[trainRouted.Train.ValidRoute[0][iindex].TCSectionIndex];
                if (thisSection.CircuitState.TrainReserved == null)
                    break;
                thisSection.UnreserveTrain(trainRouted, true);
            }
            // signalRef.BreakDownRouteList(trainRouted.Train.ValidRoute[trainRouted.TrainRouteDirectionIndex], startindex-1, trainRouted);
            // Reset signal behind new train
            for (int iindex = startindex - 2; iindex >= trainRouted.Train.PresentPosition[0].RouteListIndex; iindex--)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[trainRouted.Train.ValidRoute[trainRouted.TrainRouteDirectionIndex][iindex].TCSectionIndex];
                SignalObject thisSignal = thisSection.EndSignals[trainRouted.Train.ValidRoute[trainRouted.TrainRouteDirectionIndex][iindex].Direction];
                if (thisSignal != null)
                {
                    thisSignal.ResetSignal(false);
                    break;
                }
            }
        }

        //================================================================================================//

    }// class TrackCircuitSection

    //================================================================================================//
    //
    // class TrackCircuitItems
    //
    //================================================================================================//
    //
    // Class for track circuit item storage
    //

    public class TrackCircuitItems
    {
        public TrackCircuitSignalList[,]
            TrackCircuitSignals = new TrackCircuitSignalList[2, (int)MstsSignalFunction.UNKNOWN];
        // List of signals (per direction and per type) //
        public TrackCircuitSignalList[]
            TrackCircuitSpeedPosts = new TrackCircuitSignalList[2];
        // List of speedposts (per direction) //
        public List<TrackCircuitMilepost> MilePosts = new List<TrackCircuitMilepost>();
        // List of mileposts //

#if ACTIVITY_EDITOR
        // List of all Element coming from OR configuration in a generic form.
        public List<TrackCircuitElement> TrackCircuitElements = new List<TrackCircuitElement>();
#endif

        //================================================================================================//
        //
        // Constructor
        //

        public TrackCircuitItems()
        {
            TrackCircuitSignalList thisList;

            for (int iDirection = 0; iDirection <= 1; iDirection++)
            {
                for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                {
                    thisList = new TrackCircuitSignalList();
                    TrackCircuitSignals[iDirection, fntype] = thisList;
                }

                thisList = new TrackCircuitSignalList();
                TrackCircuitSpeedPosts[iDirection] = thisList;
            }
        }
    }

    //================================================================================================//
    //
    // class MilepostObject
    //
    //================================================================================================//
    //
    // Class for track circuit mileposts
    //

    public class TrackCircuitMilepost
    {
        public float MilepostValue;                        // milepost value                   //
        public float[] MilepostLocation = new float[2];    // milepost location from both ends //
    }

    //================================================================================================//
    //
    // class TrackCircuitSignalList
    //
    //================================================================================================//
    //
    // Class for track circuit signal list
    //

    public class TrackCircuitSignalList
    {
        public List<TrackCircuitSignalItem> TrackCircuitItem = new List<TrackCircuitSignalItem>();
        // List of signal items //
    }

    //================================================================================================//
    //
    // class TrackCircuitSignalItem
    //
    //================================================================================================//
    //
    // Class for track circuit signal item
    //

    public class TrackCircuitSignalItem
    {
        public ObjectItemInfo.ObjectItemFindState SignalState;  // returned state // 
        public SignalObject SignalRef;            // related SignalObject     //
        public float SignalLocation;              // relative signal position //


        //================================================================================================//
        //
        // Constructor
        //

        public TrackCircuitSignalItem(SignalObject thisRef, float thisLocation)
        {
            SignalState = ObjectItemInfo.ObjectItemFindState.Object;
            SignalRef = thisRef;
            SignalLocation = thisLocation;
        }


        public TrackCircuitSignalItem(ObjectItemInfo.ObjectItemFindState thisState)
        {
            SignalState = thisState;
        }
    }

    //================================================================================================//
    //
    // subclass for TrackCircuitState
    //
    //================================================================================================//
    //
    // Class for track circuit state train occupied
    //

    public class TrainOccupyState : Dictionary<Train.TrainRouted, int>
    {
        // Contains
        public bool ContainsTrain(Train.TrainRouted thisTrain)
        {
            if (thisTrain == null) return (false);
            return (ContainsKey(thisTrain.Train.routedForward) || ContainsKey(thisTrain.Train.routedBackward));
        }

        public bool ContainsTrain(Train thisTrain)
        {
            if (thisTrain == null) return (false);
            return (ContainsKey(thisTrain.routedForward) || ContainsKey(thisTrain.routedBackward));
        }

        public Dictionary<bool, int> ContainsTrainDirected(Train.TrainRouted thisTrain)
        {
            Dictionary<bool, int> returnValue = new Dictionary<bool, int>();
            return (ContainsTrainDirected(thisTrain.Train));
        }

        public Dictionary<bool, int> ContainsTrainDirected(Train thisTrain)
        {
            Dictionary<bool, int> returnValue = new Dictionary<bool, int>();
            bool trainFound = false;

            if (thisTrain != null)
            {
                trainFound = ContainsKey(thisTrain.routedForward) || ContainsKey(thisTrain.routedBackward);
            }

            if (!trainFound)
            {
                returnValue.Add(false, 0);
            }

            else
            {
                int trainDirection = 0;
                if (ContainsKey(thisTrain.routedForward))
                {
                    trainDirection = this[thisTrain.routedForward];
                }
                else
                {
                    trainDirection = this[thisTrain.routedBackward];
                }

                returnValue.Add(true, trainDirection);
            }
            return (returnValue);
        }

        // Remove
        public void RemoveTrain(Train.TrainRouted thisTrain)
        {
            if (thisTrain != null)
            {
                if (ContainsTrain(thisTrain.Train.routedForward)) Remove(thisTrain.Train.routedForward);
                if (ContainsTrain(thisTrain.Train.routedBackward)) Remove(thisTrain.Train.routedBackward);
            }
        }
    }

    //
    // Class for track circuit state train occupied
    //

    public class TrainQueue : Queue<Train.TrainRouted>
    {
        public Train PeekTrain()
        {
            if (Count <= 0) return (null);
            Train.TrainRouted thisTrain = Peek();
            return (thisTrain.Train);
        }

        public bool ContainsTrain(Train.TrainRouted thisTrain)
        {
            if (thisTrain == null) return (false);
            return (Contains(thisTrain.Train.routedForward) || Contains(thisTrain.Train.routedBackward));
        }
    }

    //================================================================================================//
    //
    // class TrackCircuitState
    //
    //================================================================================================//
    //
    // Class for track circuit state
    //

    public class TrackCircuitState
    {
        public TrainOccupyState TrainOccupy;                       // trains occupying section      //
        public Train.TrainRouted TrainReserved;                    // train reserving section       //
        public int SignalReserved;                                 // signal reserving section      //
        public TrainQueue TrainPreReserved;                        // trains with pre-reservation   //
        public TrainQueue TrainClaimed;                            // trains with normal claims     //
        public bool RemoteAvailable;                               // remote info available         //
        public bool RemoteOccupied;                                // remote occupied state         //
        public bool RemoteSignalReserved;                          // remote signal reserved        //
        public int RemoteReserved;                                 // remote reserved (number only) //

        //================================================================================================//
        //
        // Constructor
        //

        public TrackCircuitState()
        {
            TrainOccupy = new TrainOccupyState();
            TrainReserved = null;
            SignalReserved = -1;
            TrainPreReserved = new TrainQueue();
            TrainClaimed = new TrainQueue();
        }


        //================================================================================================//
        //
        // Restore
        // IMPORTANT : trains are restored to dummy value, will be restored to full contents later
        //

        public void Restore(Simulator simulator, BinaryReader inf)
        {
            int noOccupy = inf.ReadInt32();
            for (int trainNo = 0; trainNo < noOccupy; trainNo++)
            {
                int trainNumber = inf.ReadInt32();
                int trainRouteIndex = inf.ReadInt32();
                int trainDirection = inf.ReadInt32();
                Train thisTrain = new Train(simulator, trainNumber);
                Train.TrainRouted thisRouted = new Train.TrainRouted(thisTrain, trainRouteIndex);
                TrainOccupy.Add(thisRouted, trainDirection);
            }

            int trainReserved = inf.ReadInt32();
            if (trainReserved >= 0)
            {
                int trainRouteIndexR = inf.ReadInt32();
                Train thisTrain = new Train(simulator, trainReserved);
                Train.TrainRouted thisRouted = new Train.TrainRouted(thisTrain, trainRouteIndexR);
                TrainReserved = thisRouted;
            }

            SignalReserved = inf.ReadInt32();

            int noPreReserve = inf.ReadInt32();
            for (int trainNo = 0; trainNo < noPreReserve; trainNo++)
            {
                int trainNumber = inf.ReadInt32();
                int trainRouteIndex = inf.ReadInt32();
                Train thisTrain = new Train(simulator, trainNumber);
                Train.TrainRouted thisRouted = new Train.TrainRouted(thisTrain, trainRouteIndex);
                TrainPreReserved.Enqueue(thisRouted);
            }

            int noClaimed = inf.ReadInt32();
            for (int trainNo = 0; trainNo < noClaimed; trainNo++)
            {
                int trainNumber = inf.ReadInt32();
                int trainRouteIndex = inf.ReadInt32();
                Train thisTrain = new Train(simulator, trainNumber);
                Train.TrainRouted thisRouted = new Train.TrainRouted(thisTrain, trainRouteIndex);
                TrainClaimed.Enqueue(thisRouted);
            }

        }

        //================================================================================================//
        //
        // Reset train references after restore
        //

        public void RestoreTrains(List<Train> trains)
        {

            // Occupy

            Dictionary<int[], int> tempTrains = new Dictionary<int[], int>();

            foreach (KeyValuePair<Train.TrainRouted, int> thisOccupy in TrainOccupy)
            {
                int[] trainKey = new int[2];
                trainKey[0] = thisOccupy.Key.Train.Number;
                trainKey[1] = thisOccupy.Key.TrainRouteDirectionIndex;
                int direction = thisOccupy.Value;
                tempTrains.Add(trainKey, direction);
            }

            TrainOccupy.Clear();

            foreach (KeyValuePair<int[], int> thisTemp in tempTrains)
            {
                int[] trainKey = thisTemp.Key;
                int number = trainKey[0];
                int routeIndex = trainKey[1];
                int direction = thisTemp.Value;
                Train thisTrain = Signals.FindTrain(number, trains);
                if (thisTrain != null)
                {
                    Train.TrainRouted thisTrainRouted = routeIndex == 0 ? thisTrain.routedForward : thisTrain.routedBackward;
                    TrainOccupy.Add(thisTrainRouted, direction);
                }
            }

            // Reserved

            if (TrainReserved != null)
            {
                int number = TrainReserved.Train.Number;
                Train reservedTrain = Signals.FindTrain(number, trains);
                if (reservedTrain != null)
                {
                    int reservedDirection = TrainReserved.TrainRouteDirectionIndex;
                    TrainReserved = reservedDirection == 0 ? reservedTrain.routedForward : reservedTrain.routedBackward;
                }
                else
                {
                    TrainReserved = null;
                }
            }

            // PreReserved

            Queue<Train.TrainRouted> tempQueue = new Queue<Train.TrainRouted>();

            foreach (Train.TrainRouted thisTrainRouted in TrainPreReserved)
            {
                tempQueue.Enqueue(thisTrainRouted);
            }
            TrainPreReserved.Clear();
            foreach (Train.TrainRouted thisTrainRouted in tempQueue)
            {
                Train thisTrain = Signals.FindTrain(thisTrainRouted.Train.Number, trains);
                int routeIndex = thisTrainRouted.TrainRouteDirectionIndex;
                if (thisTrain != null)
                {
                    Train.TrainRouted foundTrainRouted = routeIndex == 0 ? thisTrain.routedForward : thisTrain.routedBackward;
                    TrainPreReserved.Enqueue(foundTrainRouted);
                }
            }

            // Claimed

            tempQueue.Clear();

            foreach (Train.TrainRouted thisTrainRouted in TrainClaimed)
            {
                tempQueue.Enqueue(thisTrainRouted);
            }
            TrainClaimed.Clear();
            foreach (Train.TrainRouted thisTrainRouted in tempQueue)
            {
                Train thisTrain = Signals.FindTrain(thisTrainRouted.Train.Number, trains);
                int routeIndex = thisTrainRouted.TrainRouteDirectionIndex;
                if (thisTrain != null)
                {
                    Train.TrainRouted foundTrainRouted = routeIndex == 0 ? thisTrain.routedForward : thisTrain.routedBackward;
                    TrainClaimed.Enqueue(foundTrainRouted);
                }
            }

        }

        //================================================================================================//
        //
        // Save
        //

        public void Save(BinaryWriter outf)
        {
            outf.Write(TrainOccupy.Count);
            foreach (KeyValuePair<Train.TrainRouted, int> thisOccupy in TrainOccupy)
            {
                Train.TrainRouted thisTrain = thisOccupy.Key;
                outf.Write(thisTrain.Train.Number);
                outf.Write(thisTrain.TrainRouteDirectionIndex);
                outf.Write(thisOccupy.Value);
            }

            if (TrainReserved == null)
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(TrainReserved.Train.Number);
                outf.Write(TrainReserved.TrainRouteDirectionIndex);
            }

            outf.Write(SignalReserved);

            outf.Write(TrainPreReserved.Count);
            foreach (Train.TrainRouted thisTrain in TrainPreReserved)
            {
                outf.Write(thisTrain.Train.Number);
                outf.Write(thisTrain.TrainRouteDirectionIndex);
            }

            outf.Write(TrainClaimed.Count);
            foreach (Train.TrainRouted thisTrain in TrainClaimed)
            {
                outf.Write(thisTrain.Train.Number);
                outf.Write(thisTrain.TrainRouteDirectionIndex);
            }

        }

        //================================================================================================//
        //
        // Get list of trains occupying track, in required direction if required
        //

        public List<Train.TrainRouted> TrainsOccupying()
        {
            List<Train.TrainRouted> reqList = new List<Train.TrainRouted>();
            foreach (KeyValuePair<Train.TrainRouted, int> thisTCT in TrainOccupy)
            {
                reqList.Add(thisTCT.Key);
            }
            return (reqList);
        }

        public List<Train.TrainRouted> TrainsOccupying(int reqDirection)
        {
            List<Train.TrainRouted> reqList = new List<Train.TrainRouted>();
            foreach (KeyValuePair<Train.TrainRouted, int> thisTCT in TrainOccupy)
            {
                if (thisTCT.Value == reqDirection)
                {
                    reqList.Add(thisTCT.Key);
                }
            }
            return (reqList);
        }

        //================================================================================================//
        //
        // check if any trains occupy track, in required direction if required
        //

        public bool HasTrainsOccupying()
        {
            return (TrainOccupy.Count > 0);
        }

        public bool HasTrainsOccupying(int reqDirection, bool stationary)
        {
            foreach (KeyValuePair<Train.TrainRouted, int> thisTCT in TrainOccupy)
            {
                if (thisTCT.Value == reqDirection)
                {
                    if (Math.Abs(thisTCT.Key.Train.SpeedMpS) > 0.5f)
                        return (true);   // exclude (almost) stationary trains
                }

                if ((Math.Abs(thisTCT.Key.Train.SpeedMpS) <= 0.5f) && stationary)
                    return (true);   // (almost) stationay trains
            }

            return (false);
        }

        public bool HasOtherTrainsOccupying(Train.TrainRouted thisTrain)
        {
            if (TrainOccupy.Count == 0)  // no trains
            {
                return (false);
            }

            if (TrainOccupy.Count == 1 && TrainOccupy.ContainsTrain(thisTrain))  // only one train and that one is us
            {
                return (false);
            }

            return (true);
        }

        public bool HasOtherTrainsOccupying(int reqDirection, bool stationary, Train.TrainRouted thisTrain)
        {
            foreach (KeyValuePair<Train.TrainRouted, int> thisTCT in TrainOccupy)
            {
                Train.TrainRouted otherTrain = thisTCT.Key;
                if (otherTrain != thisTrain)
                {
                    if (thisTCT.Value == reqDirection)
                    {
                        if (Math.Abs(thisTCT.Key.Train.SpeedMpS) > 0.5f)
                            return (true);   // exclude (almost) stationary trains
                    }

                    if ((Math.Abs(thisTCT.Key.Train.SpeedMpS) <= 0.5f) && stationary)
                        return (true);   // (almost) stationay trains
                }
            }

            return (false);
        }

        //================================================================================================//
        //
        // check if this train occupies track
        //

        // routed train
        public bool ThisTrainOccupying(Train.TrainRouted thisTrain)
        {
            return (TrainOccupy.ContainsTrain(thisTrain));
        }

        // unrouted train
        public bool ThisTrainOccupying(Train thisTrain)
        {
            return (TrainOccupy.ContainsTrain(thisTrain));
        }

    }

    //================================================================================================//
    //
    // class CrossOverItem
    //
    //================================================================================================//
    //
    // Class for cross over items
    //

    public class CrossOverItem
    {
        public float[] Position = new float[2];        // position within track sections //
        public int[] SectionIndex = new int[2];          // indices of original sections   //
        public int[] ItemIndex = new int[2];             // TDB item indices               //
        public uint TrackShape;
    }

    //================================================================================================//
    //
    //  class SignalObject
    //
    //================================================================================================//

    public class SignalObject
    {

        public enum InternalBlockstate
        {
            Reserved,                   // all sections reserved for requiring train       //
            Reservable,                 // all secetions clear and reservable for train    //
            OccupiedSameDirection,      // occupied by train moving in same direction      //
            ReservedOther,              // reserved for other train                        //
            ForcedWait,                 // train is forced to wait for other train         //
            OccupiedOppositeDirection,  // occupied by train moving in opposite direction  //
            Open,                       // sections are claimed and not accesible          //
            Blocked,                    // switch locked against train                     //
        }

        public enum Permission
        {
            Granted,
            Requested,
            Denied,
        }

        public enum HoldState                // signal is locked in hold
        {
            None,                            // signal is clear
            StationStop,                     // because of station stop
            ManualLock,                      // because of manual lock. 
            ManualPass,                      // Sometime you want to set a light green, especially in MP
            ManualApproach,                  // Sometime to set approach, in MP again
            //PLEASE DO NOT CHANGE THE ORDER OF THESE ENUMS
        }

        public Signals signalRef;               // reference to overlaying Signal class
        public static SignalObject[] signalObjects;
        public static TrackNode[] trackNodes;
        public static TrItem[] trItems;
        public SignalWorldObject WorldObject;   // Signal World Object information

        public int trackNode;                   // Track node which contains this signal
        public int trRefIndex;                  // Index to TrItemRef within Track Node 

        public int TCReference = -1;            // Reference to TrackCircuit (index)
        public float TCOffset;                  // Position within TrackCircuit
        public int TCDirection;                 // Direction within TrackCircuit
        public int TCNextTC = -1;               // Index of next TrackCircuit (NORMAL signals only)
        public int TCNextDirection;             // Direction of next TrackCircuit 

        public List<int> JunctionsPassed = new List<int>();  // Junctions which are passed checking next signal //

        public int thisRef;                     // This signal's reference.
        public int direction;                   // Direction facing on track

        public bool isSignal = true;            // if signal, false if speedpost //
        public List<SignalHead> SignalHeads = new List<SignalHead>();

        public int SignalNumClearAhead_MSTS = -2;    // Overall maximum SignalNumClearAhead over all heads (MSTS calculation)
        public int SignalNumClearAhead_ORTS = -2;    // Overall maximum SignalNumClearAhead over all heads (ORTS calculation)
        public int SignalNumNormalHeads;             // no. of normal signal heads in signal
        public int ReqNumClearAhead;                 // Passed on value for SignalNumClearAhead

        public int draw_state;                  // actual signal state

        public Train.TrainRouted enabledTrain;  // full train structure for which signal is enabled

        private InternalBlockstate internalBlockState = InternalBlockstate.Open;    // internal blockstate
        public Permission hasPermission = Permission.Denied;  // Permission to pass red signal
        public HoldState holdState = HoldState.None;

        public int[] sigfound = new int[(int)MstsSignalFunction.UNKNOWN];  // active next signal - used for signals with NORMAL heads only
        private int[] defaultNextSignal = new int[(int)MstsSignalFunction.UNKNOWN];  // default next signal
        public Traveller tdbtraveller;          // TDB traveller to determine distance between objects

        public Train.TCSubpathRoute signalRoute = new Train.TCSubpathRoute();  // train route from signal
        public int trainRouteDirectionIndex;    // direction index in train route array (usually 0, value 1 valid for Manual only)
        private int thisTrainRouteIndex;        // index of section after signal in train route list

        private Train.TCSubpathRoute fixedRoute = new Train.TCSubpathRoute();     // fixed route from signal
        public bool hasFixedRoute;              // signal has fixed route
        private bool fullRoute;                 // required route is full route to next signal or end-of-track
        private bool propagated;                // route request propagated to next signal
        private bool isPropagated;              // route request for this signal was propagated from previous signal
        public bool ForcePropagation = false;   // Force propagation (used in case of signals at very short distance)

        public bool ApproachControlCleared;     // set in case signal has cleared on approach control

        public bool StationHold = false;        // Set if signal must be held at station - processed by signal script
        protected List<KeyValuePair<int, int>> LockedTrains;

        public bool enabled
        {
            get
            {
                if (MPManager.IsMultiPlayer() && MPManager.PreferGreen == true) return true;
                return (enabledTrain != null);
            }
        }

        public MstsBlockState blockState
        {
            get
            {
                MstsBlockState lstate = MstsBlockState.JN_OBSTRUCTED;
                switch (internalBlockState)
                {
                    case InternalBlockstate.Reserved:
                    case InternalBlockstate.Reservable:
                        lstate = MstsBlockState.CLEAR;
                        break;
                    case InternalBlockstate.OccupiedSameDirection:
                        lstate = MstsBlockState.OCCUPIED;
                        break;
                    default:
                        lstate = MstsBlockState.JN_OBSTRUCTED;
                        break;
                }

                return (lstate);
            }
        }

        public int trItem
        {
            get
            {
                return trackNodes[trackNode].TrVectorNode.TrItemRefs[trRefIndex];
            }
        }

        public int revDir                //  Needed because signal faces train!
        {
            get
            {
                return direction == 0 ? 1 : 0;
            }
        }

        //================================================================================================//
        ///
        //  Constructor for empty item
        ///

        public SignalObject()
        {
            LockedTrains = new List<KeyValuePair<int, int>>();
        }

        //================================================================================================//
        ///
        //  Constructor for Copy 
        ///

        public SignalObject(SignalObject copy)
        {
            signalRef = copy.signalRef;
            WorldObject = new SignalWorldObject(copy.WorldObject);

            trackNode = copy.trackNode;
            LockedTrains = new List<KeyValuePair<int, int>>();
            foreach (var lockInfo in copy.LockedTrains)
            {
                KeyValuePair<int, int> oneLock = new KeyValuePair<int, int>(lockInfo.Key, lockInfo.Value);
                LockedTrains.Add(oneLock);
            }

            TCReference = copy.TCReference;
            TCOffset = copy.TCOffset;
            TCDirection = copy.TCDirection;
            TCNextTC = copy.TCNextTC;
            TCNextDirection = copy.TCNextDirection;

            direction = copy.direction;
            isSignal = copy.isSignal;
            SignalNumClearAhead_MSTS = copy.SignalNumClearAhead_MSTS;
            SignalNumClearAhead_ORTS = copy.SignalNumClearAhead_ORTS;
            SignalNumNormalHeads = copy.SignalNumNormalHeads;

            draw_state = copy.draw_state;
            internalBlockState = copy.internalBlockState;
            hasPermission = copy.hasPermission;

            tdbtraveller = new Traveller(copy.tdbtraveller);

            sigfound = new int[copy.sigfound.Length];
            copy.sigfound.CopyTo(sigfound, 0);
            defaultNextSignal = new int[copy.defaultNextSignal.Length];
            copy.defaultNextSignal.CopyTo(defaultNextSignal, 0);
        }

        //================================================================================================//
        //
        // Constructor for restore
        // IMPORTANT : enabled train is restore temporarily as Trains are restored later as Signals
        // Full restore of train link follows in RestoreTrains
        //

        public void Restore(Simulator simulator, BinaryReader inf)
        {
            int trainNumber = inf.ReadInt32();

            for (int iSig = 0; iSig < sigfound.Length; iSig++)
            {
                sigfound[iSig] = inf.ReadInt32();
            }

            bool validRoute = inf.ReadBoolean();

            if (validRoute)
            {
                signalRoute = new Train.TCSubpathRoute(inf);
            }

            thisTrainRouteIndex = inf.ReadInt32();
            holdState = (HoldState)inf.ReadInt32();

            int totalJnPassed = inf.ReadInt32();

            for (int iJn = 0; iJn < totalJnPassed; iJn++)
            {
                int thisJunction = inf.ReadInt32();
                JunctionsPassed.Add(thisJunction);
                signalRef.TrackCircuitList[thisJunction].SignalsPassingRoutes.Add(thisRef);
            }

            fullRoute = inf.ReadBoolean();
            propagated = inf.ReadBoolean();
            isPropagated = inf.ReadBoolean();
            ForcePropagation = false; // preset (not stored)
            ApproachControlCleared = inf.ReadBoolean();
            ReqNumClearAhead = inf.ReadInt32();
            StationHold = inf.ReadBoolean();
            hasPermission = (Permission)inf.ReadInt32();

            // set dummy train, route direction index will be set later on restore of train

            enabledTrain = null;
            if (trainNumber >= 0)
            {
                Train thisTrain = new Train(simulator, trainNumber);
                Train.TrainRouted thisTrainRouted = new Train.TrainRouted(thisTrain, 0);
                enabledTrain = thisTrainRouted;
            }
            //  Retrieve lock table
            LockedTrains = new List<KeyValuePair<int, int>>();
            int cntLock = inf.ReadInt32();
            for (int cnt = 0; cnt < cntLock; cnt++)
            {
                KeyValuePair<int, int> lockInfo = new KeyValuePair<int, int>(inf.ReadInt32(), inf.ReadInt32());
                LockedTrains.Add(lockInfo);

            }
        }

        //================================================================================================//
        //
        // Restore Train Reference
        //

        public void RestoreTrains(List<Train> trains)
        {
            if (enabledTrain != null)
            {
                int number = enabledTrain.Train.Number;

                Train foundTrain = Signals.FindTrain(number, trains);

                // check if this signal is next signal forward for this train

                if (foundTrain != null && foundTrain.NextSignalObject[0] != null && this.thisRef == foundTrain.NextSignalObject[0].thisRef)
                {
                    enabledTrain = foundTrain.routedForward;
                    foundTrain.NextSignalObject[0] = this;
                }

                // check if this signal is next signal backward for this train

                else if (foundTrain != null && foundTrain.NextSignalObject[1] != null && this.thisRef == foundTrain.NextSignalObject[1].thisRef)
                {
                    enabledTrain = foundTrain.routedBackward;
                    foundTrain.NextSignalObject[1] = this;
                }
                else
                {
                    // check if this section is reserved for this train

                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[TCReference];
                    if (thisSection.CircuitState.TrainReserved != null && thisSection.CircuitState.TrainReserved.Train.Number == number)
                    {
                        enabledTrain = thisSection.CircuitState.TrainReserved;
                    }
                    else
                    {
                        enabledTrain = null; // reset - train not found
                    }
                }
            }
        }

        //================================================================================================//
        //
        // Restore Signal Aspect based on train information
        // Process non-propagated signals only, others are updated through propagation
        //

        public void RestoreAspect()
        {
            if (enabledTrain != null && !isPropagated)
            {
                if (isSignalNormal())
                {
                    checkRouteState(false, signalRoute, enabledTrain);
                    propagateRequest();
                    StateUpdate();
                }
                else
                {
                    getBlockState_notRouted();
                    StateUpdate();
                }
            }
        }

        //================================================================================================//
        //
        // Save
        //

        public void Save(BinaryWriter outf)
        {
            if (enabledTrain == null)
            {
                outf.Write(-1);
            }
            else
            {
                outf.Write(enabledTrain.Train.Number);
            }

            foreach (int thisSig in sigfound)
            {
                outf.Write(thisSig);
            }

            if (signalRoute == null)
            {
                outf.Write(false);
            }
            else
            {
                outf.Write(true);
                signalRoute.Save(outf);
            }

            outf.Write(thisTrainRouteIndex);
            outf.Write((int)holdState);

            outf.Write(JunctionsPassed.Count);
            if (JunctionsPassed.Count > 0)
            {
                foreach (int thisJunction in JunctionsPassed)
                {
                    outf.Write(thisJunction);
                }
            }

            outf.Write(fullRoute);
            outf.Write(propagated);
            outf.Write(ApproachControlCleared);
            outf.Write(isPropagated);
            outf.Write(ReqNumClearAhead);
            outf.Write(StationHold);
            outf.Write((int)hasPermission);
            outf.Write(LockedTrains.Count);
            for (int cnt = 0; cnt < LockedTrains.Count; cnt++)
            {
                outf.Write(LockedTrains[cnt].Key);
                outf.Write(LockedTrains[cnt].Value);
            }

        }

        //================================================================================================//
        //
        // return blockstate
        //

        public MstsBlockState block_state()
        {
            return (blockState);
        }

        //================================================================================================//
        //
        // return station hold state
        //

        public bool isStationHold()
        {
            return (StationHold);
        }

        //================================================================================================//
        ///
        // setSignalDefaultNextSignal : set default next signal based on non-Junction tracks ahead
        // this routine also sets fixed routes for signals which do not lead onto junction or crossover
        //
        ///

        public void setSignalDefaultNextSignal()
        {
            int thisTC = TCReference;
            float position = TCOffset;
            int direction = TCDirection;
            bool setFixedRoute = false;

            // for normal signals : start at next TC

            if (TCNextTC > 0)
            {
                thisTC = TCNextTC;
                direction = TCNextDirection;
                position = 0.0f;
                setFixedRoute = true;
            }

            bool completedFixedRoute = !setFixedRoute;

            // get trackcircuit

            TrackCircuitSection thisSection = null;
            if (thisTC > 0)
            {
                thisSection = signalRef.TrackCircuitList[thisTC];
            }

            // set default

            for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
            {
                defaultNextSignal[fntype] = -1;
            }

            // loop through valid sections

            while (thisSection != null && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
            {

                if (!completedFixedRoute)
                {
                    fixedRoute.Add(new Train.TCRouteElement(thisSection.Index, direction));
                }

                // normal signal

                if (defaultNextSignal[(int)MstsSignalFunction.NORMAL] < 0)
                {
                    if (thisSection.EndSignals[direction] != null)
                    {
                        defaultNextSignal[(int)MstsSignalFunction.NORMAL] = thisSection.EndSignals[direction].thisRef;
                        completedFixedRoute = true;
                    }
                }

                // other signals

                for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                {
                    if (fntype != (int)MstsSignalFunction.NORMAL && fntype != (int)MstsSignalFunction.UNKNOWN)
                    {
                        TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[direction, fntype];
                        bool signalFound = defaultNextSignal[fntype] >= 0;
                        for (int iItem = 0; iItem < thisList.TrackCircuitItem.Count && !signalFound; iItem++)
                        {
                            TrackCircuitSignalItem thisItem = thisList.TrackCircuitItem[iItem];
                            if (thisItem.SignalLocation > position)
                            {
                                defaultNextSignal[fntype] = thisItem.SignalRef.thisRef;
                                signalFound = true;
                            }
                        }
                    }
                }

                int pinIndex = direction;
                direction = thisSection.Pins[pinIndex, 0].Direction;
                thisSection = signalRef.TrackCircuitList[thisSection.Pins[pinIndex, 0].Link];
            }

            // copy default as valid items

            for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
            {
                sigfound[fntype] = defaultNextSignal[fntype];
            }

            // Allow use of fixed route if ended on END_OF_TRACK

            if (thisSection != null && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.EndOfTrack)
            {
                completedFixedRoute = true;
            }

            // if valid next normal signal found, signal has fixed route

            if (setFixedRoute && completedFixedRoute)
            {
                hasFixedRoute = true;
                fullRoute = true;
            }
            else
            {
                hasFixedRoute = false;
                fixedRoute.Clear();
            }
        }

        //================================================================================================//
        ///
        // isSignalNormal : Returns true if at least one signal head is type normal.
        ///

        public bool isSignalNormal()
        {
            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == MstsSignalFunction.NORMAL)
                    return true;
            }
            return false;
        }

        //================================================================================================//
        ///
        // isSignalType : Returns true if at least one signal head is of required type
        ///

        public bool isSignalType(MstsSignalFunction[] reqSIGFN)
        {
            foreach (SignalHead sigHead in SignalHeads)
            {
                if (reqSIGFN.Contains(sigHead.sigFunction))
                    return true;
            }
            return false;
        }

        //================================================================================================//
        ///
        // next_sig_mr : returns most restrictive state of next signal of required type
        ///
        ///

        public MstsSignalAspect next_sig_mr(MstsSignalFunction fn_type)
        {
            int nextSignal = sigfound[(int)fn_type];
            if (nextSignal < 0)
            {
                nextSignal = SONextSignal(fn_type);
                sigfound[(int)fn_type] = nextSignal;
            }

            if (nextSignal >= 0)
            {
                return signalObjects[nextSignal].this_sig_mr(fn_type);
            }
            else
            {
                return MstsSignalAspect.STOP;
            }
        }

        //================================================================================================//
        ///
        // next_sig_lr : returns least restrictive state of next signal of required type
        ///
        ///

        public MstsSignalAspect next_sig_lr(MstsSignalFunction fn_type)
        {
            int nextSignal = sigfound[(int)fn_type];
            if (nextSignal < 0)
            {
                nextSignal = SONextSignal(fn_type);
                sigfound[(int)fn_type] = nextSignal;
            }
            if (nextSignal >= 0)
            {
                return signalObjects[nextSignal].this_sig_lr(fn_type);
            }
            else
            {
                return MstsSignalAspect.STOP;
            }
        }

        //================================================================================================//
        //
        // next_nsig_lr : returns least restrictive state of next signal of required type of the nth signal ahead
        //
        //

        public MstsSignalAspect next_nsig_lr(MstsSignalFunction fn_type, int nsignal, string dumpfile)
        {
            int foundsignal = 0;
            MstsSignalAspect foundAspect = MstsSignalAspect.CLEAR_2;
            SignalObject nextSignalObject = this;

            while (foundsignal < nsignal && foundAspect != MstsSignalAspect.STOP)
            {
                // use sigfound
                int nextSignal = nextSignalObject.sigfound[(int)fn_type];

                // sigfound not set, try direct search
                if (nextSignal < 0)
                {
                    nextSignal = SONextSignal(fn_type);
                    sigfound[(int)fn_type] = nextSignal;
                }

                // signal found : get state
                if (nextSignal >= 0)
                {
                    foundsignal++;

                    nextSignalObject = signalObjects[nextSignal];
                    foundAspect = nextSignalObject.this_sig_lr(fn_type);

                    if (!String.IsNullOrEmpty(dumpfile))
                    {
                        File.AppendAllText(dumpfile, "\nNEXT_NSIG_LR : Found signal " + foundsignal + " : " + nextSignalObject.thisRef + " ; state = " + foundAspect + "\n");
                    }

                    // reached required signal or state is stop : return
                    if (foundsignal >= nsignal || foundAspect == MstsSignalAspect.STOP)
                    {
                        if (!String.IsNullOrEmpty(dumpfile))
                        {
                            File.AppendAllText(dumpfile, "NEXT_NSIG_LR : returned : " + foundAspect + "\n");
                        }
                        return (foundAspect);
                    }
                }

                // signal not found : return stop
                else
                {
                    if (!String.IsNullOrEmpty(dumpfile))
                    {
                        File.AppendAllText(dumpfile, "NEXT_NSIG_LR : returned : " + foundAspect + " ; last found index : " + foundsignal + "\n");
                    }
                    return MstsSignalAspect.STOP;
                }
            }

            if (!String.IsNullOrEmpty(dumpfile))
            {
                File.AppendAllText(dumpfile, "NEXT_NSIG_LR : while loop exited ; last found index : " + foundsignal + "\n");
            }
            return (MstsSignalAspect.STOP); // emergency exit - loop should normally have exited on return
        }

        //================================================================================================//
        //
        // opp_sig_mr
        //

        public MstsSignalAspect opp_sig_mr(MstsSignalFunction fn_type)
        {
            int signalFound = SONextSignalOpp(fn_type);
            return (signalFound >= 0 ? signalObjects[signalFound].this_sig_mr(fn_type) : MstsSignalAspect.STOP);
        }//opp_sig_mr

        public MstsSignalAspect opp_sig_mr(MstsSignalFunction fn_type, ref SignalObject foundSignal) // used for debug print process
        {
            int signalFound = SONextSignalOpp(fn_type);
            foundSignal = signalFound >= 0 ? signalObjects[signalFound] : null;
            return (signalFound >= 0 ? signalObjects[signalFound].this_sig_mr(fn_type) : MstsSignalAspect.STOP);
        }//opp_sig_mr

        //================================================================================================//
        //
        // opp_sig_lr
        //

        public MstsSignalAspect opp_sig_lr(MstsSignalFunction fn_type)
        {
            int signalFound = SONextSignalOpp(fn_type);
            return (signalFound >= 0 ? signalObjects[signalFound].this_sig_lr(fn_type) : MstsSignalAspect.STOP);
        }//opp_sig_lr

        public MstsSignalAspect opp_sig_lr(MstsSignalFunction fn_type, ref SignalObject foundSignal) // used for debug print process
        {
            int signalFound = SONextSignalOpp(fn_type);
            foundSignal = signalFound >= 0 ? signalObjects[signalFound] : null;
            return (signalFound >= 0 ? signalObjects[signalFound].this_sig_lr(fn_type) : MstsSignalAspect.STOP);
        }//opp_sig_lr

        //================================================================================================//
        //
        // this_sig_mr : Returns the most restrictive state of this signal's heads of required type
        //

        // standard version without state return
        public MstsSignalAspect this_sig_mr(MstsSignalFunction fn_type)
        {
            bool sigfound = false;
            return (this_sig_mr(fn_type, ref sigfound));
        }

        // additional version with state return
        public MstsSignalAspect this_sig_mr(MstsSignalFunction fn_type, ref bool sigfound)
        {
            MstsSignalAspect sigAsp = MstsSignalAspect.UNKNOWN;
            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == fn_type && sigHead.state < sigAsp)
                {
                    sigAsp = sigHead.state;
                }
            }
            if (sigAsp == MstsSignalAspect.UNKNOWN)
            {
                sigfound = false;
                return MstsSignalAspect.STOP;
            }
            else
            {
                sigfound = true;
                return sigAsp;
            }
        }//this_sig_mr

        //================================================================================================//
        //
        // this_sig_lr : Returns the least restrictive state of this signal's heads of required type
        //

        // standard version without state return
        public MstsSignalAspect this_sig_lr(MstsSignalFunction fn_type)
        {
            bool sigfound = false;
            return (this_sig_lr(fn_type, ref sigfound));
        }

        // additional version with state return
        public MstsSignalAspect this_sig_lr(MstsSignalFunction fn_type, ref bool sigfound)
        {
            MstsSignalAspect sigAsp = MstsSignalAspect.STOP;
            bool sigAspSet = false;
            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == fn_type && sigHead.state >= sigAsp)
                {
                    sigAsp = sigHead.state;
                    sigAspSet = true;
                }
            }

            sigfound = sigAspSet;

            if (sigAspSet)
            {
                return sigAsp;
            }
            else if (fn_type == MstsSignalFunction.NORMAL)
            {
                return MstsSignalAspect.CLEAR_2;
            }
            else
            {
                return MstsSignalAspect.STOP;
            }
        }//this_sig_lr

        //================================================================================================//
        //
        // this_sig_speed : Returns the speed related to the least restrictive aspect (for normal signal)
        //

        public ObjectSpeedInfo this_sig_speed(MstsSignalFunction fn_type)
        {
            var sigAsp = MstsSignalAspect.STOP;
            var set_speed = new ObjectSpeedInfo(-1, -1, false, false, 0);

            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == fn_type && sigHead.state >= sigAsp)
                {
                    sigAsp = sigHead.state;
                    set_speed = sigHead.speed_info[(int)sigAsp];
                }
            }
            return set_speed;
        }//this_sig_speed

        //================================================================================================//
        //
        // this_sig_noSpeedReduction : Returns the setting if speed must be reduced on RESTRICTED or STOP_AND_PROCEED
        // returns TRUE if speed reduction must be suppressed
        //

        public bool this_sig_noSpeedReduction(MstsSignalFunction fn_type)
        {
            var sigAsp = MstsSignalAspect.STOP;
            bool setNoReduction = false;

            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == fn_type && sigHead.state >= sigAsp)
                {
                    sigAsp = sigHead.state;
                    if (sigAsp <= MstsSignalAspect.RESTRICTING && sigHead.speed_info != null && sigHead.speed_info[(int)sigAsp] != null)
                    {
                        setNoReduction = sigHead.speed_info[(int)sigAsp].speed_noSpeedReductionOrIsTempSpeedReduction == 1;
                    }
                    else
                    {
                        setNoReduction = false;
                    }
                }
            }
            return setNoReduction;
        }//this_sig_noSpeedReduction

        //================================================================================================//
        //
        // isRestrictedSpeedPost : Returns TRUE if it is a restricted (temp) speedpost
        //

        public int SpeedPostType()
        {
            var sigAsp = MstsSignalAspect.CLEAR_2;
            int speedPostType = 0; // default = standard speedpost

            SignalHead sigHead = SignalHeads.First();

            if (sigHead.speed_info != null && sigHead.speed_info[(int)sigAsp] != null)
            {
                speedPostType = sigHead.speed_info[(int)sigAsp].speed_noSpeedReductionOrIsTempSpeedReduction;

            }
            return speedPostType;

        }//isRestrictedSpeedPost

        //================================================================================================//
        //
        // this_lim_speed : Returns the lowest allowed speed (for speedpost and speed signal)
        //

        public ObjectSpeedInfo this_lim_speed(MstsSignalFunction fn_type)
        {
            var set_speed = new ObjectSpeedInfo(9E9f, 9E9f, false, false, 0);

            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == fn_type)
                {
                    ObjectSpeedInfo this_speed = sigHead.speed_info[(int)sigHead.state];
                    if (this_speed != null)
                    {
                        if (this_speed.speed_pass > 0 && this_speed.speed_pass < set_speed.speed_pass)
                        {
                            set_speed.speed_pass = this_speed.speed_pass;
                            set_speed.speed_flag = 0;
                            set_speed.speed_reset = 0;
                            if (!isSignal) set_speed.speed_noSpeedReductionOrIsTempSpeedReduction = this_speed.speed_noSpeedReductionOrIsTempSpeedReduction;
                        }

                        if (this_speed.speed_freight > 0 && this_speed.speed_freight < set_speed.speed_freight)
                        {
                            set_speed.speed_freight = this_speed.speed_freight;
                            set_speed.speed_flag = 0;
                            set_speed.speed_reset = 0;
                            if (!isSignal) set_speed.speed_noSpeedReductionOrIsTempSpeedReduction = this_speed.speed_noSpeedReductionOrIsTempSpeedReduction;
                        }
                    }

                }
            }

            if (set_speed.speed_pass > 1E9f)
                set_speed.speed_pass = -1;
            if (set_speed.speed_freight > 1E9f)
                set_speed.speed_freight = -1;

            return set_speed;
        }//this_lim_speed

        //================================================================================================//
        //
        // route_set : check if required route is set
        //

        public bool route_set(int req_mainnode, uint req_jnnode)
        {
            bool routeset = false;
            bool retry = false;

            // if signal is enabled for a train, check if required section is in train route path

            if (enabledTrain != null && !MPManager.IsMultiPlayer())
            {
                Train.TCSubpathRoute RoutePart = enabledTrain.Train.ValidRoute[enabledTrain.TrainRouteDirectionIndex];

                TrackNode thisNode = signalRef.trackDB.TrackNodes[req_mainnode];
                for (int iSection = 0; iSection <= thisNode.TCCrossReference.Count - 1 && !routeset; iSection++)
                {
                    int sectionIndex = thisNode.TCCrossReference[iSection].Index;

                    for (int iElement = 0; iElement < RoutePart.Count && !routeset; iElement++)
                    {
                        routeset = (sectionIndex == RoutePart[iElement].TCSectionIndex && signalRef.TrackCircuitList[sectionIndex].CircuitType == TrackCircuitSection.TrackCircuitType.Normal);
                    }
                }

                // if not found in trainroute, try signalroute

                if (!routeset && signalRoute != null)
                {
                    for (int iElement = 0; iElement <= signalRoute.Count - 1 && !routeset; iElement++)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[signalRoute[iElement].TCSectionIndex];
                        routeset = (thisSection.OriginalIndex == req_mainnode && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal);
                    }
                }
                retry = !routeset;
            }


            // not enabled, follow set route but only if not normal signal (normal signal will not clear if not enabled)
            // also, for normal enabled signals - try and follow pins (required node may be beyond present route)

            if (retry || !isSignalNormal() || MPManager.IsMultiPlayer())
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[TCReference];
                int curDirection = TCDirection;
                int newDirection = 0;
                int sectionIndex = -1;
                bool passedTrackJn = false;

                List<int> passedSections = new List<int>();
                passedSections.Add(thisSection.Index);

                routeset = (req_mainnode == thisSection.OriginalIndex);
                while (!routeset && thisSection != null)
                {
                    if (thisSection.ActivePins[curDirection, 0].Link >= 0)
                    {
                        newDirection = thisSection.ActivePins[curDirection, 0].Direction;
                        sectionIndex = thisSection.ActivePins[curDirection, 0].Link;
                    }
                    else
                    {
                        newDirection = thisSection.ActivePins[curDirection, 1].Direction;
                        sectionIndex = thisSection.ActivePins[curDirection, 1].Link;
                    }

                    // if Junction, if active pins not set use selected route
                    if (sectionIndex < 0 && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction)
                    {
                        // check if this is required junction
                        if (Convert.ToUInt32(thisSection.Index) == req_jnnode)
                        {
                            passedTrackJn = true;
                        }
                        // break if passed required junction
                        else if (passedTrackJn)
                        {
                            break;
                        }

                        if (thisSection.ActivePins[1, 0].Link == -1 && thisSection.ActivePins[1, 1].Link == -1)
                        {
                            int selectedDirection = signalRef.trackDB.TrackNodes[thisSection.OriginalIndex].TrJunctionNode.SelectedRoute;
                            newDirection = thisSection.Pins[1, selectedDirection].Direction;
                            sectionIndex = thisSection.Pins[1, selectedDirection].Link;
                        }
                    }

                    // if NORMAL, if active pins not set use default pins
                    if (sectionIndex < 0 && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal)
                    {
                        newDirection = thisSection.Pins[curDirection, 0].Direction;
                        sectionIndex = thisSection.Pins[curDirection, 0].Link;
                    }

                    // check for loop
                    if (passedSections.Contains(sectionIndex))
                    {
                        thisSection = null;  // route is looped - exit
                    }

                    // next section
                    else if (sectionIndex >= 0)
                    {
                        passedSections.Add(sectionIndex);
                        thisSection = signalRef.TrackCircuitList[sectionIndex];
                        curDirection = newDirection;
                        routeset = (req_mainnode == thisSection.OriginalIndex && thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Normal);
                    }

                    // no next section
                    else
                    {
                        thisSection = null;
                    }
                }
            }

            return (routeset);
        }

        //================================================================================================//
        //
        // Find next signal of specified type along set sections - not for NORMAL signals
        //

        public int SONextSignal(MstsSignalFunction fntype)
        {
            int thisTC = TCReference;
            int direction = TCDirection;
            int signalFound = -1;
            TrackCircuitSection thisSection = null;
            bool sectionSet = false;

            // for normal signals

            if (fntype == MstsSignalFunction.NORMAL)
            {
                if (isSignalNormal())        // if this signal is normal : cannot be done using this route (set through sigfound variable)
                    return (-1);
                signalFound = SONextSignalNormal(TCReference);   // other types of signals (sigfound not used)
            }

        // for other signals : move to next TC (signal would have been default if within same section)

            else
            {
                thisSection = signalRef.TrackCircuitList[thisTC];
                sectionSet = enabledTrain == null ? false : thisSection.IsSet(enabledTrain, false);

                if (sectionSet)
                {
                    int pinIndex = direction;
                    thisTC = thisSection.ActivePins[pinIndex, 0].Link;
                    direction = thisSection.ActivePins[pinIndex, 0].Direction;
                }
            }

            // loop through valid sections

            while (sectionSet && thisTC > 0 && signalFound < 0)
            {
                thisSection = signalRef.TrackCircuitList[thisTC];

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                    thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    if (!JunctionsPassed.Contains(thisTC))
                        JunctionsPassed.Add(thisTC);  // set reference to junction section
                    if (!thisSection.SignalsPassingRoutes.Contains(thisRef))
                        thisSection.SignalsPassingRoutes.Add(thisRef);
                }

                // check if required type of signal is along this section

                TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[direction, (int)fntype];
                if (thisList.TrackCircuitItem.Count > 0)
                {
                    signalFound = thisList.TrackCircuitItem[0].SignalRef.thisRef;
                }

                // get next section if active link is set

                if (signalFound < 0)
                {
                    int pinIndex = direction;
                    sectionSet = thisSection.IsSet(enabledTrain, false);
                    if (sectionSet)
                    {
                        thisTC = thisSection.ActivePins[pinIndex, 0].Link;
                        direction = thisSection.ActivePins[pinIndex, 0].Direction;
                        if (thisTC == -1)
                        {
                            thisTC = thisSection.ActivePins[pinIndex, 1].Link;
                            direction = thisSection.ActivePins[pinIndex, 1].Direction;
                        }
                    }
                }
            }

            if (signalFound < 0 && signalRoute != null) // if signal not found following switches use signal route
            {
                for (int iSection = 0; iSection <= (signalRoute.Count - 1) && signalFound < 0; iSection++)
                {
                    thisSection = signalRef.TrackCircuitList[signalRoute[iSection].TCSectionIndex];
                    direction = signalRoute[iSection].Direction;
                    TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[direction, (int)fntype];
                    if (thisList.TrackCircuitItem.Count > 0)
                    {
                        signalFound = thisList.TrackCircuitItem[0].SignalRef.thisRef;
                    }
                }
            }

            return (signalFound);
        }

        //================================================================================================//
        //
        // Find next signal of specified type along set sections - NORMAL signals ONLY
        //

        private int SONextSignalNormal(int thisTC)
        {
            int direction = TCDirection;
            int signalFound = -1;
            TrackCircuitSection thisSection = null;

            int pinIndex = direction;

            if (thisTC < 0)
            {
                thisTC = TCReference;
                thisSection = signalRef.TrackCircuitList[thisTC];
                pinIndex = direction;
                thisTC = thisSection.ActivePins[pinIndex, 0].Link;
                direction = thisSection.ActivePins[pinIndex, 0].Direction;
            }

            // loop through valid sections

            while (thisTC > 0 && signalFound < 0)
            {
                thisSection = signalRef.TrackCircuitList[thisTC];

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                    thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    if (!JunctionsPassed.Contains(thisTC))
                        JunctionsPassed.Add(thisTC);  // set reference to junction section
                    if (!thisSection.SignalsPassingRoutes.Contains(thisRef))
                        thisSection.SignalsPassingRoutes.Add(thisRef);
                }

                // check if normal signal is along this section

                if (thisSection.EndSignals[direction] != null)
                {
                    signalFound = thisSection.EndSignals[direction].thisRef;
                }

                // get next section if active link is set

                if (signalFound < 0)
                {
                    pinIndex = direction;
                    thisTC = thisSection.ActivePins[pinIndex, 0].Link;
                    direction = thisSection.ActivePins[pinIndex, 0].Direction;
                    if (thisTC == -1)
                    {
                        thisTC = thisSection.ActivePins[pinIndex, 1].Link;
                        direction = thisSection.ActivePins[pinIndex, 1].Direction;
                    }

                    // if no active link but signal has route allocated, use train route to find next section

                    if (thisTC == -1 && signalRoute != null)
                    {
                        int thisIndex = signalRoute.GetRouteIndex(thisSection.Index, 0);
                        if (thisIndex >= 0 && thisIndex <= signalRoute.Count - 2)
                        {
                            thisTC = signalRoute[thisIndex + 1].TCSectionIndex;
                            direction = signalRoute[thisIndex + 1].Direction;
                        }
                    }
                }
            }

            return (signalFound);
        }

        //================================================================================================//
        //
        // SONextSignalOpp : find next signal in opp direction
        //

        public int SONextSignalOpp(MstsSignalFunction fntype)
        {
            int thisTC = TCReference;
            int direction = TCDirection == 0 ? 1 : 0;    // reverse direction
            int signalFound = -1;

            TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisTC];
            bool sectionSet = enabledTrain == null ? false : thisSection.IsSet(enabledTrain, false);

            // loop through valid sections

            while (sectionSet && thisTC > 0 && signalFound < 0)
            {
                thisSection = signalRef.TrackCircuitList[thisTC];

                if (thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Junction ||
                    thisSection.CircuitType == TrackCircuitSection.TrackCircuitType.Crossover)
                {
                    if (!JunctionsPassed.Contains(thisTC))
                        JunctionsPassed.Add(thisTC);  // set reference to junction section
                    if (!thisSection.SignalsPassingRoutes.Contains(thisRef))
                        thisSection.SignalsPassingRoutes.Add(thisRef);
                }

                // check if required type of signal is along this section

                if (fntype == MstsSignalFunction.NORMAL)
                {
                    signalFound = thisSection.EndSignals[direction] != null ? thisSection.EndSignals[direction].thisRef : -1;
                }
                else
                {
                    TrackCircuitSignalList thisList = thisSection.CircuitItems.TrackCircuitSignals[direction, (int)fntype];
                    if (thisList.TrackCircuitItem.Count > 0)
                    {
                        signalFound = thisList.TrackCircuitItem[0].SignalRef.thisRef;
                    }
                }

                // get next section if active link is set

                if (signalFound < 0)
                {
                    int pinIndex = direction;
                    sectionSet = thisSection.IsSet(enabledTrain, false);
                    if (sectionSet)
                    {
                        thisTC = thisSection.ActivePins[pinIndex, 0].Link;
                        direction = thisSection.ActivePins[pinIndex, 0].Direction;
                        if (thisTC == -1)
                        {
                            thisTC = thisSection.ActivePins[pinIndex, 1].Link;
                            direction = thisSection.ActivePins[pinIndex, 1].Direction;
                        }
                    }
                }
            }

            return (signalFound);
        }

        //================================================================================================//
        //
        // Update : perform route check and state update
        //

        public void Update()
        {
            // perform route update for normal signals if enabled

            if (isSignalNormal())
            {
                // if in hold, set to most restrictive for each head

                if (holdState != HoldState.None)
                {
                    foreach (SignalHead sigHead in SignalHeads)
                    {
                        if (holdState == HoldState.ManualLock || holdState == HoldState.StationStop) sigHead.SetMostRestrictiveAspect();
                    }
                    return;
                }

                // if enabled - perform full update and propagate if not yet done

                if (enabledTrain != null)
                {
                    // if internal state is not reserved (route fully claimed), perform route check

                    if (internalBlockState != InternalBlockstate.Reserved)
                    {
                        checkRouteState(isPropagated, signalRoute, enabledTrain);
                    }

                    // propagate request

                    if (!isPropagated)
                    {
                        propagateRequest();
                    }

                    StateUpdate();

                    // propagate request if not yet done

                    if (!propagated && enabledTrain != null)
                    {
                        propagateRequest();
                    }
                }

        // fixed route - check route and update

                else if (hasFixedRoute)
                {
                    // if internal state is not reserved (route fully claimed), perform route check

                    if (internalBlockState != InternalBlockstate.Reserved)
                    {
                        checkRouteState(true, fixedRoute, null);
                    }

                    StateUpdate();

                }

        // no route - perform update only

                else
                {
                    StateUpdate();
                }

            }

        // check blockstate for other signals

            else
            {
                getBlockState_notRouted();
                StateUpdate();
            }
        }

        //================================================================================================//
        //
        // fully reset signal as train has passed
        //

        public void resetSignalEnabled()
        {

            // reset train information

            enabledTrain = null;
            trainRouteDirectionIndex = 0;
            signalRoute.Clear();
            fullRoute = hasFixedRoute;
            thisTrainRouteIndex = -1;

            isPropagated = false;
            propagated = false;
            ForcePropagation = false;
            ApproachControlCleared = false;

            // reset block state to most restrictive

            internalBlockState = InternalBlockstate.Blocked;

            // reset next signal information to default

            for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
            {
                sigfound[fntype] = defaultNextSignal[fntype];
            }

            foreach (int thisSectionIndex in JunctionsPassed)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                thisSection.SignalsPassingRoutes.Remove(thisRef);
            }

            // reset permission //

            hasPermission = Permission.Denied;

            StateUpdate();
        }

        //================================================================================================//
        //
        // StateUpdate : Perform the update for each head on this signal to determine state of signal.
        //

        public void StateUpdate()
        {
            // update all normal heads first

            if (MPManager.IsMultiPlayer())
            {
                if (MPManager.IsClient()) return; //client won't handle signal update

                //if there were hold manually, will not update
                if (holdState == HoldState.ManualApproach || holdState == HoldState.ManualLock || holdState == HoldState.ManualPass) return;
            }

            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction == MstsSignalFunction.NORMAL)
                    sigHead.Update();
            }

            // next, update all other heads

            foreach (SignalHead sigHead in SignalHeads)
            {
                if (sigHead.sigFunction != MstsSignalFunction.NORMAL)
                    sigHead.Update();
            }

        } // Update

        //================================================================================================//
        //
        // DistanceTo : Returns the distance from the TDBtraveller to this signal. 
        //

        public float DistanceTo(Traveller tdbTraveller)
        {
            int trItem = trackNodes[trackNode].TrVectorNode.TrItemRefs[trRefIndex];
            return tdbTraveller.DistanceTo(trItems[trItem].TileX, trItems[trItem].TileZ, trItems[trItem].X, trItems[trItem].Y, trItems[trItem].Z);
        }//DistanceTo

        //================================================================================================//
        //
        // ObjectDistance : Returns the distance from this object to the next object
        //

        public float ObjectDistance(SignalObject nextObject)
        {
            int nextTrItem = trackNodes[nextObject.trackNode].TrVectorNode.TrItemRefs[nextObject.trRefIndex];
            return this.tdbtraveller.DistanceTo(
                                    trItems[nextTrItem].TileX, trItems[nextTrItem].TileZ,
                                    trItems[nextTrItem].X, trItems[nextTrItem].Y, trItems[nextTrItem].Z);
        }//ObjectDistance

        //================================================================================================//
        //
        // isSignalHead : Check Whether signal head is for this signal.
        //

        public bool isSignalHead(SignalItem signalItem)
        {
            // Tritem for this signal
            SignalItem thisSignalItem = (SignalItem)trItems[this.trItem];
            // Same Tile
            if (signalItem.TileX == thisSignalItem.TileX && signalItem.TileZ == thisSignalItem.TileZ)
            {
                // Same position
                if ((Math.Abs(signalItem.X - thisSignalItem.X) < 0.01) &&
                    (Math.Abs(signalItem.Y - thisSignalItem.Y) < 0.01) &&
                    (Math.Abs(signalItem.Z - thisSignalItem.Z) < 0.01))
                {
                    return true;
                }
            }
            return false;
        }//isSignalHead

        //================================================================================================//
        //
        // AddHead : Adds a head to this signal.
        //

        public void AddHead(int trItem, int TDBRef, SignalItem sigItem)
        {
            // create SignalHead
            SignalHead head = new SignalHead(this, trItem, TDBRef, sigItem);

            // set junction link
            if (head.TrackJunctionNode != 0)
            {
                if (head.JunctionPath == 0)
                {
                    head.JunctionMainNode =
                       trackNodes[head.TrackJunctionNode].TrPins[trackNodes[head.TrackJunctionNode].Inpins].Link;
                }
                else
                {
                    head.JunctionMainNode =
                       trackNodes[head.TrackJunctionNode].TrPins[trackNodes[head.TrackJunctionNode].Inpins + 1].Link;
                }
            }
            SignalHeads.Add(head);

        }//AddHead (signal)

        //================================================================================================//
        //
        // AddHead : Adds a head to this signal (for speedpost).
        //

        public void AddHead(int trItem, int TDBRef, SpeedPostItem speedItem)
        {
            // create SignalHead
            SignalHead head = new SignalHead(this, trItem, TDBRef, speedItem);
            SignalHeads.Add(head);

        }//AddHead (speedpost)

        //================================================================================================//
        //
        // SetSignalType : Sets the signal type from the sigcfg file for each signal head.
        //

        public void SetSignalType(SignalConfigurationFile sigCFG)
        {
            foreach (SignalHead sigHead in SignalHeads)
            {
                sigHead.SetSignalType(trItems, sigCFG);
            }
        }//SetSignalType

        //================================================================================================//
        //
        // TranslateTMAspect : Gets the display aspect for the track monitor.
        //

        public TrackMonitorSignalAspect TranslateTMAspect(MstsSignalAspect SigState)
        {
            switch (SigState)
            {
                case MstsSignalAspect.STOP:
                    if (hasPermission == Permission.Granted)
                        return TrackMonitorSignalAspect.Permission;
                    else
                        return TrackMonitorSignalAspect.Stop;
                case MstsSignalAspect.STOP_AND_PROCEED:
                    return TrackMonitorSignalAspect.StopAndProceed;
                case MstsSignalAspect.RESTRICTING:
                    return TrackMonitorSignalAspect.Restricted;
                case MstsSignalAspect.APPROACH_1:
                    return TrackMonitorSignalAspect.Approach_1;
                case MstsSignalAspect.APPROACH_2:
                    return TrackMonitorSignalAspect.Approach_2;
                case MstsSignalAspect.APPROACH_3:
                    return TrackMonitorSignalAspect.Approach_3;
                case MstsSignalAspect.CLEAR_1:
                    return TrackMonitorSignalAspect.Clear_1;
                case MstsSignalAspect.CLEAR_2:
                    return TrackMonitorSignalAspect.Clear_2;
                default:
                    return TrackMonitorSignalAspect.None;
            }
        } // GetMonitorAspect

        //================================================================================================//
        //
        // request to clear signal in explorer mode
        //

        public Train.TCSubpathRoute requestClearSignalExplorer(Train.TCSubpathRoute thisRoute,
            float reqDistance, Train.TrainRouted thisTrain, bool propagated, int signalNumClearAhead)
        {
            // build output route from input route
            Train.TCSubpathRoute newRoute = new Train.TCSubpathRoute(thisRoute);

            // if signal has fixed route, use that else build route
            if (fixedRoute != null && fixedRoute.Count > 0)
            {
                signalRoute = new Train.TCSubpathRoute(fixedRoute);
            }

            // build route from signal, upto next signal or max distance, take into account manual switch settings
            else
            {
                List<int> nextRoute = signalRef.ScanRoute(thisTrain.Train, TCNextTC, 0.0f, TCNextDirection, true, reqDistance, true, true, true, false,
                true, false, false, false, false, thisTrain.Train.IsFreight);

                signalRoute = new Train.TCSubpathRoute();

                foreach (int sectionIndex in nextRoute)
                {
                    Train.TCRouteElement thisElement = new Train.TCRouteElement(Math.Abs(sectionIndex), sectionIndex >= 0 ? 0 : 1);
                    signalRoute.Add(thisElement);
                }
            }

            // set full route if route ends with signal
            TrackCircuitSection lastSection = signalRef.TrackCircuitList[signalRoute[signalRoute.Count - 1].TCSectionIndex];
            int lastDirection = signalRoute[signalRoute.Count - 1].Direction;

            if (lastSection.EndSignals[lastDirection] != null)
            {
                fullRoute = true;
                sigfound[(int)MstsSignalFunction.NORMAL] = lastSection.EndSignals[lastDirection].thisRef;
            }

            // try and clear signal

            enabledTrain = thisTrain;
            checkRouteState(propagated, signalRoute, thisTrain);

            // extend route if block is clear or permission is granted, even if signal is not cleared (signal state may depend on next signal)
            bool extendRoute = false;
            if (this_sig_lr(MstsSignalFunction.NORMAL) > MstsSignalAspect.STOP) extendRoute = true;
            if (internalBlockState <= InternalBlockstate.Reservable) extendRoute = true;

            // if signal is cleared or permission is granted, extend route with signal route

            if (extendRoute || hasPermission == Permission.Granted)
            {
                foreach (Train.TCRouteElement thisElement in signalRoute)
                {
                    newRoute.Add(thisElement);
                }
            }

            // if signal is cleared, propagate request if required
            if (extendRoute && fullRoute)
            {
                isPropagated = propagated;
                int ReqNumClearAhead = 0;

                if (SignalNumClearAhead_MSTS > -2)
                {
                    ReqNumClearAhead = propagated ?
                        signalNumClearAhead - SignalNumNormalHeads : SignalNumClearAhead_MSTS - SignalNumNormalHeads;
                }
                else
                {
                    if (SignalNumClearAhead_ORTS == -1)
                    {
                        ReqNumClearAhead = propagated ? signalNumClearAhead : 1;
                    }
                    else if (SignalNumClearAhead_ORTS == 0)
                    {
                        ReqNumClearAhead = 0;
                    }
                    else
                    {
                        ReqNumClearAhead = isPropagated ? signalNumClearAhead - 1 : SignalNumClearAhead_ORTS - 1;
                    }
                }


                if (ReqNumClearAhead > 0)
                {
                    int nextSignalIndex = sigfound[(int)MstsSignalFunction.NORMAL];
                    if (nextSignalIndex >= 0)
                    {
                        SignalObject nextSignal = signalObjects[nextSignalIndex];
                        newRoute = nextSignal.requestClearSignalExplorer(newRoute, thisTrain.Train.minCheckDistanceM, thisTrain, true, ReqNumClearAhead);
                    }
                }
            }

            return (newRoute);
        }
        //================================================================================================//
        //
        // request to clear signal
        //

        public bool requestClearSignal(Train.TCSubpathRoute RoutePart, Train.TrainRouted thisTrain,
                        int clearNextSignals, bool requestIsPropagated, SignalObject lastSignal)
        {

#if DEBUG_REPORTS
			File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Request for clear signal from train {0} at section {1} for signal {2}\n",
				thisTrain.Train.Number,
				thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex,
				thisRef));
#endif
            if (thisTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Request for clear signal from train {0} at section {1} for signal {2}\n",
                    thisTrain.Train.Number,
                    thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex,
                    thisRef));
            }

            // set general variables
            int procstate = 0;
            int foundFirstSection = -1;
            int foundLastSection = -1;
            SignalObject nextSignal = null;

            isPropagated = requestIsPropagated;
            propagated = false;   // always pass on request

            // check if signal not yet enabled - if it is, give warning and quit

            // check if signal not yet enabled - if it is, give warning, reset signal and set both trains to node control, and quit

            if (enabledTrain != null && enabledTrain != thisTrain)
            {
                Trace.TraceWarning("Request to clear signal {0} from train {1}, signal already enabled for train {2}",
                                       thisRef, thisTrain.Train.Number, enabledTrain.Train.Number);
                Train.TrainRouted otherTrain = enabledTrain;
                ResetSignal(true);
                int routeListIndex = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].RouteListIndex;
                signalRef.BreakDownRouteList(thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex], routeListIndex, thisTrain);
                routeListIndex = otherTrain.Train.PresentPosition[otherTrain.TrainRouteDirectionIndex].RouteListIndex;
                signalRef.BreakDownRouteList(otherTrain.Train.ValidRoute[otherTrain.TrainRouteDirectionIndex], routeListIndex, otherTrain);

                thisTrain.Train.SwitchToNodeControl(thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].TCSectionIndex);
                if (otherTrain.Train.ControlMode != Train.TRAIN_CONTROL.EXPLORER && !otherTrain.Train.IsPathless) otherTrain.Train.SwitchToNodeControl(otherTrain.Train.PresentPosition[otherTrain.TrainRouteDirectionIndex].TCSectionIndex);
                procstate = -1;
                return false;
            }
            if (thisTrain.Train.TCRoute != null && HasLockForTrain(thisTrain.Train.Number, thisTrain.Train.TCRoute.activeSubpath))
            {
                return false;
            }
            if (enabledTrain != thisTrain) // new allocation - reset next signals
            {
                for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
                {
                    sigfound[fntype] = defaultNextSignal[fntype];
                }
            }
            enabledTrain = thisTrain;

            // find section in route part which follows signal

            if (procstate == 0)
            {
                signalRoute.Clear();

                int firstIndex = -1;
                if (lastSignal != null)
                {
                    firstIndex = lastSignal.thisTrainRouteIndex;
                }
                if (firstIndex < 0)
                {
                    firstIndex = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex].RouteListIndex;
                }

                if (firstIndex >= 0)
                {
                    for (int iNode = firstIndex;
                             iNode < RoutePart.Count && foundFirstSection < 0;
                             iNode++)
                    {
                        Train.TCRouteElement thisElement = RoutePart[iNode];
                        if (thisElement.TCSectionIndex == TCNextTC)
                        {
                            foundFirstSection = iNode;
                            thisTrainRouteIndex = iNode;
                        }
                    }
                }

                if (foundFirstSection < 0)
                {
                    // no route from this signal - reset enable and exit
                    enabledTrain = null;
                    // if signal on holding list, set hold state
                    if (thisTrain.Train.HoldingSignals.Contains(thisRef) && holdState == HoldState.None) holdState = HoldState.StationStop;
                    return false;
                }
            }

            // copy sections upto next normal signal
            // check for loop


            if (procstate == 0)
            {
                List<int> sectionsInRoute = new List<int>();

                for (int iNode = foundFirstSection; iNode < RoutePart.Count && foundLastSection < 0; iNode++)
                {
                    Train.TCRouteElement thisElement = RoutePart[iNode];
                    if (sectionsInRoute.Contains(thisElement.TCSectionIndex))
                    {
                        foundLastSection = iNode;  // loop
                    }
                    else
                    {
                        signalRoute.Add(thisElement);
                        sectionsInRoute.Add(thisElement.TCSectionIndex);

                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                        if (thisSection.EndSignals[thisElement.Direction] != null)
                        {
                            foundLastSection = iNode;
                            nextSignal = thisSection.EndSignals[thisElement.Direction];
                        }
                    }
                }
            }

            // check if signal has route, is enabled, request is by enabled train and train is not occupying sections in signal route

            if (enabledTrain != null && enabledTrain == thisTrain && signalRoute != null && signalRoute.Count > 0)
            {
                foreach (Train.TCRouteElement routeElement in signalRoute)
                {
                    TrackCircuitSection routeSection = signalRef.TrackCircuitList[routeElement.TCSectionIndex];
                    if (routeSection.CircuitState.ThisTrainOccupying(thisTrain))
                    {
                        return false;  // train has passed signal - clear request is invalid
                    }
                }
            }

            // check if end of track reached

            Train.TCRouteElement lastSignalElement = signalRoute[signalRoute.Count - 1];
            TrackCircuitSection lastSignalSection = signalRef.TrackCircuitList[lastSignalElement.TCSectionIndex];

            fullRoute = true;

            // if end of signal route is not a signal or end-of-track it is not a full route

            if (nextSignal == null && lastSignalSection.CircuitType != TrackCircuitSection.TrackCircuitType.EndOfTrack)
            {
                fullRoute = false;
            }

            // if next signal is found and relevant, set reference

            if (nextSignal != null)
            {
                sigfound[(int)MstsSignalFunction.NORMAL] = nextSignal.thisRef;
            }
            else
            {
                sigfound[(int)MstsSignalFunction.NORMAL] = -1;
            }

            // set number of signals to clear ahead

            if (SignalNumClearAhead_MSTS > -2)
            {
                ReqNumClearAhead = clearNextSignals > 0 ?
                    clearNextSignals - SignalNumNormalHeads : SignalNumClearAhead_MSTS - SignalNumNormalHeads;
            }
            else
            {
                if (SignalNumClearAhead_ORTS == -1)
                {
                    ReqNumClearAhead = clearNextSignals > 0 ? clearNextSignals : 1;
                }
                else if (SignalNumClearAhead_ORTS == 0)
                {
                    ReqNumClearAhead = 0;
                }
                else
                {
                    ReqNumClearAhead = clearNextSignals > 0 ? clearNextSignals - 1 : SignalNumClearAhead_ORTS - 1;
                }
            }

            // perform route check

            checkRouteState(isPropagated, signalRoute, thisTrain);

            // propagate request

            if (!isPropagated && enabledTrain != null)
            {
                propagateRequest();
            }
            if (thisTrain != null && thisTrain.Train is AITrain && thisTrain.Train.SpeedMpS == 0)
            {
                WorldLocation location = this.tdbtraveller.WorldLocation;
                ((AITrain)thisTrain.Train).AuxActionsContain.CheckGenActions(this.GetType(), location, 0f, 0f, this.tdbtraveller.TrackNodeIndex);
            }
            if (this_sig_mr(MstsSignalFunction.NORMAL) != MstsSignalAspect.STOP)
                return true;
            else
                return false;
        }

        //================================================================================================//
        //
        // check and update Route State
        //

        public void checkRouteState(bool isPropagated, Train.TCSubpathRoute thisRoute, Train.TrainRouted thisTrain, bool sound = true)
        {
            // check if signal must be hold
            bool signalHold = (holdState != HoldState.None);
            if (enabledTrain != null && enabledTrain.Train.HoldingSignals.Contains(thisRef) && holdState < HoldState.ManualLock)
            {
                holdState = HoldState.StationStop;
                signalHold = true;
            }
            else if (holdState == HoldState.StationStop)
            {
                if (enabledTrain == null || !enabledTrain.Train.HoldingSignals.Contains(thisRef))
                {
                    holdState = HoldState.None;
                    signalHold = false;
                }
            }

            // check if signal has route, is enabled, request is by enabled train and train is not occupying sections in signal route

            if (enabledTrain != null && enabledTrain == thisTrain && signalRoute != null && signalRoute.Count > 0)
            {
                foreach (Train.TCRouteElement routeElement in signalRoute)
                {
                    TrackCircuitSection routeSection = signalRef.TrackCircuitList[routeElement.TCSectionIndex];
                    if (routeSection.CircuitState.ThisTrainOccupying(thisTrain))
                    {
                        return;  // train has passed signal - clear request is invalid
                    }
                }
            }

            // test if propage state still correct - if next signal for enabled train is this signal, it is not propagated

            if (enabledTrain != null && enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex] != null &&
                enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef == thisRef)
            {
                isPropagated = false;
            }

            // test clearance for full route section

            if (!signalHold)
            {
                if (fullRoute)
                {
                    bool newroute = getBlockState(thisRoute, thisTrain, !sound);
                    if (newroute)
                        thisRoute = this.signalRoute;
                }

                // test clearance for sections in route only if first signal ahead of train

                else if (enabledTrain != null && !isPropagated)
                {
                    getPartBlockState(thisRoute);
                }

                // test clearance for sections in route if signal is second signal ahead of train, first signal route is clear but first signal is still showing STOP
                // case for double-hold signals

                else if (enabledTrain != null && isPropagated)
                {
                    SignalObject firstSignal = enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex];
                    if (firstSignal != null &&
                        firstSignal.sigfound[(int)MstsSignalFunction.NORMAL] == thisRef &&
                        firstSignal.internalBlockState <= InternalBlockstate.Reservable &&
                        firstSignal.this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP)
                    {
                        getPartBlockState(thisRoute);
                    }
                }
            }

            // else consider route blocked

            else
            {
                internalBlockState = InternalBlockstate.Blocked;
            }

            // derive signal state

            StateUpdate();
            MstsSignalAspect signalState = this_sig_lr(MstsSignalFunction.NORMAL);

            float lengthReserved = 0.0f;

            // check for permission

            if (internalBlockState == InternalBlockstate.OccupiedSameDirection && hasPermission == Permission.Requested && !isPropagated)
            {
                hasPermission = Permission.Granted;
                if (sound) signalRef.Simulator.SoundNotify = Event.PermissionGranted;
            }
            else
            {
                if (enabledTrain != null && enabledTrain.Train.ControlMode == Train.TRAIN_CONTROL.MANUAL &&
                    internalBlockState <= InternalBlockstate.OccupiedSameDirection && hasPermission == Permission.Requested)
                {
                    signalRef.Simulator.SoundNotify = Event.PermissionGranted;
                }
                else if (hasPermission == Permission.Requested)
                {
                    if (sound) signalRef.Simulator.SoundNotify = Event.PermissionDenied;
                }

                if (enabledTrain != null && enabledTrain.Train.ControlMode == Train.TRAIN_CONTROL.MANUAL && signalState == MstsSignalAspect.STOP &&
                internalBlockState <= InternalBlockstate.OccupiedSameDirection && hasPermission == Permission.Requested)
                {
                    hasPermission = Permission.Granted;
                }
                else if (hasPermission == Permission.Requested)
                {
                    hasPermission = Permission.Denied;
                }
            }

            // reserve full section if allowed

            if (enabledTrain != null)
            {
                if (internalBlockState == InternalBlockstate.Reservable)
                {
                    internalBlockState = InternalBlockstate.Reserved; // preset all sections are reserved

                    foreach (Train.TCRouteElement thisElement in thisRoute)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        if (thisSection.CircuitState.TrainReserved != null || thisSection.CircuitState.TrainOccupy.Count > 0)
                        {
                            internalBlockState = InternalBlockstate.Reservable; // not all sections are reserved // 
                            break;
                        }
                        thisSection.Reserve(enabledTrain, thisRoute);
                        enabledTrain.Train.LastReservedSection[enabledTrain.TrainRouteDirectionIndex] = thisElement.TCSectionIndex;
                        lengthReserved += thisSection.Length;
                    }

                    enabledTrain.Train.ClaimState = false;
                }

            // reserve partial sections if signal clears on occupied track or permission is granted

                else if ((signalState > MstsSignalAspect.STOP || hasPermission == Permission.Granted) &&
                         (internalBlockState != InternalBlockstate.Reserved && internalBlockState < InternalBlockstate.ReservedOther))
                {

                    // reserve upto available section

                    int lastSectionIndex = 0;
                    bool reservable = true;

                    for (int iSection = 0; iSection < thisRoute.Count && reservable; iSection++)
                    {
                        Train.TCRouteElement thisElement = thisRoute[iSection];
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                        if (thisSection.IsAvailable(enabledTrain))
                        {
                            if (thisSection.CircuitState.TrainReserved == null)
                            {
                                thisSection.Reserve(enabledTrain, thisRoute);
                            }
                            enabledTrain.Train.LastReservedSection[enabledTrain.TrainRouteDirectionIndex] = thisElement.TCSectionIndex;
                            lastSectionIndex = iSection;
                            lengthReserved += thisSection.Length;
                        }
                        else
                        {
                            reservable = false;
                        }
                    }

                    // set pre-reserved or reserved for all other sections

                    for (int iSection = lastSectionIndex++; iSection < thisRoute.Count && reservable; iSection++)
                    {
                        Train.TCRouteElement thisElement = thisRoute[iSection];
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];

                        if (thisSection.IsAvailable(enabledTrain) && thisSection.CircuitState.TrainReserved == null)
                        {
                            thisSection.Reserve(enabledTrain, thisRoute);
                        }
                        else if (thisSection.CircuitState.HasOtherTrainsOccupying(enabledTrain))
                        {
                            thisSection.PreReserve(enabledTrain);
                        }
                        else if (thisSection.CircuitState.TrainReserved == null || thisSection.CircuitState.TrainReserved.Train != enabledTrain.Train)
                        {
                            thisSection.PreReserve(enabledTrain);
                        }
                        else
                        {
                            reservable = false;
                        }
                    }
                    enabledTrain.Train.ClaimState = false;
                }

            // if claim allowed - reserve free sections and claim all other if first signal ahead of train

                else if (enabledTrain.Train.ClaimState && internalBlockState != InternalBlockstate.Reserved &&
                         enabledTrain.Train.NextSignalObject[0] != null && enabledTrain.Train.NextSignalObject[0].thisRef == thisRef)
                {
                    foreach (Train.TCRouteElement thisElement in thisRoute)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        if (thisSection.DeadlockReference > 0) // do not claim into deadlock area as path may not have been resolved
                        {
                            break;
                        }

                        if (thisSection.CircuitState.TrainReserved == null || (thisSection.CircuitState.TrainReserved.Train != enabledTrain.Train))
                        {
                            // deadlock has been set since signal request was issued - reject claim, break and reset claimstate
                            if (thisSection.DeadlockTraps.ContainsKey(thisTrain.Train.Number))
                            {
                                thisTrain.Train.ClaimState = false;
                                break;
                            }

                            thisSection.Claim(enabledTrain);
                        }
                    }
                }
            }
        }

        //================================================================================================//
        //
        // propagate clearance request
        //

        private void propagateRequest()
        {
            // no. of next signals to clear : as passed on -1 if signal has normal clear ahead
            // if passed on < 0, use this signals num to clear

            SignalObject nextSignal = null;
            if (sigfound[(int)MstsSignalFunction.NORMAL] >= 0)
            {
                nextSignal = signalObjects[sigfound[(int)MstsSignalFunction.NORMAL]];
            }

            Train.TCSubpathRoute RoutePart;
            if (enabledTrain != null)
            {
                RoutePart = enabledTrain.Train.ValidRoute[enabledTrain.TrainRouteDirectionIndex];   // if known which route to use
            }
            else
            {
                RoutePart = signalRoute; // else use signal route
            }

            bool propagateState = true;  // normal propagate state

            // if section is clear but signal remains at stop - dual signal situation - do not treat as propagate
            if (internalBlockState == InternalBlockstate.Reserved && this_sig_lr(MstsSignalFunction.NORMAL) == MstsSignalAspect.STOP && isSignalNormal())
            {
                propagateState = false;
            }

            if ((ReqNumClearAhead > 0 || ForcePropagation) && nextSignal != null && internalBlockState == InternalBlockstate.Reserved)
            {
                nextSignal.requestClearSignal(RoutePart, enabledTrain, ReqNumClearAhead, propagateState, this);
                propagated = true;
                ForcePropagation = false;
            }

        } //propagateRequest

        //================================================================================================//
        //
        // get block state - not routed
        // Check blockstate for normal signal which is not enabled
        // Check blockstate for other types of signals
        //

        private void getBlockState_notRouted()
        {

            InternalBlockstate localBlockState = InternalBlockstate.Reserved; // preset to lowest option

            // check fixed route for normal signals

            if (isSignalNormal() && hasFixedRoute)
            {
                foreach (Train.TCRouteElement thisElement in fixedRoute)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    if (thisSection.CircuitState.HasTrainsOccupying())
                    {
                        localBlockState = InternalBlockstate.OccupiedSameDirection;
                    }
                }
            }

        // otherwise follow sections upto first non-set switch or next signal
            else
            {
                int thisTC = TCReference;
                int direction = TCDirection;
                int nextTC = -1;

                // for normal signals : start at next TC

                if (TCNextTC > 0)
                {
                    thisTC = TCNextTC;
                    direction = TCNextDirection;
                }

                // get trackcircuit

                TrackCircuitSection thisSection = null;
                if (thisTC > 0)
                {
                    thisSection = signalRef.TrackCircuitList[thisTC];
                }

                // loop through valid sections

                while (thisSection != null)
                {

                    // set blockstate

                    if (thisSection.CircuitState.HasTrainsOccupying())
                    {
                        if (thisSection.Index == TCReference)  // for section where signal is placed, check if train is ahead
                        {
                            Dictionary<Train, float> trainAhead =
                                                    thisSection.TestTrainAhead(null, TCOffset, TCDirection);
                            if (trainAhead.Count > 0)
                                localBlockState = InternalBlockstate.OccupiedSameDirection;
                        }
                        else
                        {
                            localBlockState = InternalBlockstate.OccupiedSameDirection;
                        }
                    }

                    // if section has signal at end stop check

                    if (thisSection.EndSignals[direction] != null)
                    {
                        thisSection = null;
                    }

        // get next section if active link is set

                    else
                    {
                        //                     int pinIndex = direction == 0 ? 1 : 0;
                        int pinIndex = direction;
                        nextTC = thisSection.ActivePins[pinIndex, 0].Link;
                        direction = thisSection.ActivePins[pinIndex, 0].Direction;
                        if (nextTC == -1)
                        {
                            nextTC = thisSection.ActivePins[pinIndex, 1].Link;
                            direction = thisSection.ActivePins[pinIndex, 1].Direction;
                        }

                        // set state to blocked if ending at unset or unaligned switch

                        if (nextTC >= 0)
                        {
                            thisSection = signalRef.TrackCircuitList[nextTC];
                        }
                        else
                        {
                            thisSection = null;
                            localBlockState = InternalBlockstate.Blocked;
                        }
                    }
                }
            }

            internalBlockState = localBlockState;
        }

        //================================================================================================//
        //
        // Get block state
        // Get internal state of full block for normal enabled signal upto next signal for clear request
        // returns true if train set to use alternative route
        //

        // check for train
        private bool getBlockState(Train.TCSubpathRoute thisRoute, Train.TrainRouted thisTrain, bool AIPermissionRequest)
        {
            if (signalRef.UseLocationPassingPaths)
            {
                return (getBlockState_locationBased(thisRoute, thisTrain, AIPermissionRequest));
            }
            else
            {
                return (getBlockState_pathBased(thisRoute, thisTrain));
            }
        }

        //================================================================================================//
        //
        // Get block state
        // Get internal state of full block for normal enabled signal upto next signal for clear request
        // returns true if train set to use alternative route
        // based on path-based deadlock processing
        //
        private bool getBlockState_pathBased(Train.TCSubpathRoute thisRoute, Train.TrainRouted thisTrain)
        {
            bool returnvalue = false;

            InternalBlockstate blockstate = InternalBlockstate.Reserved;  // preset to lowest possible state //

            // loop through all sections in route list

            Train.TCRouteElement lastElement = null;

            foreach (Train.TCRouteElement thisElement in thisRoute)
            {
                lastElement = thisElement;
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                int direction = thisElement.Direction;
                blockstate = thisSection.getSectionState(enabledTrain, direction, blockstate, thisRoute, thisRef);
                if (blockstate > InternalBlockstate.Reservable)
                    break;           // break on first non-reservable section //

                // if alternative path from section available but train already waiting for deadlock, set blocked
                if (thisElement.StartAlternativePath != null)
                {
                    TrackCircuitSection endSection = signalRef.TrackCircuitList[thisElement.StartAlternativePath[1]];
                    if (endSection.CheckDeadlockAwaited(thisTrain.Train.Number))
                    {
                        blockstate = InternalBlockstate.Blocked;
                        lastElement = thisElement;
                        break;
                    }
                }
            }

            // check if alternative route available

            int lastElementIndex = thisRoute.GetRouteIndex(lastElement.TCSectionIndex, 0);

            if (blockstate > InternalBlockstate.Reservable && thisTrain != null)
            {
                int startAlternativeRoute = -1;
                int altRoute = -1;

                Train.TCSubpathRoute trainRoute = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex];
                Train.TCPosition thisPosition = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex];

                for (int iElement = lastElementIndex; iElement >= 0; iElement--)
                {
                    Train.TCRouteElement prevElement = thisRoute[iElement];
                    if (prevElement.StartAlternativePath != null)
                    {
                        startAlternativeRoute =
                            trainRoute.GetRouteIndex(thisRoute[iElement].TCSectionIndex, thisPosition.RouteListIndex);
                        altRoute = prevElement.StartAlternativePath[0];
                        break;
                    }
                }

                // check if alternative path may be used

                if (startAlternativeRoute > 0)
                {
                    Train.TCRouteElement startElement = trainRoute[startAlternativeRoute];
                    int endSectionIndex = startElement.StartAlternativePath[1];
                    TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];
                    if (endSection.CheckDeadlockAwaited(thisTrain.Train.Number))
                    {
                        startAlternativeRoute = -1; // reset use of alternative route
                    }
                }

                // if available, select part of route upto next signal

                if (startAlternativeRoute > 0)
                {
                    Train.TCSubpathRoute altRoutePart = thisTrain.Train.ExtractAlternativeRoute_pathBased(altRoute);

                    // check availability of alternative route

                    InternalBlockstate newblockstate = InternalBlockstate.Reservable;

                    foreach (Train.TCRouteElement thisElement in altRoutePart)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        int direction = thisElement.Direction;
                        newblockstate = thisSection.getSectionState(enabledTrain, direction, newblockstate, thisRoute, thisRef);
                        if (newblockstate > InternalBlockstate.Reservable)
                            break;           // break on first non-reservable section //
                    }

                    // if available, use alternative route

                    if (newblockstate <= InternalBlockstate.Reservable)
                    {
                        blockstate = newblockstate;
                        thisTrain.Train.SetAlternativeRoute_pathBased(startAlternativeRoute, altRoute, this);
                        returnvalue = true;
                    }
                }
            }

            // check if approaching deadlock part, and if alternative route must be taken - if point where alt route start is not yet reserved
            // alternative route may not be taken if there is a train already waiting for the deadlock
            else if (thisTrain != null)
            {
                int startAlternativeRoute = -1;
                int altRoute = -1;
                TrackCircuitSection startSection = null;
                TrackCircuitSection endSection = null;

                Train.TCSubpathRoute trainRoute = thisTrain.Train.ValidRoute[thisTrain.TrainRouteDirectionIndex];
                Train.TCPosition thisPosition = thisTrain.Train.PresentPosition[thisTrain.TrainRouteDirectionIndex];

                for (int iElement = lastElementIndex; iElement >= 0; iElement--)
                {
                    Train.TCRouteElement prevElement = thisRoute[iElement];
                    if (prevElement.StartAlternativePath != null)
                    {
                        endSection = signalRef.TrackCircuitList[prevElement.StartAlternativePath[1]];
                        if (endSection.DeadlockTraps.ContainsKey(thisTrain.Train.Number) && !endSection.CheckDeadlockAwaited(thisTrain.Train.Number))
                        {
                            altRoute = prevElement.StartAlternativePath[0];
                            startAlternativeRoute =
                                trainRoute.GetRouteIndex(prevElement.TCSectionIndex, thisPosition.RouteListIndex);
                            startSection = signalRef.TrackCircuitList[prevElement.TCSectionIndex];
                        }
                        break;
                    }
                }

                // use alternative route

                if (startAlternativeRoute > 0 &&
                    (startSection.CircuitState.TrainReserved == null || startSection.CircuitState.TrainReserved.Train != thisTrain.Train))
                {
                    Train.TCSubpathRoute altRoutePart = thisTrain.Train.ExtractAlternativeRoute_pathBased(altRoute);

                    // check availability of alternative route

                    InternalBlockstate newblockstate = InternalBlockstate.Reservable;

                    foreach (Train.TCRouteElement thisElement in altRoutePart)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        int direction = thisElement.Direction;
                        newblockstate = thisSection.getSectionState(enabledTrain, direction, newblockstate, thisRoute, thisRef);
                        if (newblockstate > InternalBlockstate.Reservable)
                            break;           // break on first non-reservable section //
                    }

                    // if available, use alternative route

                    if (newblockstate <= InternalBlockstate.Reservable)
                    {
                        blockstate = newblockstate;
                        thisTrain.Train.SetAlternativeRoute_pathBased(startAlternativeRoute, altRoute, this);
                        if (endSection.DeadlockTraps.ContainsKey(thisTrain.Train.Number) && !endSection.DeadlockAwaited.Contains(thisTrain.Train.Number))
                            endSection.DeadlockAwaited.Add(thisTrain.Train.Number);
                        returnvalue = true;

                    }
                }
            }

            internalBlockState = blockstate;
            return (returnvalue);
        }

        //================================================================================================//
        //
        // Get block state
        // Get internal state of full block for normal enabled signal upto next signal for clear request
        // returns true if train set to use alternative route
        // based on location-based deadlock processing
        //

        private bool getBlockState_locationBased(Train.TCSubpathRoute thisRoute, Train.TrainRouted thisTrain, bool AIPermissionRequest)
        {
            List<int> SectionsWithAlternativePath = new List<int>();
            List<int> SectionsWithAltPathSet = new List<int>();
            bool altRouteAssigned = false;

            bool returnvalue = false;
            bool deadlockArea = false;

            InternalBlockstate blockstate = InternalBlockstate.Reserved;  // preset to lowest possible state //

            // loop through all sections in route list

            Train.TCRouteElement lastElement = null;

            foreach (Train.TCRouteElement thisElement in thisRoute)
            {
                lastElement = thisElement;
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                int direction = thisElement.Direction;

                blockstate = thisSection.getSectionState(enabledTrain, direction, blockstate, thisRoute, thisRef);
                if (blockstate > InternalBlockstate.OccupiedSameDirection)
                    break;     // exit on first none-available section

                // check if section is trigger section for waitany instruction
                if (thisTrain != null)
                {
                    if (thisTrain.Train.CheckAnyWaitCondition(thisSection.Index))
                    {
                        blockstate = InternalBlockstate.Blocked;
                    }
                }

                // check if this section is start of passing path area
                // if so, select which path must be used - but only if cleared by train in AUTO mode

                if (thisSection.DeadlockReference > 0 && thisElement.FacingPoint && thisTrain != null)
                {
                    if (thisTrain.Train.ControlMode == Train.TRAIN_CONTROL.AUTO_NODE || thisTrain.Train.ControlMode == Train.TRAIN_CONTROL.AUTO_SIGNAL)
                    {
                        DeadlockInfo sectionDeadlockInfo = signalRef.DeadlockInfoList[thisSection.DeadlockReference];

                        // if deadlock area and no path yet selected - exit loop; else follow assigned path
                        if (sectionDeadlockInfo.HasTrainAndSubpathIndex(thisTrain.Train.Number, thisTrain.Train.TCRoute.activeSubpath) &&
                            thisElement.UsedAlternativePath < 0)
                        {
                            deadlockArea = true;
                            break; // exits on deadlock area
                        }
                        else
                        {
                            SectionsWithAlternativePath.Add(thisElement.TCSectionIndex);
                            altRouteAssigned = true;
                        }
                    }
                }
                if (thisTrain != null && blockstate == InternalBlockstate.OccupiedSameDirection && (AIPermissionRequest || hasPermission == Permission.Requested)) break;
            }

            // if deadlock area : check alternative path if not yet selected - but only if opening junction is reservable
            // if free alternative path is found, set path available otherwise set path blocked

            if (deadlockArea && lastElement.UsedAlternativePath < 0)
            {
                if (blockstate <= InternalBlockstate.Reservable)
                {

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt",
                    "\n **** Get block state for section " + lastElement.TCSectionIndex.ToString() + " for train : " + thisTrain.Train.Number.ToString() + "\n");
#endif
                    TrackCircuitSection lastSection = signalRef.TrackCircuitList[lastElement.TCSectionIndex];
                    DeadlockInfo sectionDeadlockInfo = signalRef.DeadlockInfoList[lastSection.DeadlockReference];
                    List<int> availableRoutes = sectionDeadlockInfo.CheckDeadlockPathAvailability(lastSection, thisTrain.Train);

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\nReturned no. of available paths : " + availableRoutes.Count.ToString() + "\n");
                File.AppendAllText(@"C:\Temp\deadlock.txt", "****\n\n");
#endif

                    if (availableRoutes.Count >= 1)
                    {
                        int endSectionIndex = -1;
                        int usedRoute = sectionDeadlockInfo.SelectPath(availableRoutes, thisTrain.Train, ref endSectionIndex);
                        lastElement.UsedAlternativePath = usedRoute;
                        SectionsWithAltPathSet.Add(lastElement.TCSectionIndex);
                        altRouteAssigned = true;

                        thisTrain.Train.SetAlternativeRoute_locationBased(lastSection.Index, sectionDeadlockInfo, usedRoute, this);
                        returnvalue = true;
                        blockstate = InternalBlockstate.Reservable;
                    }
                    else
                    {
                        blockstate = InternalBlockstate.Blocked;
                    }
                }
                else
                {
                    blockstate = InternalBlockstate.Blocked;
                }
            }

            internalBlockState = blockstate;

            // reset any alternative route selections if route is not available
            if (altRouteAssigned && blockstate != InternalBlockstate.Reservable && blockstate != InternalBlockstate.Reserved)
            {
                foreach (int SectionNo in SectionsWithAlternativePath)
                {
#if DEBUG_REPORTS
                    Trace.TraceInformation("Train : {0} : state {1} but route already set for section {2}",
                        thisTrain.Train.Name, blockstate, SectionNo);
#endif
                    int routeIndex = thisTrain.Train.ValidRoute[0].GetRouteIndex(SectionNo, thisTrain.Train.PresentPosition[0].RouteListIndex);
                    Train.TCRouteElement thisElement = thisTrain.Train.ValidRoute[0][routeIndex];
                    thisElement.UsedAlternativePath = -1;
                }
                foreach (int SectionNo in SectionsWithAltPathSet)
                {
#if DEBUG_REPORTS
                    Trace.TraceInformation("Train : {0} : state {1} but route now set for section {2}",
                        thisTrain.Train.Name, blockstate, SectionNo);
#endif
                    int routeIndex = thisTrain.Train.ValidRoute[0].GetRouteIndex(SectionNo, thisTrain.Train.PresentPosition[0].RouteListIndex);
                    Train.TCRouteElement thisElement = thisTrain.Train.ValidRoute[0][routeIndex];
                    thisElement.UsedAlternativePath = -1;
                }
            }

            return (returnvalue);
        }

        //================================================================================================//
        //
        // Get part block state
        // Get internal state of part of block for normal enabled signal upto next signal for clear request
        // if there are no switches before next signal or end of track, treat as full block
        //

        private void getPartBlockState(Train.TCSubpathRoute thisRoute)
        {

            // check beyond last section for next signal or end of track 

            int listIndex = (thisRoute.Count > 0) ? (thisRoute.Count - 1) : thisTrainRouteIndex;

            Train.TCRouteElement lastElement = thisRoute[listIndex];
            int thisSectionIndex = lastElement.TCSectionIndex;
            int direction = lastElement.Direction;

            Train.TCSubpathRoute additionalElements = new Train.TCSubpathRoute();

            bool end_of_info = false;

            while (!end_of_info)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];

                TrackCircuitSection.TrackCircuitType thisType = thisSection.CircuitType;

                switch (thisType)
                {
                    case (TrackCircuitSection.TrackCircuitType.EndOfTrack):
                        end_of_info = true;
                        break;

                    case (TrackCircuitSection.TrackCircuitType.Junction):
                    case (TrackCircuitSection.TrackCircuitType.Crossover):
                        end_of_info = true;
                        break;

                    default:
                        Train.TCRouteElement newElement = new Train.TCRouteElement(thisSectionIndex, direction);
                        additionalElements.Add(newElement);

                        if (thisSection.EndSignals[direction] != null)
                        {
                            end_of_info = true;
                        }
                        break;
                }

                if (!end_of_info)
                {
                    thisSectionIndex = thisSection.Pins[direction, 0].Link;
                    direction = thisSection.Pins[direction, 0].Direction;
                }
            }

            InternalBlockstate blockstate = InternalBlockstate.Reserved;  // preset to lowest possible state //

            // check all elements in original route

            foreach (Train.TCRouteElement thisElement in thisRoute)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                direction = thisElement.Direction;
                blockstate = thisSection.getSectionState(enabledTrain, direction, blockstate, thisRoute, thisRef);
                if (blockstate > InternalBlockstate.Reservable)
                    break;           // break on first non-reservable section //
            }

            // check all additional elements upto signal, junction or end-of-track

            if (blockstate <= InternalBlockstate.Reservable)
            {
                foreach (Train.TCRouteElement thisElement in additionalElements)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                    direction = thisElement.Direction;
                    blockstate = thisSection.getSectionState(enabledTrain, direction, blockstate, additionalElements, thisRef);
                    if (blockstate > InternalBlockstate.Reservable)
                        break;           // break on first non-reservable section //
                }
            }

            //          if (blockstate <= INTERNAL_BLOCKSTATE.RESERVABLE && end_at_junction)
            //          {
            //              blockstate = INTERNAL_BLOCKSTATE.OCCUPIED_SAMEDIR;  // set restricted state
            //          }

            internalBlockState = blockstate;

        }

        //================================================================================================//
        //
        // Set signal default route and next signal list as switch in route is reset
        // Used in manual mode for signals which clear by default
        //

        public void SetDefaultRoute()
        {
            signalRoute = new Train.TCSubpathRoute(fixedRoute);
            for (int iSigtype = 0; iSigtype <= defaultNextSignal.Length - 1; iSigtype++)
            {
                sigfound[iSigtype] = defaultNextSignal[iSigtype];
            }
        }

        //================================================================================================//
        //
        // Reset signal and clear all train sections
        //

        public void ResetSignal(bool propagateReset)
        {
            Train.TrainRouted thisTrain = enabledTrain;

            // search for last signal enabled for this train, start reset from there //

            SignalObject thisSignal = this;
            List<SignalObject> passedSignals = new List<SignalObject>();
            int thisSignalIndex = thisSignal.thisRef;

            if (propagateReset)
            {
                while (thisSignalIndex >= 0 && signalObjects[thisSignalIndex].enabledTrain == thisTrain)
                {
                    thisSignal = signalObjects[thisSignalIndex];
                    passedSignals.Add(thisSignal);
                    thisSignalIndex = thisSignal.sigfound[(int)MstsSignalFunction.NORMAL];
                }
            }
            else
            {
                passedSignals.Add(thisSignal);
            }

            foreach (SignalObject nextSignal in passedSignals)
            {
                if (nextSignal.signalRoute != null)
                {
                    List<TrackCircuitSection> sectionsToClear = new List<TrackCircuitSection>();
                    foreach (Train.TCRouteElement thisElement in nextSignal.signalRoute)
                    {
                        TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                        sectionsToClear.Add(thisSection);  // store in list as signalRoute is lost during remove action
                    }
                    foreach (TrackCircuitSection thisSection in sectionsToClear)
                    {
                        if (thisTrain != null)
                        {
                            thisSection.RemoveTrain(thisTrain, false);
                        }
                        else
                        {
                            thisSection.Unreserve();
                        }
                    }
                }

                nextSignal.resetSignalEnabled();
            }
        }

        //================================================================================================//
        //
        // Reset signal route and next signal list as switch in route is reset
        //

        public void ResetRoute(int resetSectionIndex)
        {

            // remove this signal from any other junctions

            foreach (int thisSectionIndex in JunctionsPassed)
            {
                if (thisSectionIndex != resetSectionIndex)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisSectionIndex];
                    thisSection.SignalsPassingRoutes.Remove(thisRef);
                }
            }

            JunctionsPassed.Clear();

            for (int fntype = 0; fntype < (int)MstsSignalFunction.UNKNOWN; fntype++)
            {
                sigfound[fntype] = defaultNextSignal[fntype];
            }

            // if signal is enabled, ensure next normal signal is reset

            if (enabledTrain != null && sigfound[(int)MstsSignalFunction.NORMAL] < 0)
            {
                sigfound[(int)MstsSignalFunction.NORMAL] = SONextSignalNormal(TCNextTC);
            }

#if DEBUG_REPORTS
            File.AppendAllText(@"C:\temp\printproc.txt",
				String.Format("Signal {0} reset on Junction Change\n",
				thisRef));

            if (enabledTrain != null)
            {
				File.AppendAllText(@"C:\temp\printproc.txt",
					String.Format("Train {0} affected; new NORMAL signal : {1}\n",
					enabledTrain.Train.Number, sigfound[(int)MstsSignalFunction.NORMAL]));
            }
#endif
            if (enabledTrain != null && enabledTrain.Train.CheckTrain)
            {
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Signal {0} reset on Junction Change\n",
                    thisRef));
                File.AppendAllText(@"C:\temp\checktrain.txt",
                    String.Format("Train {0} affected; new NORMAL signal : {1}\n",
                    enabledTrain.Train.Number, sigfound[(int)MstsSignalFunction.NORMAL]));
            }
        }

        //================================================================================================//
        //
        // Test for approach control - position only
        //

        public bool ApproachControlPosition(int reqPositionM, string dumpfile)
        {
            // no train approaching
            if (enabledTrain == null)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    File.AppendAllText(dumpfile, "APPROACH CONTROL : no train approaching");
                }

                return (false);
            }

            // signal is not first signal for train
            if (enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex] != null &&
                enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef != thisRef)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("APPROACH CONTROL : Train {0} : First signal is not this signal but {1} \n",
                        enabledTrain.Train.Number, enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                return (false);
            }

            // if already cleared - return true

            if (ApproachControlCleared)
            {
                return (true);
            }

            // test distance

            if (Convert.ToInt32(enabledTrain.Train.distanceToSignal) < reqPositionM)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("APPROACH CONTROL : Train {0} at distance {1} (required {2}), clear allowed \n",
                        enabledTrain.Train.Number, enabledTrain.Train.distanceToSignal, reqPositionM);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                ApproachControlCleared = true;
                return (true);
            }
            else
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("APPROACH CONTROL : Train {0} at distance {1} (required {2}), clear not allowed \n",
                        enabledTrain.Train.Number, enabledTrain.Train.distanceToSignal, reqPositionM);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                return (false);
            }
        }

        //================================================================================================//
        //
        // Test for approach control - position and speed
        //

        public bool ApproachControlSpeed(int reqPositionM, int reqSpeedMpS, string dumpfile)
        {
            // no train approaching
            if (enabledTrain == null)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    File.AppendAllText(dumpfile, "APPROACH CONTROL : no train approaching");
                }

                return (false);
            }

            // signal is not first signal for train
            if (enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex] != null &&
                enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef != thisRef)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("APPROACH CONTROL : Train {0} : First signal is not this signal but {1} \n",
                        enabledTrain.Train.Number, enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                return (false);
            }

            // if already cleared - return true

            if (ApproachControlCleared)
            {
                return (true);
            }

            // test distance

            if (Convert.ToInt32(enabledTrain.Train.distanceToSignal) < reqPositionM)
            {
                bool validSpeed = false;
                if (reqSpeedMpS > 0)
                {
                    if (Math.Abs(enabledTrain.Train.SpeedMpS) < reqSpeedMpS)
                    {
                        validSpeed = true;
                    }
                }
                else if (reqSpeedMpS == 0)
                {
                    if (Math.Abs(enabledTrain.Train.SpeedMpS) < 0.1)
                    {
                        validSpeed = true;
                    }
                }

                if (validSpeed)
                {
                    if (!String.IsNullOrEmpty(dumpfile))
                    {
                        var sob = new StringBuilder();
                        sob.AppendFormat("APPROACH CONTROL : Train {0} at distance {1} (required {2}) and speed {3} (required {4}), clear allowed \n",
                            enabledTrain.Train.Number, enabledTrain.Train.distanceToSignal, reqPositionM, enabledTrain.Train.SpeedMpS, reqSpeedMpS);
                        File.AppendAllText(dumpfile, sob.ToString());
                    }

                    ApproachControlCleared = true;
                    return (true);
                }
                else
                {
                    if (!String.IsNullOrEmpty(dumpfile))
                    {
                        var sob = new StringBuilder();
                        sob.AppendFormat("APPROACH CONTROL : Train {0} at distance {1} (required {2}) and speed {3} (required {4}), clear not allowed \n",
                            enabledTrain.Train.Number, enabledTrain.Train.distanceToSignal, reqPositionM, enabledTrain.Train.SpeedMpS, reqSpeedMpS);
                        File.AppendAllText(dumpfile, sob.ToString());
                    }

                    return (false);
                }
            }
            else
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("APPROACH CONTROL : Train {0} at distance {1} (required {2}), clear not allowed \n",
                        enabledTrain.Train.Number, enabledTrain.Train.distanceToSignal, reqPositionM);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                return (false);
            }
        }

        //================================================================================================//
        //
        // Test if train has call-on set
        //

        public bool TrainHasCallOn(bool allowOnNonePlatform, string dumpfile)
        {
            // no train approaching
            if (enabledTrain == null)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    File.AppendAllText(dumpfile, "CALL ON : no train approaching \n");
                }

                return (false);
            }

            // signal is not first signal for train
            if (enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex] != null &&
                enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef != thisRef)
            {
                if (!String.IsNullOrEmpty(dumpfile))
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("CALL ON : Train {0} : First signal is not this signal but {1} \n",
                        enabledTrain.Train.Name, enabledTrain.Train.NextSignalObject[enabledTrain.TrainRouteDirectionIndex].thisRef);
                    File.AppendAllText(dumpfile, sob.ToString());
                }

                return (false);
            }

            if (enabledTrain.Train != null && signalRoute != null)
            {
                // process in timetable mode
                if (signalRef.Simulator.TimetableMode)
                {
                    TTTrain enabledTTTrain = enabledTrain.Train as TTTrain;

                    // always allow if set for stable working
                    if (enabledTTTrain.Stable_CallOn)
                    {
                        if (!String.IsNullOrEmpty(dumpfile))
                        {
                            var sob = new StringBuilder();
                            sob.AppendFormat("CALL ON : Train {0} : valid - train has Stable_CallOn set \n", enabledTrain.Train.Name);
                            File.AppendAllText(dumpfile, sob.ToString());
                        }
                        return (true);
                    }

                    // loop through sections in signal route
                    bool allclear = true;
                    bool intoPlatform = false;

                    foreach (Train.TCRouteElement routeElement in signalRoute)
                    {
                        TrackCircuitSection routeSection = signalRef.TrackCircuitList[routeElement.TCSectionIndex];

                        // if train is to attach to train in section, allow callon if train is stopped

                        foreach (KeyValuePair<Train.TrainRouted, int> occTrainInfo in routeSection.CircuitState.TrainOccupy)
                        {
                            Train.TrainRouted occTrain = occTrainInfo.Key;
                            TTTrain occTTTrain = occTrain.Train as TTTrain;
                            AITrain.AI_MOVEMENT_STATE movState = occTTTrain.MovementState;

                            if (occTrain.Train.Number == enabledTrain.Train.AttachTo)
                            {
                                if (movState == AITrain.AI_MOVEMENT_STATE.STOPPED || movState == AITrain.AI_MOVEMENT_STATE.STATION_STOP || movState == AITrain.AI_MOVEMENT_STATE.AI_STATIC)
                                {
                                    if (!String.IsNullOrEmpty(dumpfile))
                                    {
                                        var sob = new StringBuilder();
                                        sob.AppendFormat("CALL ON : Train {0} : valid - train is to attach to {1} \n",
                                            enabledTrain.Train.Name, occTrain.Train.Name);
                                        File.AppendAllText(dumpfile, sob.ToString());
                                    }
                                    return (true);
                                }
                                else
                                {
                                    if (!String.IsNullOrEmpty(dumpfile))
                                    {
                                        var sob = new StringBuilder();
                                        sob.AppendFormat("CALL ON : Train {0} : invalid - train is to attach to {1} but train is moving \n",
                                            enabledTrain.Train.Name, occTTTrain.Name);
                                        File.AppendAllText(dumpfile, sob.ToString());
                                    }
                                    return (false);
                                }
                            }
                        }

                        // check if route leads into platform

                        if (routeSection.PlatformIndex.Count > 0)
                        {
                            PlatformDetails thisPlatform = signalRef.PlatformDetailsList[routeSection.PlatformIndex[0]];
                            if (enabledTrain.Train.StationStops.Count > 0) // train has stops
                            {
                                if (String.Compare(enabledTrain.Train.StationStops[0].PlatformItem.Name, thisPlatform.Name) == 0 && enabledTrain.Train.StationStops[0].CallOnAllowed) // stop is next station stop and callon is set
                                {
                                    intoPlatform = true;

                                    // only allow if train ahead is stopped
                                    foreach (KeyValuePair<Train.TrainRouted, int> occTrainInfo in routeSection.CircuitState.TrainOccupy)
                                    {
                                        Train.TrainRouted occTrain = occTrainInfo.Key;
                                        TTTrain occTTTrain = occTrain.Train as TTTrain;
                                        AITrain.AI_MOVEMENT_STATE movState = occTTTrain.MovementState;

                                        if (movState == AITrain.AI_MOVEMENT_STATE.STOPPED || movState == AITrain.AI_MOVEMENT_STATE.STATION_STOP || movState == AITrain.AI_MOVEMENT_STATE.AI_STATIC)
                                        {
                                            if (!String.IsNullOrEmpty(dumpfile))
                                            {
                                                var sob = new StringBuilder();
                                                sob.AppendFormat("CALL ON : Train {0} : access to platform {1}, train {2} is stopped \n",
                                                    enabledTrain.Train.Name, thisPlatform.Name, occTTTrain.Name);
                                                File.AppendAllText(dumpfile, sob.ToString());
                                            }
                                        }
                                        else
                                        {
                                            if (!String.IsNullOrEmpty(dumpfile))
                                            {
                                                var sob = new StringBuilder();
                                                sob.AppendFormat("CALL ON : Train {0} : invalid - access to platform {1}, but train {2} is moving \n",
                                                    enabledTrain.Train.Name, thisPlatform.Name, occTTTrain.Name);
                                                File.AppendAllText(dumpfile, sob.ToString());
                                            }
                                            allclear = false;
                                            break; // no need to check for other trains
                                        }
                                    }
                                }
                                else
                                {
                                    // not first station or train has call-on not set, no need to check any further
                                    if (!String.IsNullOrEmpty(dumpfile))
                                    {
                                        var sob = new StringBuilder();
                                        sob.AppendFormat("CALL ON : Train {0} : invalid - access to platform {1}, train does not call or has no call-on set \n",
                                            enabledTrain.Train.Name, thisPlatform.Name);
                                        File.AppendAllText(dumpfile, sob.ToString());
                                    }
                                    allclear = false;
                                }
                            }
                            else
                            {
                                // train has no stops - no need to check further
                                if (!String.IsNullOrEmpty(dumpfile))
                                {
                                    var sob = new StringBuilder();
                                    sob.AppendFormat("CALL ON : Train {0} : invalid - access to platform {1}, but train has no stops \n",
                                        enabledTrain.Train.Name, thisPlatform.Name);
                                    File.AppendAllText(dumpfile, sob.ToString());
                                }
                                allclear = false;
                            }

                            if (!allclear) // invalid situation found - no need to check any further
                            {
                                break;
                            }
                        }
                    }

                    if (intoPlatform)
                    {
                        // path leads into platform - return state as derived
                        return (allclear);
                    }
                    else
                    {
                        // path does not lead into platform - return state as defined in call
                        if (!String.IsNullOrEmpty(dumpfile))
                        {
                            var sob = new StringBuilder();
                            sob.AppendFormat("CALL ON : Train {0} : {1} - route does not lead into platform \n", enabledTrain.Train.Name, allowOnNonePlatform);
                            File.AppendAllText(dumpfile, sob.ToString());
                        }
                        return (allowOnNonePlatform);
                    }
                }

                // process in activity  mode
                else
                {
                    bool intoPlatform = false;

                    foreach (Train.TCRouteElement routeElement in signalRoute)
                    {
                        TrackCircuitSection routeSection = signalRef.TrackCircuitList[routeElement.TCSectionIndex];

                        // check if route leads into platform

                        if (routeSection.PlatformIndex.Count > 0)
                        {
                            intoPlatform = true;
                        }
                    }

                    if (!intoPlatform)
                    {
                        //if track does not lead into platform, return state as defined in call
                        if (!String.IsNullOrEmpty(dumpfile))
                        {
                            var sob = new StringBuilder();
                            sob.AppendFormat("CALL ON : Train {0} : {1} - route does not lead into platform \n", enabledTrain.Train.Name, allowOnNonePlatform);
                            File.AppendAllText(dumpfile, sob.ToString());
                        }
                        return (allowOnNonePlatform);
                    }
                    else
                    {
                        // never allow if track leads into platform
                        if (!String.IsNullOrEmpty(dumpfile))
                        {
                            var sob = new StringBuilder();
                            sob.AppendFormat("CALL ON : Train {0} : invalid - route leads into platform \n", enabledTrain.Train.Name);
                            File.AppendAllText(dumpfile, sob.ToString());
                        }
                        return (false);
                    }
                }
            }

            if (!String.IsNullOrEmpty(dumpfile))
            {
                var sob = new StringBuilder();
                sob.AppendFormat("CALL ON : Train {0} : not valid \n", enabledTrain.Train.Name);
                File.AppendAllText(dumpfile, sob.ToString());
            }
            return (false);
        }

        //================================================================================================//
        /// <summary>
        /// LockForTrain
        /// Add a lock for a train and a specific subpath (default 0).  This allow the control of this signal by a specific action
        /// </summary>

        public bool LockForTrain(int trainNumber, int subpath = 0)
        {
            KeyValuePair<int, int> newLock = new KeyValuePair<int, int>(trainNumber, subpath);
            LockedTrains.Add(newLock);
            return false;
        }

        public bool UnlockForTrain(int trainNumber, int subpath = 0)
        {
            bool info = LockedTrains.Remove(LockedTrains.First(item => item.Key.Equals(trainNumber) && item.Value.Equals(subpath)));
            return info;
        }

        public bool HasLockForTrain(int trainNumber, int subpath = 0)
        {
            bool info = (LockedTrains.Count > 0 && LockedTrains.Exists(item => item.Key.Equals(trainNumber) && item.Value.Equals(subpath)));
            return info;
        }

        public bool CleanAllLock(int trainNumber)
        {
            int info = LockedTrains.RemoveAll(item => item.Key.Equals(trainNumber));
            if (info > 0)
                return true;
            return false;
        }

        //================================================================================================//
        //
        // HasHead
        //
        // Returns 1 if signal has optional head set, 0 if not
        //

        public int HasHead(int requiredHeadIndex)
        {
            return ((requiredHeadIndex < WorldObject.HeadsSet.Length) ? (WorldObject.HeadsSet[requiredHeadIndex] ? 1 : 0) : 0);
        }

        //================================================================================================//
        //
        // Set HOLD state for dispatcher control
        //
        // Parameter : bool, if set signal must be reset if set (and train position allows)
        //
        // Returned : bool[], dimension 2,
        //            field [0] : if true, hold state is set
        //            field [1] : if true, signal is reset (always returns false if reset not requested)
        //

        public bool[] requestHoldSignalDispatcher(bool requestResetSignal)
        {
            bool[] returnValue = new bool[2] { false, false };
            MstsSignalAspect thisAspect = this_sig_lr(MstsSignalFunction.NORMAL);

            // signal not enabled - set lock, reset if cleared (auto signal can clear without enabling)

            if (enabledTrain == null || enabledTrain.Train == null)
            {
                holdState = HoldState.ManualLock;
                if (thisAspect > MstsSignalAspect.STOP) ResetSignal(true);
                returnValue[0] = true;
            }

            // if enabled, cleared and reset not requested : no action

            else if (!requestResetSignal && thisAspect > MstsSignalAspect.STOP)
            {
                holdState = HoldState.ManualLock; //just in case this one later will be set to green by the system
                returnValue[0] = true;
            }

            // if enabled and not cleared : set hold, no reset required

            else if (thisAspect == MstsSignalAspect.STOP)
            {
                holdState = HoldState.ManualLock;
                returnValue[0] = true;
            }

            // enabled, cleared , reset required : check train speed
            // if train is moving : no action
            //temporarily removed by JTang, before the full revision is ready
            //          else if (Math.Abs(enabledTrain.Train.SpeedMpS) > 0.1f)
            //          {
            //          }

            // if train is stopped : reset signal, breakdown train route, set holdstate

            else
            {
                int signalRouteIndex = enabledTrain.Train.ValidRoute[enabledTrain.TrainRouteDirectionIndex].GetRouteIndex(TCNextTC, 0);
                if (signalRouteIndex >= 0)
                {
                    signalRef.BreakDownRouteList(enabledTrain.Train.ValidRoute[enabledTrain.TrainRouteDirectionIndex], signalRouteIndex, enabledTrain);
                    ResetSignal(true);
                    holdState = HoldState.ManualLock;
                    returnValue[0] = true;
                    returnValue[1] = true;
                }
                else //hopefully this does not happen
                {
                    holdState = HoldState.ManualLock;
                    returnValue[0] = true;
                }
            }

            return (returnValue);
        }

        //================================================================================================//
        //
        // Reset HOLD state for dispatcher control
        //
        // Parameter : none
        //
        // Returned : void
        //

        public void clearHoldSignalDispatcher()
        {
            holdState = HoldState.None;
        }

    }  // SignalObject


    //================================================================================================//
    //
    // class SignalHead
    //
    //================================================================================================//

    public class SignalHead
    {
        public SignalType signalType;           // from sigcfg file
        public MstsSignalAspect state = MstsSignalAspect.STOP;
        public int draw_state;
        public int trItemIndex;                 // Index to trItem   
        public uint TrackJunctionNode;          // Track Junction Node (= 0 if not set)
        public uint JunctionPath;               // Required Junction Path
        public int JunctionMainNode;            // Main node following junction
        public int TDBIndex;                    // Index to TDB Signal Item
        public ObjectSpeedInfo[] speed_info;      // speed limit info (per aspect)

        public SignalObject mainSignal;        //  This is the signal which this head forms a part.

        public float? ApproachControlLimitPositionM;
        public float? ApproachControlLimitSpeedMpS;

        public MstsSignalFunction sigFunction
        {
            get
            {
                if (signalType != null)
                    return (MstsSignalFunction)signalType.FnType;
                else
                    return MstsSignalFunction.UNKNOWN;
            }
        }

        public String SignalTypeName
        {
            get
            {
                if (signalType != null)
                    return signalType.Name;
                else
                    return "";
            }
        }

        //================================================================================================//
        //
        // Constructor for signals
        //

        public SignalHead(SignalObject sigOoject, int trItem, int TDBRef, SignalItem sigItem)
        {
            mainSignal = sigOoject;
            trItemIndex = trItem;
            TDBIndex = TDBRef;

            if (sigItem.NoSigDirs > 0)
            {
                TrackJunctionNode = sigItem.TrSignalDirs[0].TrackNode;
                JunctionPath = sigItem.TrSignalDirs[0].LinkLRPath;
            }

            var sigasp_values = Enum.GetValues(typeof(MstsSignalAspect));
            speed_info = new ObjectSpeedInfo[sigasp_values.Length];
        }

        //================================================================================================//
        //
        // Constructor for speedposts
        //

        public SignalHead(SignalObject sigOoject, int trItem, int TDBRef, SpeedPostItem speedItem)
        {
            mainSignal = sigOoject;
            trItemIndex = trItem;
            TDBIndex = TDBRef;
            draw_state = 1;
            state = MstsSignalAspect.CLEAR_2;
            signalType = new SignalType(SignalType.FnTypes.Speed, MstsSignalAspect.CLEAR_2);

            var sigasp_values = Enum.GetValues(typeof(MstsSignalAspect));
            speed_info = new ObjectSpeedInfo[sigasp_values.Length];

            float speedMpS = MpS.ToMpS(speedItem.SpeedInd, !speedItem.IsMPH);
            if (speedItem.IsResume)
                speedMpS = 999f;

            float passSpeed = speedItem.IsPassenger ? speedMpS : -1;
            float freightSpeed = speedItem.IsFreight ? speedMpS : -1;
            ObjectSpeedInfo speedinfo = new ObjectSpeedInfo(passSpeed, freightSpeed, false, false, speedItem is TempSpeedPostItem? (speedMpS == 999f? 2 : 1) : 0);
            speed_info[(int)state] = speedinfo;
        }

        //================================================================================================//
        //
        // SetSignalType : This method sets the signal type object from the CIGCFG file
        //

        public void SetSignalType(TrItem[] TrItems, SignalConfigurationFile sigCFG)
        {
            SignalItem sigItem = (SignalItem)TrItems[TDBIndex];

            // set signal type
            if (sigCFG.SignalTypes.ContainsKey(sigItem.SignalType))
            {
                signalType = sigCFG.SignalTypes[sigItem.SignalType];

                // set signal speeds
                foreach (SignalAspect thisAspect in signalType.Aspects)
                {
                    int arrindex = (int)thisAspect.Aspect;
                    speed_info[arrindex] = new ObjectSpeedInfo(thisAspect.SpeedMpS, thisAspect.SpeedMpS, thisAspect.Asap, thisAspect.Reset, thisAspect.NoSpeedReduction? 1 : 0);
                }

                // update overall SignalNumClearAhead

                if (sigFunction == MstsSignalFunction.NORMAL)
                {
                    mainSignal.SignalNumClearAhead_MSTS = Math.Max(mainSignal.SignalNumClearAhead_MSTS, signalType.NumClearAhead_MSTS);
                    mainSignal.SignalNumClearAhead_ORTS = Math.Max(mainSignal.SignalNumClearAhead_ORTS, signalType.NumClearAhead_ORTS);
                }

                // set approach control limits

                if (signalType.ApproachControlDetails != null)
                {
                    ApproachControlLimitPositionM = signalType.ApproachControlDetails.ApproachControlPositionM;
                    ApproachControlLimitSpeedMpS = signalType.ApproachControlDetails.ApproachControlSpeedMpS;
                }
                else
                {
                    ApproachControlLimitPositionM = null;
                    ApproachControlLimitSpeedMpS = null;
                }

                if (sigFunction == MstsSignalFunction.SPEED)
                {
                    mainSignal.isSignal = false;
                }
            }
            else
            {
                Trace.TraceWarning("SignalObject trItem={0}, trackNode={1} has SignalHead with undefined SignalType {2}.",
                                  mainSignal.trItem, mainSignal.trackNode, sigItem.SignalType);
            }


        }//SetSignalType

        //================================================================================================//
        //
        //  Following methods used in scipting
        //

        public MstsSignalAspect next_sig_mr(MstsSignalFunction sigFN)
        {
            return mainSignal.next_sig_mr(sigFN);
        }

        public MstsSignalAspect next_sig_lr(MstsSignalFunction sigFN)
        {
            return mainSignal.next_sig_lr(sigFN);
        }

        public MstsSignalAspect this_sig_lr(MstsSignalFunction sigFN)
        {
            return mainSignal.this_sig_lr(sigFN);
        }

        public MstsSignalAspect this_sig_lr(MstsSignalFunction sigFN, ref bool sigfound)
        {
            return mainSignal.this_sig_lr(sigFN, ref sigfound);
        }

        public MstsSignalAspect this_sig_mr(MstsSignalFunction sigFN)
        {
            return mainSignal.this_sig_mr(sigFN);
        }

        public MstsSignalAspect this_sig_mr(MstsSignalFunction sigFN, ref bool sigfound)
        {
            return mainSignal.this_sig_mr(sigFN, ref sigfound);
        }

        public MstsSignalAspect opp_sig_mr(MstsSignalFunction sigFN)
        {
            return mainSignal.opp_sig_mr(sigFN);
        }

        public MstsSignalAspect opp_sig_mr(MstsSignalFunction sigFN, ref SignalObject signalFound) // for debug purposes
        {
            return mainSignal.opp_sig_mr(sigFN, ref signalFound);
        }

        public MstsSignalAspect opp_sig_lr(MstsSignalFunction sigFN)
        {
            return mainSignal.opp_sig_lr(sigFN);
        }

        public MstsSignalAspect opp_sig_lr(MstsSignalFunction sigFN, ref SignalObject signalFound) // for debug purposes
        {
            return mainSignal.opp_sig_lr(sigFN, ref signalFound);
        }

        public MstsSignalAspect next_nsig_lr(MstsSignalFunction sigFN, int nsignals, string dumpfile)
        {
            return mainSignal.next_nsig_lr(sigFN, nsignals, dumpfile);
        }

        //================================================================================================//
        //
        //  dist_multi_sig_mr : Returns most restrictive state of signal type A, for all type A upto type B
        //  
        //

        public MstsSignalAspect dist_multi_sig_mr(MstsSignalFunction sigFN1, MstsSignalFunction sigFN2, string dumpfile)
        {
            MstsSignalAspect foundState = MstsSignalAspect.CLEAR_2;
            bool foundValid = false;

            // get signal of type 2 (end signal)

            if (dumpfile.Length > 1)
            {
                File.AppendAllText(dumpfile,
                    String.Format("DIST_MULTI_SIG_MR for {0} + upto {1}\n",
                    sigFN1, sigFN2));
            }

            int sig2Index = mainSignal.sigfound[(int)sigFN2];
            if (sig2Index < 0)           // try renewed search with full route
            {
                sig2Index = mainSignal.SONextSignal(sigFN2);
                mainSignal.sigfound[(int)sigFN2] = sig2Index;
            }

            if (dumpfile.Length > 1)
            {
                if (sig2Index < 0)
                    File.AppendAllText(dumpfile, "  no signal type 2 found\n");
            }

            if (dumpfile.Length > 1)
            {
                var sob = new StringBuilder();
                sob.AppendFormat("  signal type 2 : {0}", mainSignal.sigfound[(int)sigFN2]);

                if (mainSignal.sigfound[(int)sigFN2] > 0)
                {
                    SignalObject otherSignal = mainSignal.signalRef.SignalObjects[mainSignal.sigfound[(int)sigFN2]];
                    sob.AppendFormat(" (");

                    foreach (SignalHead otherHead in otherSignal.SignalHeads)
                    {
                        sob.AppendFormat(" {0} ", otherHead.TDBIndex);
                    }

                    sob.AppendFormat(") ");
                }
                sob.AppendFormat("\n");

                File.AppendAllText(dumpfile, sob.ToString());
            }

            SignalObject thisSignal = mainSignal;

            // ensure next signal of type 1 is located correctly

            thisSignal.sigfound[(int)sigFN1] = thisSignal.SONextSignal(sigFN1);

            // loop through all available signals of type 1

            while (thisSignal.sigfound[(int)sigFN1] >= 0)
            {
                thisSignal = thisSignal.signalRef.SignalObjects[thisSignal.sigfound[(int)sigFN1]];

                MstsSignalAspect thisState = thisSignal.this_sig_mr(sigFN1);

                if (dumpfile.Length > 1)
                {
                    var sob = new StringBuilder();
                    sob.AppendFormat("  signal type 1 : {0}", thisSignal.thisRef);
                    sob.AppendFormat(" (");

                    foreach (SignalHead otherHead in thisSignal.SignalHeads)
                    {
                        sob.AppendFormat(" {0} ", otherHead.TDBIndex);
                    }

                    sob.AppendFormat(") ");
                    sob.AppendFormat("\n");

                    File.AppendAllText(dumpfile, sob.ToString());
                    File.AppendAllText(dumpfile,
                        String.Format("  signal type 1 : {0} = {1}\n",
                        thisSignal.thisRef, thisState));
                }

                // ensure correct next signals are located

                if (sigFN1 != MstsSignalFunction.NORMAL || !thisSignal.isSignalNormal()) thisSignal.sigfound[(int)sigFN1] = thisSignal.SONextSignal(sigFN1);
                if (sigFN2 != MstsSignalFunction.NORMAL || !thisSignal.isSignalNormal()) thisSignal.sigfound[(int)sigFN2] = thisSignal.SONextSignal(sigFN2);

                if (sig2Index >= 0 && thisSignal.sigfound[(int)sigFN2] != sig2Index)  // we are beyond type 2 signal
                {
                    return (foundValid ? foundState : MstsSignalAspect.STOP);
                }
                foundValid = true;
                foundState = foundState < thisState ? foundState : thisState;
            }

            return (foundValid ? foundState : MstsSignalAspect.STOP);   // no type 2 or running out of signals before finding type 2
        }

        //================================================================================================//
        //
        //  sig_feature : return state of requested feature through signal head flags
        //  
        //

        public bool sig_feature(int feature)
        {
            bool flag_value = true;

            if (mainSignal.WorldObject != null)
            {
                if (feature < mainSignal.WorldObject.FlagsSet.Length)
                {
                    flag_value = mainSignal.WorldObject.FlagsSet[feature];
                }
            }

            return flag_value;
        }

        //================================================================================================//
        //
        //  def_draw_state : Returns the default draw state for this signal head from the SIGCFG file
        //  Retruns -1 id no draw state.
        //

        public int def_draw_state(MstsSignalAspect state)
        {
            if (signalType != null)
                return signalType.def_draw_state(state);
            else
                return -1;
        }//def_draw_state

        //================================================================================================//
        //
        //  SetMostRestrictiveAspect : Sets the state to the most restrictive aspect for this head.
        //

        public void SetMostRestrictiveAspect()
        {
            if (signalType != null)
                state = signalType.GetMostRestrictiveAspect();
            else
                state = MstsSignalAspect.STOP;

            draw_state = def_draw_state(state);
        }//SetMostRestrictiveAspect

        //================================================================================================//
        //
        //  SetLeastRestrictiveAspect : Sets the state to the least restrictive aspect for this head.
        //

        public void SetLeastRestrictiveAspect()
        {
            if (signalType != null)
                state = signalType.GetLeastRestrictiveAspect();
            else
                state = MstsSignalAspect.CLEAR_2;
            def_draw_state(state);
        }//SetLeastRestrictiveAspect

        //================================================================================================//
        //
        //  route_set : check if linked route is set
        //

        public int route_set()
        {
            bool juncfound = true;

            // call route_set routine from main signal

            if (TrackJunctionNode > 0)
            {
                juncfound = mainSignal.route_set(JunctionMainNode, TrackJunctionNode);
            }
            //added by JTang
            else if (MPManager.IsMultiPlayer())
            {
                var node = mainSignal.signalRef.trackDB.TrackNodes[mainSignal.trackNode];
                if (node.TrJunctionNode == null && node.TrPins != null && mainSignal.TCDirection < node.TrPins.Length)
                {
                    node = mainSignal.signalRef.trackDB.TrackNodes[node.TrPins[mainSignal.TCDirection].Link];
                    if (node.TrJunctionNode == null) return 0;
                    for (var pin = node.Inpins; pin < node.Inpins + node.Outpins; pin++)
                    {
                        if (node.TrPins[pin].Link == mainSignal.trackNode && pin - node.Inpins != node.TrJunctionNode.SelectedRoute)
                        {
                            juncfound = false;
                            break;
                        }
                    }
                }
            }
            if (juncfound)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }//route_set

        //================================================================================================//
        //
        //  Default update process
        //

        public void Update()
        {
            SIGSCRfile.SH_update(this, Signals.scrfile);
        }
    } //Update


    //================================================================================================//
    //
    // class SignalRefObject
    //
    //================================================================================================//

    public class SignalRefObject
    {
        public uint SignalWorldIndex;
        public uint HeadIndex;

        //================================================================================================//
        //
        // Constructor
        //

        public SignalRefObject(int WorldIndexIn, uint HeadItemIn)
        {
            SignalWorldIndex = Convert.ToUInt32(WorldIndexIn);
            HeadIndex = HeadItemIn;
        }
    }

    //================================================================================================//
    //
    // class SignalWorldInfo
    //
    //================================================================================================//

    public class SignalWorldObject
    {
        public string SFileName;
        public Dictionary<uint, uint> HeadReference;     // key=TDBIndex, value=headindex
        public bool[] HeadsSet;                          // Flags heads which are set
        public bool[] FlagsSet;                          // Flags signal-flags which are set
        public bool[] FlagsSetBackfacing;                // Flags signal-flags which are set
        //    for backfacing signal
        public List<int> Backfacing = new List<int>();   // Flags heads which are backfacing

        //================================================================================================//
        //
        // Constructor
        //

        public SignalWorldObject(Orts.Formats.Msts.SignalObj SignalWorldItem, SignalConfigurationFile sigcfg)
        {
            Orts.Formats.Msts.SignalShape thisCFGShape;

            HeadReference = new Dictionary<uint, uint>();

            // set flags with length to number of possible SubObjects type

            FlagsSet = new bool[Orts.Formats.Msts.SignalShape.SignalSubObj.SignalSubTypes.Count];
            FlagsSetBackfacing = new bool[Orts.Formats.Msts.SignalShape.SignalSubObj.SignalSubTypes.Count];
            for (uint iFlag = 0; iFlag < FlagsSet.Length; iFlag++)
            {
                FlagsSet[iFlag] = false;
                FlagsSetBackfacing[iFlag] = false;
            }

            // get filename in Uppercase

            SFileName = Path.GetFileName(SignalWorldItem.FileName).ToUpperInvariant();

            // search defined shapes in SIGCFG to find signal definition

            if (sigcfg.SignalShapes.TryGetValue(SFileName, out thisCFGShape))
            {

                HeadsSet = new bool[thisCFGShape.SignalSubObjs.Count];

                // loop through all heads and check SubObj flag per bit to check if head is set

                uint iMask = 1;

                for (int iHead = 0; iHead < thisCFGShape.SignalSubObjs.Count; iHead++)
                {
                    HeadsSet[iHead] = false;
                    uint headSet = SignalWorldItem.SignalSubObj & iMask;
                    Orts.Formats.Msts.SignalShape.SignalSubObj thisSubObjs = thisCFGShape.SignalSubObjs[iHead];
                    if (headSet != 0)
                    {

                        // set head, and if head is flag, also set flag

                        HeadsSet[iHead] = true;

                        if (thisSubObjs.BackFacing)
                        {
                            Backfacing.Add(iHead);
                            if (thisSubObjs.SignalSubType >= 1)
                            {
                                FlagsSetBackfacing[thisSubObjs.SignalSubType] = true;
                            }
                        }
                        else if (thisSubObjs.SignalSubType >= 1)
                        {
                            FlagsSet[thisSubObjs.SignalSubType] = true;
                        }
                    }
                    iMask = iMask << 1;
                }

                // get TDB and head reference from World file

                foreach (Orts.Formats.Msts.SignalUnit signalUnitInfo in SignalWorldItem.SignalUnits.Units)
                {
                    uint TrItemRef = signalUnitInfo.TrItem;
                    uint HeadRef = Convert.ToUInt32(signalUnitInfo.SubObj);
                    HeadReference.Add(TrItemRef, HeadRef);
                }
            }
            else
            {
                Trace.TraceWarning("Signal not found : {0} n", SFileName);
            }

        }


        //================================================================================================//
        //
        // Constructor for copy
        //

        public SignalWorldObject(SignalWorldObject copy)
        {
            SFileName = String.Copy(copy.SFileName);
            Backfacing = copy.Backfacing;

            HeadsSet = new bool[copy.HeadsSet.Length];
            FlagsSet = new bool[copy.FlagsSet.Length];
            FlagsSetBackfacing = new bool[copy.FlagsSet.Length];
            copy.HeadsSet.CopyTo(HeadsSet, 0);
            copy.FlagsSet.CopyTo(FlagsSet, 0);
            copy.FlagsSetBackfacing.CopyTo(FlagsSet, 0);

            HeadReference = new Dictionary<uint, uint>();
            foreach (KeyValuePair<uint, uint> thisRef in copy.HeadReference)
            {
                HeadReference.Add(thisRef.Key, thisRef.Value);
            }
        }

    }

    //================================================================================================//
    //
    // class ObjectItemInfo
    //
    //================================================================================================//

    public class ObjectItemInfo
    {
        public enum ObjectItemType
        {
            Any,
            Signal,
            Speedlimit,
        }

        public enum ObjectItemFindState
        {
            None = 0,
            Object = 1,
            EndOfTrack = -1,
            PassedDanger = -2,
            PassedMaximumDistance = -3,
            TdbError = -4,
            EndOfAuthority = -5,
            EndOfPath = -6,
        }

        public ObjectItemType ObjectType;                     // type information
        public ObjectItemFindState ObjectState;               // state information

        public SignalObject ObjectDetails;                    // actual object 

        public float distance_found;
        public float distance_to_train;
        public float distance_to_object;

        public MstsSignalAspect signal_state;                   // UNKNOWN if type = speedlimit
        // set active by TRAIN
        public float speed_passenger;                // -1 if not set
        public float speed_freight;                  // -1 if not set
        public int speed_flag;
        public int speed_reset;
        // for signals: if = 1 no speed reduction; for speedposts: if = 0 standard; = 1 start of temp speedreduction post; = 2 end of temp speed reduction post
        public int speed_noSpeedReductionOrIsTempSpeedReduction; 
        public float actual_speed;                   // set active by TRAIN

        public bool processed;                       // for AI trains, set active by TRAIN

        //================================================================================================//
        //
        // Constructor
        //

        public ObjectItemInfo(SignalObject thisObject, float distance)
        {
            ObjectSpeedInfo speed_info;
            ObjectState = ObjectItemFindState.Object;

            distance_found = distance;

            ObjectDetails = thisObject;

            if (thisObject.isSignal)
            {
                ObjectType = ObjectItemType.Signal;
                signal_state = MstsSignalAspect.UNKNOWN;  // set active by TRAIN
                speed_passenger = -1;                      // set active by TRAIN
                speed_freight = -1;                      // set active by TRAIN
                speed_flag = 0;                       // set active by TRAIN
                speed_reset = 0;                      // set active by TRAIN
                speed_noSpeedReductionOrIsTempSpeedReduction = 0;
            }
            else
            {
                ObjectType = ObjectItemType.Speedlimit;
                signal_state = MstsSignalAspect.UNKNOWN;
                speed_info = thisObject.this_lim_speed(MstsSignalFunction.SPEED);
                speed_passenger = speed_info.speed_pass;
                speed_freight = speed_info.speed_freight;
                speed_flag = speed_info.speed_flag;
                speed_reset = speed_info.speed_reset;
                speed_noSpeedReductionOrIsTempSpeedReduction = speed_info.speed_noSpeedReductionOrIsTempSpeedReduction;
            }
        }



        public ObjectItemInfo(ObjectItemFindState thisState)
        {
            ObjectState = thisState;
        }

    }

    //================================================================================================//
    //
    // class ObjectSpeedInfo
    //
    //================================================================================================//

    public class ObjectSpeedInfo
    {

        public float speed_pass;
        public float speed_freight;
        public int speed_flag;
        public int speed_reset;
        public int speed_noSpeedReductionOrIsTempSpeedReduction;

        //================================================================================================//
        //
        // Constructor
        //

        public ObjectSpeedInfo(float pass, float freight, bool asap, bool reset, int nospeedreductionOristempspeedreduction)
        {
            speed_pass = pass;
            speed_freight = freight;
            speed_flag = asap ? 1 : 0;
            speed_reset = reset ? 1 : 0;
            speed_noSpeedReductionOrIsTempSpeedReduction = nospeedreductionOristempspeedreduction;
        }
    }

    //================================================================================================//
    //
    // Class Platform Details
    //
    //================================================================================================//

    public class PlatformDetails
    {
        public List<int> TCSectionIndex = new List<int>();
        public int[] PlatformReference = new int[2];
        public float[,] TCOffset = new float[2, 2];
        public float[] nodeOffset = new float[2];
        public float Length;
        public int[] EndSignals = new int[2] { -1, -1 };
        public float[] DistanceToSignals = new float[2];
        public string Name;
        public uint MinWaitingTime;
        public int NumPassengersWaiting;


        //================================================================================================//
        //
        // Constructor
        //

        public PlatformDetails(int platformReference)
        {
            PlatformReference[0] = platformReference;
        }

        //================================================================================================//
        //
        // Constructor for copy
        //

        public PlatformDetails(PlatformDetails orgDetails)
        {
            foreach (int sectionIndex in orgDetails.TCSectionIndex)
            {
                TCSectionIndex.Add(sectionIndex);
            }

            orgDetails.PlatformReference.CopyTo(PlatformReference, 0);
            TCOffset[0, 0] = orgDetails.TCOffset[0, 0];
            TCOffset[0, 1] = orgDetails.TCOffset[0, 1];
            TCOffset[1, 0] = orgDetails.TCOffset[1, 0];
            TCOffset[1, 1] = orgDetails.TCOffset[1, 1];
            orgDetails.nodeOffset.CopyTo(nodeOffset, 0);
            Length = orgDetails.Length;
            orgDetails.EndSignals.CopyTo(EndSignals, 0);
            orgDetails.DistanceToSignals.CopyTo(DistanceToSignals, 0);
            Name = String.Copy(orgDetails.Name);
            MinWaitingTime = orgDetails.MinWaitingTime;
            NumPassengersWaiting = orgDetails.NumPassengersWaiting;
        }
    }

    //================================================================================================//

    //================================================================================================//
    //
    // DeadlockInfo Object
    //
    //================================================================================================//

    public class DeadlockInfo
    {
        public enum DeadlockTrainState                                    // state of train wrt this deadlock                     
        {
            KeepClearThisDirection,
            KeepClearReverseDirection,
            Approaching,
            StoppedAheadLoop,
            InLoop,
            StoppedInLoop,
        }

        protected Signals signalRef;                                       // reference to overlaying Signals class

        public int DeadlockIndex;                                          // this deadlock unique index reference
        public List<DeadlockPathInfo> AvailablePathList;                   // list of available paths
        public Dictionary<int, List<int>> PathReferences;                  // list of paths per boundary section
        public Dictionary<int, List<int>> TrainReferences;                 // list of paths as allowed per train/subpath index
        public Dictionary<int, Dictionary<int, bool>> TrainLengthFit;      // list of length fit per train/subpath and per path
        public Dictionary<int, int> TrainOwnPath;                          // train's own path per train/subpath
        public Dictionary<int, int> InverseInfo;                           // list of paths which are each others inverse
        public Dictionary<int, Dictionary<int, int>> TrainSubpathIndex;    // unique index per train and subpath
        private int nextTrainSubpathIndex;                                 // counter for train/subpath index

        //================================================================================================//
        //
        // Constructor for emtpy struct to gain access to methods
        //

        public DeadlockInfo(Signals signalReference)
        {
            signalRef = signalReference;
        }

        //================================================================================================//
        //
        // Constructor
        //

        public DeadlockInfo(Signals signalReference, TrackCircuitSection startSection, TrackCircuitSection endSection)
        {
            signalRef = signalReference;

            DeadlockIndex = signalRef.deadlockIndex++;

            AvailablePathList = new List<DeadlockPathInfo>();
            PathReferences = new Dictionary<int, List<int>>();
            TrainReferences = new Dictionary<int, List<int>>();
            TrainLengthFit = new Dictionary<int, Dictionary<int, bool>>();
            TrainOwnPath = new Dictionary<int, int>();
            InverseInfo = new Dictionary<int, int>();
            TrainSubpathIndex = new Dictionary<int, Dictionary<int, int>>();
            nextTrainSubpathIndex = 0;

            signalRef.DeadlockInfoList.Add(DeadlockIndex, this);
        }

        //================================================================================================//
        //
        // Constructor for restore
        //

        public DeadlockInfo(Signals signalReference, BinaryReader inf)
        {
            signalRef = signalReference;

            DeadlockIndex = inf.ReadInt32();
            AvailablePathList = new List<DeadlockPathInfo>();

            int totalPaths = inf.ReadInt32();
            for (int iPath = 0; iPath <= totalPaths - 1; iPath++)
            {
                DeadlockPathInfo thisPath = new DeadlockPathInfo(inf);
                AvailablePathList.Add(thisPath);
            }

            PathReferences = new Dictionary<int, List<int>>();

            int totalReferences = inf.ReadInt32();
            for (int iReference = 0; iReference <= totalReferences - 1; iReference++)
            {
                int thisReference = inf.ReadInt32();
                List<int> thisList = new List<int>();

                int totalItems = inf.ReadInt32();
                for (int iItem = 0; iItem <= totalItems - 1; iItem++)
                {
                    int thisItem = inf.ReadInt32();
                    thisList.Add(thisItem);
                }
                PathReferences.Add(thisReference, thisList);
            }

            TrainReferences = new Dictionary<int, List<int>>();

            totalReferences = inf.ReadInt32();
            for (int iReference = 0; iReference <= totalReferences - 1; iReference++)
            {
                int thisReference = inf.ReadInt32();
                List<int> thisList = new List<int>();

                int totalItems = inf.ReadInt32();
                for (int iItem = 0; iItem <= totalItems - 1; iItem++)
                {
                    int thisItem = inf.ReadInt32();
                    thisList.Add(thisItem);
                }
                TrainReferences.Add(thisReference, thisList);
            }

            TrainLengthFit = new Dictionary<int, Dictionary<int, bool>>();

            int totalFits = inf.ReadInt32();
            for (int iFits = 0; iFits <= totalFits - 1; iFits++)
            {
                int thisTrain = inf.ReadInt32();
                Dictionary<int, bool> thisLengthFit = new Dictionary<int, bool>();

                int totalItems = inf.ReadInt32();
                for (int iItem = 0; iItem <= totalItems - 1; iItem++)
                {
                    int itemRef = inf.ReadInt32();
                    bool itemValue = inf.ReadBoolean();

                    thisLengthFit.Add(itemRef, itemValue);
                }
                TrainLengthFit.Add(thisTrain, thisLengthFit);
            }

            TrainOwnPath = new Dictionary<int, int>();

            int totalOwnPath = inf.ReadInt32();
            for (int iOwnPath = 0; iOwnPath <= totalOwnPath - 1; iOwnPath++)
            {
                int trainIndex = inf.ReadInt32();
                int pathIndex = inf.ReadInt32();
                TrainOwnPath.Add(trainIndex, pathIndex);
            }

            InverseInfo = new Dictionary<int, int>();
            int totalInverseInfo = inf.ReadInt32();

            for (int iInfo = 0; iInfo <= totalInverseInfo - 1; iInfo++)
            {
                int infoKey = inf.ReadInt32();
                int infoValue = inf.ReadInt32();
                InverseInfo.Add(infoKey, infoValue);
            }

            TrainSubpathIndex = new Dictionary<int, Dictionary<int, int>>();
            int totalTrain = inf.ReadInt32();

            for (int iTrain = 0; iTrain <= totalTrain - 1; iTrain++)
            {
                int trainValue = inf.ReadInt32();
                Dictionary<int, int> subpathList = new Dictionary<int, int>();

                int totalSubpaths = inf.ReadInt32();
                for (int iSubpath = 0; iSubpath <= totalSubpaths - 1; iSubpath++)
                {
                    int subpathValue = inf.ReadInt32();
                    int indexValue = inf.ReadInt32();
                    subpathList.Add(subpathValue, indexValue);
                }
                TrainSubpathIndex.Add(trainValue, subpathList);
            }

            nextTrainSubpathIndex = inf.ReadInt32();
        }

        //================================================================================================//
        //
        // save
        //

        public void Save(BinaryWriter outf)
        {
            outf.Write(DeadlockIndex);
            outf.Write(AvailablePathList.Count);

            foreach (DeadlockPathInfo thisPathInfo in AvailablePathList)
            {
                thisPathInfo.Save(outf);
            }

            outf.Write(PathReferences.Count);
            foreach (KeyValuePair<int, List<int>> thisReference in PathReferences)
            {
                outf.Write(thisReference.Key);
                outf.Write(thisReference.Value.Count);

                foreach (int thisRefValue in thisReference.Value)
                {
                    outf.Write(thisRefValue);
                }
            }

            outf.Write(TrainReferences.Count);
            foreach (KeyValuePair<int, List<int>> thisReference in TrainReferences)
            {
                outf.Write(thisReference.Key);
                outf.Write(thisReference.Value.Count);

                foreach (int thisRefValue in thisReference.Value)
                {
                    outf.Write(thisRefValue);
                }
            }

            outf.Write(TrainLengthFit.Count);
            foreach (KeyValuePair<int, Dictionary<int, bool>> thisLengthFit in TrainLengthFit)
            {
                outf.Write(thisLengthFit.Key);
                outf.Write(thisLengthFit.Value.Count);

                foreach (KeyValuePair<int, bool> thisAvailValue in thisLengthFit.Value)
                {
                    outf.Write(thisAvailValue.Key);
                    outf.Write(thisAvailValue.Value);
                }
            }

            outf.Write(TrainOwnPath.Count);
            foreach (KeyValuePair<int, int> ownTrainInfo in TrainOwnPath)
            {
                outf.Write(ownTrainInfo.Key);
                outf.Write(ownTrainInfo.Value);
            }

            outf.Write(InverseInfo.Count);
            foreach (KeyValuePair<int, int> thisInfo in InverseInfo)
            {
                outf.Write(thisInfo.Key);
                outf.Write(thisInfo.Value);
            }

            outf.Write(TrainSubpathIndex.Count);
            foreach (KeyValuePair<int, Dictionary<int, int>> trainInfo in TrainSubpathIndex)
            {
                outf.Write(trainInfo.Key);
                outf.Write(trainInfo.Value.Count);

                foreach (KeyValuePair<int, int> subpathInfo in trainInfo.Value)
                {
                    outf.Write(subpathInfo.Key);
                    outf.Write(subpathInfo.Value);
                }
            }

            outf.Write(nextTrainSubpathIndex);
        }

        //================================================================================================//
        //
        // Create deadlock info from alternative path or find related info
        //

        public DeadlockInfo FindDeadlockInfo(ref Train.TCSubpathRoute partPath, Train.TCSubpathRoute mainPath, int startSectionIndex, int endSectionIndex)
        {
            TrackCircuitSection startSection = signalRef.TrackCircuitList[startSectionIndex];
            TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];

            int usedStartSectionRouteIndex = mainPath.GetRouteIndex(startSectionIndex, 0);
            int usedEndSectionRouteIndex = mainPath.GetRouteIndex(endSectionIndex, usedStartSectionRouteIndex);

            // check if there is a deadlock info defined with these as boundaries
            int startSectionDLReference = startSection.DeadlockReference;
            int endSectionDLReference = endSection.DeadlockReference;

            DeadlockInfo newDeadlockInfo = null;

            // if either end is within a deadlock, try if end of deadlock matches train path

            if (startSection.DeadlockBoundaries != null && startSection.DeadlockBoundaries.Count > 0)
            {
                int newStartSectionRouteIndex = -1;
                foreach (KeyValuePair<int, int> startSectionInfo in startSection.DeadlockBoundaries)
                {
                    DeadlockInfo existDeadlockInfo = signalRef.DeadlockInfoList[startSectionInfo.Key];
                    Train.TCSubpathRoute existPath = existDeadlockInfo.AvailablePathList[startSectionInfo.Value].Path;
                    newStartSectionRouteIndex = mainPath.GetRouteIndexBackward(existPath[0].TCSectionIndex, usedStartSectionRouteIndex);
                    if (newStartSectionRouteIndex < 0) // may be wrong direction - try end section
                    {
                        newStartSectionRouteIndex =
                            mainPath.GetRouteIndexBackward(existDeadlockInfo.AvailablePathList[startSectionInfo.Value].EndSectionIndex, usedStartSectionRouteIndex);
                    }

                    if (newStartSectionRouteIndex >= 0)
                    {
                        newDeadlockInfo = existDeadlockInfo;
                        break; // match found, stop searching
                    }
                }

                // no match found - train path is not on existing deadlock - do not accept

                if (newStartSectionRouteIndex < 0)
                {
                    return (null);
                }
                else
                {
                    // add sections to start of temp path
                    for (int iIndex = usedStartSectionRouteIndex - 1; iIndex >= newStartSectionRouteIndex; iIndex--)
                    {
                        Train.TCRouteElement newElement = mainPath[iIndex];
                        partPath.Insert(0, newElement);
                    }
                }
            }

            if (endSection.DeadlockBoundaries != null && endSection.DeadlockBoundaries.Count > 0)
            {
                int newEndSectionRouteIndex = -1;
                foreach (KeyValuePair<int, int> endSectionInfo in endSection.DeadlockBoundaries)
                {
                    DeadlockInfo existDeadlockInfo = signalRef.DeadlockInfoList[endSectionInfo.Key];
                    Train.TCSubpathRoute existPath = existDeadlockInfo.AvailablePathList[endSectionInfo.Value].Path;
                    newEndSectionRouteIndex = mainPath.GetRouteIndex(existPath[0].TCSectionIndex, usedEndSectionRouteIndex);
                    if (newEndSectionRouteIndex < 0) // may be wrong direction - try end section
                    {
                        newEndSectionRouteIndex =
                            mainPath.GetRouteIndex(existDeadlockInfo.AvailablePathList[endSectionInfo.Value].EndSectionIndex, usedEndSectionRouteIndex);
                    }

                    if (newEndSectionRouteIndex >= 0)
                    {
                        newDeadlockInfo = existDeadlockInfo;
                        break; // match found, stop searching
                    }
                }

                // no match found - train path is not on existing deadlock - do not accept

                if (newEndSectionRouteIndex < 0)
                {
                    return (null);
                }
                else
                {
                    // add sections to end of temp path
                    for (int iIndex = usedEndSectionRouteIndex + 1; iIndex <= newEndSectionRouteIndex; iIndex++)
                    {
                        Train.TCRouteElement newElement = mainPath[iIndex];
                        partPath.Add(newElement);
                    }
                }
            }

            // if no deadlock yet found

            if (newDeadlockInfo == null)
            {
                // if both references are equal, use existing information
                if (startSectionDLReference > 0 && startSectionDLReference == endSectionDLReference)
                {
                    newDeadlockInfo = signalRef.DeadlockInfoList[startSectionDLReference];
                }

                // if both references are null, check for existing references along route
                else if (startSectionDLReference < 0 && endSectionDLReference < 0)
                {
                    if (CheckNoOverlapDeadlockPaths(partPath, signalRef))
                    {
                        newDeadlockInfo = new DeadlockInfo(signalRef, startSection, endSection);
                        signalRef.DeadlockReference.Add(startSectionIndex, newDeadlockInfo.DeadlockIndex);
                        signalRef.DeadlockReference.Add(endSectionIndex, newDeadlockInfo.DeadlockIndex);

                        startSection.DeadlockReference = newDeadlockInfo.DeadlockIndex;
                        endSection.DeadlockReference = newDeadlockInfo.DeadlockIndex;
                    }
                    // else : overlaps existing deadlocks - will sort that out later //TODO DEADLOCK
                }
            }

            return (newDeadlockInfo);
        }

        //================================================================================================//
        //
        // add unnamed path to deadlock info
        // return : [0] index to path
        //          [1] > 0 : existing, < 0 : new

        public int[] AddPath(Train.TCSubpathRoute thisPath, int startSectionIndex)
        {
            // check if equal to existing path

            for (int iIndex = 0; iIndex <= AvailablePathList.Count - 1; iIndex++)
            {
                DeadlockPathInfo existPathInfo = AvailablePathList[iIndex];
                if (thisPath.EqualsPath(existPathInfo.Path))
                {
                    // check if path referenced from correct start position, else add reference
                    if (PathReferences.ContainsKey(startSectionIndex))
                    {
                        if (!PathReferences[startSectionIndex].Contains(iIndex))
                        {
                            PathReferences[startSectionIndex].Add(iIndex);
                        }
                    }
                    else
                    {
                        List<int> refSectionPaths = new List<int>();
                        refSectionPaths.Add(iIndex);
                        PathReferences.Add(startSectionIndex, refSectionPaths);
                    }

                    // return path
                    return (new int[2] { iIndex, 1 });
                }
            }

            // new path

            int newPathIndex = AvailablePathList.Count;
            DeadlockPathInfo newPathInfo = new DeadlockPathInfo(thisPath, newPathIndex);
            AvailablePathList.Add(newPathInfo);

            // add path to list of paths from this section
            List<int> thisSectionPaths;

            if (PathReferences.ContainsKey(startSectionIndex))
            {
                thisSectionPaths = PathReferences[startSectionIndex];
            }
            else
            {
                thisSectionPaths = new List<int>();
                PathReferences.Add(startSectionIndex, thisSectionPaths);
            }

            thisSectionPaths.Add(newPathIndex);

            // set references for intermediate sections
            SetIntermediateReferences(thisPath, newPathIndex);

            if (AvailablePathList.Count == 1) // if only one entry, set name to MAIN (first path is MAIN path)
            {
                newPathInfo.Name = "MAIN";
            }
            else
            {
                newPathInfo.Name = String.Concat("PASS", AvailablePathList.Count.ToString("00"));
            }

            // check for reverse path (through existing paths only)

            for (int iPath = 0; iPath <= AvailablePathList.Count - 2; iPath++)
            {
                if (thisPath.EqualsReversePath(AvailablePathList[iPath].Path))
                {
                    InverseInfo.Add(newPathIndex, iPath);
                    InverseInfo.Add(iPath, newPathIndex);
                }
            }

            return (new int[2] { newPathIndex, -1 }); // set new path found
        }

        //================================================================================================//
        //
        // add named path to deadlock info
        // return : [0] index to path
        //          [1] > 0 : existing, < 0 : new
        //

        public int[] AddPath(Train.TCSubpathRoute thisPath, int startSectionIndex, string thisName, string thisGroupName)
        {
            // check if equal to existing path and has same name

            for (int iIndex = 0; iIndex <= AvailablePathList.Count - 1; iIndex++)
            {
                DeadlockPathInfo existPathInfo = AvailablePathList[iIndex];
                if (thisPath.EqualsPath(existPathInfo.Path) && String.Compare(existPathInfo.Name, thisName) == 0)
                {
                    if (!String.IsNullOrEmpty(thisGroupName))
                    {
                        bool groupfound = false;
                        foreach (string groupName in existPathInfo.Groups)
                        {
                            if (String.Compare(groupName, thisGroupName) == 0)
                            {
                                groupfound = true;
                                break;
                            }
                        }

                        if (!groupfound) existPathInfo.Groups.Add(String.Copy(thisGroupName));
                    }

                    // check if path referenced from correct start position, else add reference
                    if (PathReferences.ContainsKey(startSectionIndex))
                    {
                        if (!PathReferences[startSectionIndex].Contains(iIndex))
                        {
                            PathReferences[startSectionIndex].Add(iIndex);
                        }
                    }
                    else
                    {
                        List<int> refSectionPaths = new List<int>();
                        refSectionPaths.Add(iIndex);
                        PathReferences.Add(startSectionIndex, refSectionPaths);
                    }

                    // return path
                    return (new int[2] { iIndex, 1 });
                }
            }

            // new path

            int newPathIndex = AvailablePathList.Count;
            DeadlockPathInfo newPathInfo = new DeadlockPathInfo(thisPath, newPathIndex);
            newPathInfo.Name = String.Copy(thisName);
            if (!String.IsNullOrEmpty(thisGroupName)) newPathInfo.Groups.Add(String.Copy(thisGroupName));

            AvailablePathList.Add(newPathInfo);

            // add path to list of path from this section
            List<int> thisSectionPaths;

            if (PathReferences.ContainsKey(startSectionIndex))
            {
                thisSectionPaths = PathReferences[startSectionIndex];
            }
            else
            {
                thisSectionPaths = new List<int>();
                PathReferences.Add(startSectionIndex, thisSectionPaths);
            }

            thisSectionPaths.Add(newPathIndex);

            // set references for intermediate sections
            SetIntermediateReferences(thisPath, newPathIndex);

            // check for reverse path (through existing paths only)

            for (int iPath = 0; iPath <= AvailablePathList.Count - 2; iPath++)
            {
                if (thisPath.EqualsReversePath(AvailablePathList[iPath].Path))
                {
                    InverseInfo.Add(newPathIndex, iPath);
                    InverseInfo.Add(iPath, newPathIndex);
                }
            }

            return (new int[2] { newPathIndex, -1 }); // return negative index to indicate new path
        }

        //================================================================================================//
        //
        // check if path has no conflict with overlapping deadlock paths
        // returns false if there is an overlap
        //

        public bool CheckNoOverlapDeadlockPaths(Train.TCSubpathRoute thisPath, Signals signalRef)
        {
            foreach (Train.TCRouteElement thisElement in thisPath)
            {
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (thisSection.DeadlockReference >= 0)
                {
                    return (false);
                }
            }
            return (true);
        }

        //================================================================================================//
        //
        // check if at least one valid path is available into a deadlock area
        // returns indices of available paths
        //

        public List<int> CheckDeadlockPathAvailability(TrackCircuitSection startSection, Train thisTrain)
        {
            List<int> useablePaths = new List<int>();

            // get end section for this train
            int endSectionIndex = GetEndSection(thisTrain);
            TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];

            // get list of paths which are available
            List<int> freePaths = GetFreePaths(thisTrain);

            // get all possible paths from train(s) in opposite direction
            List<int> usedRoutes = new List<int>();    // all routes allowed for any train
            List<int> commonRoutes = new List<int>();  // routes common to all trains
            List<int> singleRoutes = new List<int>();  // routes which are the single available route for trains which have one route only

            bool firstTrain = true;

            // loop through other trains
            foreach (int otherTrainNumber in endSection.DeadlockActives)
            {
                Train otherTrain = thisTrain.GetOtherTrainByNumber(otherTrainNumber);

                // TODO : find proper most matching path
                if (HasTrainAndSubpathIndex(otherTrain.Number, otherTrain.TCRoute.activeSubpath))
                {
                    List<int> otherFreePaths = GetFreePaths(otherTrain);
                    foreach (int iPath in otherFreePaths)
                    {
                        if (!usedRoutes.Contains(iPath)) usedRoutes.Add(iPath);
                        if (firstTrain)
                        {
                            commonRoutes.Add(iPath);
                        }
                    }

                    if (otherFreePaths.Count == 1)
                    {
                        singleRoutes.Add(otherFreePaths[0]);
                    }

                    for (int cPathIndex = commonRoutes.Count - 1; cPathIndex >= 0 && !firstTrain; cPathIndex--)
                    {
                        if (!otherFreePaths.Contains(commonRoutes[cPathIndex]))
                        {
                            commonRoutes.RemoveAt(cPathIndex);
                        }
                    }
                }
                else
                {
                    // for now : set all possible routes to used and single
                    foreach (int iroute in freePaths)
                    {
                        singleRoutes.Add(iroute);
                        usedRoutes.Add(iroute);
                    }
                }

                firstTrain = false;
            }

#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n=================\nTrain : " + thisTrain.Number.ToString() + "\n");
            File.AppendAllText(@"C:\Temp\deadlock.txt", "At Section : " + startSection.Index.ToString() + "\n");
            File.AppendAllText(@"C:\Temp\deadlock.txt", "To Section : " + endSection.Index.ToString() + "\n");
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n Available paths : \n");
            foreach (int avroute in PathReferences[startSection.Index])
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "Path index : " + avroute.ToString() + " : \n");
                Train.TCSubpathRoute thisPath = AvailablePathList[avroute].Path;
                foreach (Train.TCRouteElement thisElement in thisPath)
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", "   - Element : " + thisElement.TCSectionIndex.ToString() + "\n");
                }
            }
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n Available Inverse paths : \n");

            if (PathReferences.ContainsKey(endSection.Index))
            {
                foreach (int avroute in PathReferences[endSection.Index])
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", "Path index : " + avroute.ToString() + " : \n");
                    Train.TCSubpathRoute thisPath = AvailablePathList[avroute].Path;
                    foreach (Train.TCRouteElement thisElement in thisPath)
                    {
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "   - Element : " + thisElement.TCSectionIndex.ToString() + "\n");
                    }
                }
            }
            else
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\nNo Inverse paths available \n");
            }

            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n Inverse references : \n");
            foreach (KeyValuePair<int, int> inverseDetail in InverseInfo)
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", " Path : " + inverseDetail.Key.ToString() + " -> " + inverseDetail.Value.ToString() + "\n");
            }
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n Free paths : \n");
            foreach (int avroute in freePaths)
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "Path index : " + avroute.ToString() + " : \n");
                Train.TCSubpathRoute thisPath = AvailablePathList[avroute].Path;
                foreach (Train.TCRouteElement thisElement in thisPath)
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", "   - Element : " + thisElement.TCSectionIndex.ToString() + "\n");
                }
            }

            File.AppendAllText(@"C:\Temp\deadlock.txt", "\nMeeting : \n");

            foreach (int otherTrainNumber in endSection.DeadlockActives)
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "   Other train : " + otherTrainNumber.ToString() + "\n");
            }

            File.AppendAllText(@"C:\Temp\deadlock.txt", "\nUsed paths : \n");
            foreach (int iRoute in usedRoutes)
            {
                if (InverseInfo.ContainsKey(iRoute))
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = " + InverseInfo[iRoute].ToString() + "\n");
                }
                else
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = <no inverse> \n");
                }
            }

            File.AppendAllText(@"C:\Temp\deadlock.txt", "\nCommom paths : \n");
            foreach (int iRoute in commonRoutes)
            {
                if (InverseInfo.ContainsKey(iRoute))
                {
                File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = " + InverseInfo[iRoute].ToString() + "\n");
                }
                else
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = <no inverse> \n");
                }
            }

            File.AppendAllText(@"C:\Temp\deadlock.txt", "\nSingle paths : \n");
            foreach (int iRoute in singleRoutes)
            {
                if (InverseInfo.ContainsKey(iRoute))
                {
                File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = " + InverseInfo[iRoute].ToString() + "\n");
                }
                else
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " - route index : " + iRoute.ToString() + " = <no inverse> \n");
                }
            }
#endif
            // get inverse path indices to compare with this train's paths

            List<int> inverseUsedRoutes = new List<int>();
            List<int> inverseCommonRoutes = new List<int>();
            List<int> inverseSingleRoutes = new List<int>();

            foreach (int iPath in usedRoutes)
            {
                if (InverseInfo.ContainsKey(iPath))
                    inverseUsedRoutes.Add(InverseInfo[iPath]);
            }
            foreach (int iPath in commonRoutes)
            {
                if (InverseInfo.ContainsKey(iPath))
                    inverseCommonRoutes.Add(InverseInfo[iPath]);
            }
            foreach (int iPath in singleRoutes)
            {
                if (InverseInfo.ContainsKey(iPath))
                    inverseSingleRoutes.Add(InverseInfo[iPath]);
            }

            // if deadlock is awaited at other end : remove paths which would cause conflict
            if (endSection.CheckDeadlockAwaited(thisTrain.Number))
            {
#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n ++ Deadlock Awaited\n");
#endif
                // check if this train has any route not used by trains from other end

                foreach (int iPath in freePaths)
                {
                    if (!inverseUsedRoutes.Contains(iPath)) useablePaths.Add(iPath);
                }

                if (useablePaths.Count > 0) return (useablePaths); // unused paths available

                // check if any path remains if common paths are excluded

                if (inverseCommonRoutes.Count >= 1) // there are common routes, so other routes may be used
                {
                    foreach (int iPath in freePaths)
                    {
                        if (!inverseCommonRoutes.Contains(iPath)) useablePaths.Add(iPath);
                    }

                    if (useablePaths.Count > 0)
                    {
#if DEBUG_DEADLOCK
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- Usable paths (checked common) : \n");
                        foreach (int iRoute in useablePaths)
                        {
                            File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                        }
#endif
                        return (useablePaths);
                    }
                }

                // check if any path remains if all required single paths are excluded

                if (inverseSingleRoutes.Count >= 1) // there are single paths
                {
                    foreach (int iPath in freePaths)
                    {
                        if (!inverseSingleRoutes.Contains(iPath)) useablePaths.Add(iPath);
                    }

                    if (useablePaths.Count > 0)
                    {
#if DEBUG_DEADLOCK
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- Usable paths (after checking single) : \n");
                        foreach (int iRoute in useablePaths)
                        {
                            File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                        }
#endif
                        return (useablePaths);
                    }
                }

                // no path available without conflict - but if deadlock also awaited on this end, proceed anyway (otherwise everything gets stuck)

                if (startSection.DeadlockAwaited.Count >= 1)
                {
#if DEBUG_DEADLOCK
                    File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- Free paths (deadlock awaited this end) : \n");
                    foreach (int iRoute in freePaths)
                    {
                        File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                    }
#endif
                    return (freePaths); // may use any path in this situation
                }

                // no path available - return empty list

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- No paths available) : \n");
                foreach (int iRoute in useablePaths)
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                }
#endif
                return (useablePaths);
            }

            // no deadlock awaited at other end : check if there is any single path set, if so exclude those to avoid conflict
            else
            {
#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n ++ No Deadlock Awaited\n");
#endif
                // check if any path remains if all required single paths are excluded

                if (inverseSingleRoutes.Count >= 1) // there are single paths
                {
                    foreach (int iPath in freePaths)
                    {
                        if (!inverseSingleRoutes.Contains(iPath)) useablePaths.Add(iPath);
                    }

                    if (useablePaths.Count > 0)
                    {
#if DEBUG_DEADLOCK
                        File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- Usable paths (after checking singles) : \n");
                        foreach (int iRoute in useablePaths)
                        {
                            File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                        }
#endif
                        return (useablePaths);
                    }
                }

                // no single path conflicts - so all free paths are available

#if DEBUG_DEADLOCK
                File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n ----- No single paths conflicts - all paths available : \n");
                foreach (int iRoute in freePaths)
                {
                    File.AppendAllText(@"C:\Temp\deadlock.txt", " Route : " + iRoute.ToString() + "\n");
                }
#endif
                return (freePaths);
            }
        }

        //================================================================================================//
        //
        // get valid list of indices related available for specific train / subpath index
        //

        public List<int> GetValidPassingPaths(int trainNumber, int sublistRef, bool allowPublic)
        {
            List<int> foundIndices = new List<int>();

            for (int iPath = 0; iPath <= AvailablePathList.Count - 1; iPath++)
            {
                DeadlockPathInfo thisPathInfo = AvailablePathList[iPath];
                int trainSubpathIndex = GetTrainAndSubpathIndex(trainNumber, sublistRef);
                if (thisPathInfo.AllowedTrains.Contains(trainSubpathIndex) || (thisPathInfo.AllowedTrains.Contains(-1) && allowPublic))
                {
                    foundIndices.Add(iPath);
                }
            }

            return (foundIndices);
        }

        //================================================================================================//
        //
        // check availability of passing paths
        // return list of paths which are free
        //

        public List<int> GetFreePaths(Train thisTrain)
        {
            List<int> freePaths = new List<int>();

            int thisTrainAndSubpathIndex = GetTrainAndSubpathIndex(thisTrain.Number, thisTrain.TCRoute.activeSubpath);
            for (int iPath = 0; iPath <= TrainReferences[thisTrainAndSubpathIndex].Count - 1; iPath++)
            {
                int pathIndex = TrainReferences[thisTrainAndSubpathIndex][iPath];
                DeadlockPathInfo altPathInfo = AvailablePathList[pathIndex];
                Train.TCSubpathRoute altPath = altPathInfo.Path;

                // check all sections upto and including last used index, but do not check first junction section

                bool pathAvail = true;
                for (int iElement = 1; iElement <= altPathInfo.LastUsefullSectionIndex; iElement++)
                {
                    TrackCircuitSection thisSection = signalRef.TrackCircuitList[altPath[iElement].TCSectionIndex];
                    if (!thisSection.IsAvailable(thisTrain.routedForward))
                    {
                        pathAvail = false;
                        break;
                    }
                }

                if (pathAvail) freePaths.Add(pathIndex);
            }

            return (freePaths);
        }

        //================================================================================================//
        //
        // set deadlock info references for intermediate sections
        //

        public int SelectPath(List<int> availableRoutes, Train thisTrain, ref int endSectionIndex)
        {
#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n\n**** For train " + thisTrain.Number.ToString() + " Select route from : \n");
            foreach (int iRoute in availableRoutes)
            {
                File.AppendAllText(@"C:\Temp\deadlock.txt", "Available route : " + iRoute.ToString() + "\n");
            }
#endif
            int selectedPathNofit = -1;
            int selectedPathFit = -1;

            int defaultPath = 0;

            bool checkedMain = false;
            bool checkedOwn = false;

            endSectionIndex = GetEndSection(thisTrain);
            TrackCircuitSection endSection = signalRef.TrackCircuitList[endSectionIndex];

            bool preferMain = true;
            // if deadlock actives : main least preferred
            if (endSection.DeadlockActives.Count > 0)
            {
                preferMain = false;
                checkedMain = true; // consider main as checked
            }

            // check if own path is also main path - if so, do not check it separately

            int indexTrainAndSubroute = GetTrainAndSubpathIndex(thisTrain.Number, thisTrain.TCRoute.activeSubpath);
            int ownPathIndex = TrainOwnPath[indexTrainAndSubroute];
            defaultPath = ownPathIndex;

            if (String.Compare(AvailablePathList[ownPathIndex].Name, "MAIN") == 0)
            {
                checkedOwn = true; // do not check own path separately
            }

            // get train fit list
            Dictionary<int, bool> trainFitInfo = TrainLengthFit[indexTrainAndSubroute];

            // loop through all available paths

            for (int iPath = 0; iPath <= availableRoutes.Count - 1; iPath++)
            {
                int pathIndex = availableRoutes[iPath];
                DeadlockPathInfo pathInfo = AvailablePathList[pathIndex];
                bool trainFitsInSection = trainFitInfo[pathIndex];

                // check for OWN
                if (!checkedOwn && pathIndex == ownPathIndex)
                {
                    checkedOwn = true;
                    if (trainFitsInSection)
                    {
                        selectedPathFit = pathIndex;
                        break; // if train fits in own path, break
                    }

                    selectedPathNofit = pathIndex;
                    if (checkedMain && selectedPathFit > 0) break;  // if doesnt fit but main has been checked and train fits somewhere, break
                }

                // check for MAIN
                if (String.Compare(pathInfo.Name, "MAIN") == 0)
                {
                    checkedMain = true;
                    if (trainFitsInSection)
                    {
                        selectedPathFit = pathIndex;
                        if (checkedOwn && preferMain) break;  // if fits and own has been checked and main prefered - break
                    }
                    else
                    {
                        if (!checkedOwn || selectedPathNofit < 0 || preferMain)  // if own has not been checked
                        {
                            selectedPathNofit = pathIndex;
                        }
                    }
                }

                // check for others
                else
                {
                    if (trainFitsInSection) // if train fits
                    {
                        selectedPathFit = pathIndex;
                        if (checkedMain || checkedOwn)
                        {
                            break;  // main and own allready checked so no need to look further
                        }
                    }
                    else
                    {
                        if ((!checkedOwn && !checkedMain) || !preferMain) // set as option if own and main both not checked or main not prefered
                        {
                            selectedPathNofit = pathIndex;
                        }
                    }
                }
            }

#if DEBUG_DEADLOCK
            File.AppendAllText(@"C:\Temp\deadlock.txt", " Selected path (fit)   : " + selectedPathFit.ToString() + "\n");
            File.AppendAllText(@"C:\Temp\deadlock.txt", " Selected path (nofit) : " + selectedPathNofit.ToString() + "\n");
            File.AppendAllText(@"C:\Temp\deadlock.txt", "\n****\n\n");
#endif
            // Sometimes selectedPathFit nor selectedPathNofit gets new value, which is wrong and will induce an
            // IndexOutOfRangeException, but I can't find out why that happens, so here is a warning message when it
            // happens, to at least find out which train, and passing path that triggers this bug.
            if (selectedPathFit < 0 && selectedPathNofit < 0 && defaultPath < 0)
                Trace.TraceWarning("Path can't be selected for train {0} at end-section index {1}", thisTrain.Name, endSectionIndex);
            return (selectedPathFit >= 0 ? selectedPathFit : selectedPathNofit >= 0 ? selectedPathNofit : defaultPath); // return fit path if set else no-fit path if set else default path
        }

        //================================================================================================//
        //
        // get end section index for deadlock area for a particular train
        //

        public int GetEndSection(Train thisTrain)
        {
            int thisTrainAndSubpathIndex = GetTrainAndSubpathIndex(thisTrain.Number, thisTrain.TCRoute.activeSubpath);
            if (!TrainReferences.ContainsKey(thisTrainAndSubpathIndex))
            {
                Trace.TraceWarning("Multiple passing paths at the same location, without common branch out, or return switch. Check the passing paths for Train name: {0} (number: {1}), and other train's paths, which have passing paths at the same locations", thisTrain.Name, thisTrain.Number);
            }
            int pathIndex = TrainReferences[thisTrainAndSubpathIndex][0];
            DeadlockPathInfo pathInfo = AvailablePathList[pathIndex];
            return (pathInfo.EndSectionIndex);
        }

        //================================================================================================//
        //
        // set deadlock info references for intermediate sections
        //

        public void SetIntermediateReferences(Train.TCSubpathRoute thisPath, int pathIndex)
        {
            for (int iElement = 1; iElement <= thisPath.Count - 2; iElement++) // loop through path excluding first and last section
            {
                Train.TCRouteElement thisElement = thisPath[iElement];
                TrackCircuitSection thisSection = signalRef.TrackCircuitList[thisElement.TCSectionIndex];
                if (thisSection.DeadlockBoundaries == null)
                {
                    thisSection.DeadlockBoundaries = new Dictionary<int, int>();
                }

                if (!thisSection.DeadlockBoundaries.ContainsKey(DeadlockIndex))
                {
                    thisSection.DeadlockBoundaries.Add(DeadlockIndex, pathIndex);
                }
            }
        }

        //================================================================================================//
        //
        // get index value for specific train/subpath combination
        // if set, return value
        // if not set, generate value, set value and return value
        //

        public int GetTrainAndSubpathIndex(int trainNumber, int subpathIndex)
        {
            if (TrainSubpathIndex.ContainsKey(trainNumber))
            {
                Dictionary<int, int> subpathList = TrainSubpathIndex[trainNumber];
                if (subpathList.ContainsKey(subpathIndex))
                {
                    return (subpathList[subpathIndex]);
                }
            }

            int newIndex = ++nextTrainSubpathIndex;
            Dictionary<int, int> newSubpathList;
            if (TrainSubpathIndex.ContainsKey(trainNumber))
            {
                newSubpathList = TrainSubpathIndex[trainNumber];
            }
            else
            {
                newSubpathList = new Dictionary<int, int>();
                TrainSubpathIndex.Add(trainNumber, newSubpathList);
            }

            newSubpathList.Add(subpathIndex, newIndex);

            return (newIndex);
        }

        //================================================================================================//
        //
        // check index value for specific train/subpath combination
        // if set, return value
        // if not set, generate value, set value and return value
        //

        public bool HasTrainAndSubpathIndex(int trainNumber, int subpathIndex)
        {
            if (TrainSubpathIndex.ContainsKey(trainNumber))
            {
                Dictionary<int, int> subpathList = TrainSubpathIndex[trainNumber];
                if (subpathList.ContainsKey(subpathIndex))
                {
                    return (true);
                }
            }
            return (false);
        }

        //================================================================================================//
        //
        // check index value for specific train/subpath combination
        // if set, return value
        // if not set, generate value, set value and return value
        //

        public bool RemoveTrainAndSubpathIndex(int trainNumber, int subpathIndex)
        {
            if (TrainSubpathIndex.ContainsKey(trainNumber))
            {
                Dictionary<int, int> subpathList = TrainSubpathIndex[trainNumber];
                if (subpathList.ContainsKey(subpathIndex))
                {
                    subpathList.Remove(subpathIndex);
                }
                if (subpathList.Count <= 0)
                {
                    TrainSubpathIndex.Remove(trainNumber);
                }
            }
            return (false);
        }

        //================================================================================================//
        //
        // Insert train reference details
        //

        public int SetTrainDetails(int trainNumber, int subpathRef, float trainLength, Train.TCSubpathRoute subpath, int elementRouteIndex)
        {
            Train.TCSubpathRoute partPath = null;  // retreived route of train through deadlock area

            // search if trains path has valid equivalent

            if (elementRouteIndex <= 0 || elementRouteIndex >= subpath.Count)
            {
                Trace.TraceWarning("Invalid route element in SetTrainDetails : value =  {0}, max. is {1}", elementRouteIndex, subpath.Count);
                return (-1);
            }

            int trainSubpathIndex = GetTrainAndSubpathIndex(trainNumber, subpathRef);
            int sectionIndex = subpath[elementRouteIndex].TCSectionIndex;
            int[] matchingPath = SearchMatchingFullPath(subpath, sectionIndex, elementRouteIndex);

            // matchingPath[0] == 1 : path runs short of all available paths - train ends within area - no alternative path available
            if (matchingPath[0] == 1)
            {
                // if no other paths for this reference, remove train/subpath reference from table
                if (!TrainReferences.ContainsKey(trainSubpathIndex))
                {
                    RemoveTrainAndSubpathIndex(trainNumber, subpathRef);
                }
                return (-1);
            }

            // matchingPath[0] == 2 : path runs through area but has no match - insert path for this train only (no inverse inserted)
            // matchingPath[1] = end section index in route

            if (matchingPath[0] == 2)
            {
                partPath = new Train.TCSubpathRoute(subpath, elementRouteIndex, matchingPath[1]);
                int[] pathReference = AddPath(partPath, sectionIndex);
                DeadlockPathInfo thisPathInfo = AvailablePathList[pathReference[0]];

                Dictionary<int, float> pathEndAndLengthInfo = partPath.GetUsefullLength(0.0f, signalRef, -1, -1);
                KeyValuePair<int, float> pathEndAndLengthValue = pathEndAndLengthInfo.ElementAt(0);
                thisPathInfo.UsefullLength = pathEndAndLengthValue.Value;
                thisPathInfo.LastUsefullSectionIndex = pathEndAndLengthValue.Key;
                thisPathInfo.EndSectionIndex = subpath[matchingPath[1]].TCSectionIndex;
                thisPathInfo.Name = String.Empty;  // path has no name

                thisPathInfo.AllowedTrains.Add(trainSubpathIndex);
                TrainOwnPath.Add(trainSubpathIndex, pathReference[0]);
            }

            // matchingPath[0] == 3 : path runs through area but no valid path available or possible - remove train index as train has no alternative paths at this location
            else if (matchingPath[0] == 3)
            {
                RemoveTrainAndSubpathIndex(trainNumber, subpathRef);
                return (matchingPath[1]);
            }

            // otherwise matchingPath [1] is matching path - add track details if not yet set

            else
            {
                DeadlockPathInfo thisPathInfo = AvailablePathList[matchingPath[1]];
                if (!thisPathInfo.AllowedTrains.Contains(trainSubpathIndex))
                {
                    thisPathInfo.AllowedTrains.Add(trainSubpathIndex);
                }
                TrainOwnPath.Add(trainSubpathIndex, matchingPath[1]);
            }

            // set cross-references to allowed track entries for easy reference

            List<int> availPathList;

            if (TrainReferences.ContainsKey(trainSubpathIndex))
            {
                availPathList = TrainReferences[trainSubpathIndex];
            }
            else
            {
                availPathList = new List<int>();
                TrainReferences.Add(trainSubpathIndex, availPathList);
            }

            Dictionary<int, bool> thisTrainFitList;
            if (TrainLengthFit.ContainsKey(trainSubpathIndex))
            {
                thisTrainFitList = TrainLengthFit[trainSubpathIndex];
            }
            else
            {
                thisTrainFitList = new Dictionary<int, bool>();
                TrainLengthFit.Add(trainSubpathIndex, thisTrainFitList);
            }

            for (int iPath = 0; iPath <= AvailablePathList.Count - 1; iPath++)
            {
                DeadlockPathInfo thisPathInfo = AvailablePathList[iPath];

                if (thisPathInfo.AllowedTrains.Contains(-1) || thisPathInfo.AllowedTrains.Contains(trainSubpathIndex))
                {
                    if (this.PathReferences[sectionIndex].Contains(iPath)) // path from correct end
                    {
                        availPathList.Add(iPath);

                        bool trainFit = (trainLength < thisPathInfo.UsefullLength);
                        thisTrainFitList.Add(iPath, trainFit);
                    }
                }
            }

            // get end section from first valid path

            partPath = new Train.TCSubpathRoute(AvailablePathList[availPathList[0]].Path);
            int lastSection = partPath[partPath.Count - 1].TCSectionIndex;
            int returnIndex = subpath.GetRouteIndex(lastSection, elementRouteIndex);
            return (returnIndex);

        }

        //================================================================================================//
        //
        // Search matching path from full route path
        //
        // return : [0] = 0 : matching path, [1] = matching path index
        //          [0] = 1 : no matching path and route does not contain any of the end sections (route ends within area)
        //          [0] = 2 : no matching path but route does run through area, [1] contains end section index
        //          [0] = 3 : no matching path in required direction but route does run through area, [1] contains end section index
        //

        public int[] SearchMatchingFullPath(Train.TCSubpathRoute fullPath, int startSectionIndex, int startSectionRouteIndex)
        {
            int[] matchingValue = new int[2] { 0, 0 };
            int foundMatchingEndRouteIndex = -1;
            int matchingPath = -1;

            // paths available from start section
            if (PathReferences.ContainsKey(startSectionIndex))
            {
                List<int> availablePaths = PathReferences[startSectionIndex];

                // search through paths from this section

                for (int iPath = 0; iPath <= availablePaths.Count - 1; iPath++)
                {
                    // extract path, get indices in train path
                    Train.TCSubpathRoute testPath = AvailablePathList[availablePaths[iPath]].Path;
                    int endSectionIndex = AvailablePathList[availablePaths[iPath]].EndSectionIndex;
                    int endSectionRouteIndex = fullPath.GetRouteIndex(endSectionIndex, startSectionRouteIndex);

                    // can only be matching path if endindex > 0 and endindex != startindex (if wrong way path, endindex = startindex)
                    if (endSectionRouteIndex > 0 && endSectionRouteIndex != startSectionRouteIndex)
                    {
                        Train.TCSubpathRoute partPath = new Train.TCSubpathRoute(fullPath, startSectionRouteIndex, endSectionRouteIndex);

                        // test route
                        if (partPath.EqualsPath(testPath))
                        {
                            matchingPath = availablePaths[iPath];
                            break;
                        }

                        // set end index (if not yet found)
                        if (foundMatchingEndRouteIndex < 0)
                        {
                            foundMatchingEndRouteIndex = endSectionRouteIndex;
                        }
                    }

                    // no matching end index - check train direction
                    else
                    {
                        // check direction
                        int areadirection = AvailablePathList[availablePaths[0]].Path[0].Direction;
                        int traindirection = fullPath[startSectionRouteIndex].Direction;

                        // train has same direction - check if end of path is really within the path
                        if (areadirection == traindirection)
                        {
                            int pathEndSection = fullPath[fullPath.Count - 1].TCSectionIndex;
                            if (testPath.GetRouteIndex(pathEndSection, 0) >= 0) // end point is within section
                            {
                                matchingValue[0] = 1;
                                matchingValue[1] = 0;
                                return (matchingValue);
                            }
                        }
                        else  //if wrong direction, train exits area at this location//
                        {
                            matchingValue[0] = 3;
                            matchingValue[1] = startSectionRouteIndex + 1;
                            return (matchingValue);
                        }
                    }
                }
            }

            // no paths available from start section, check if end section of paths matches start section
            else
            {
                if (startSectionIndex == AvailablePathList[0].EndSectionIndex)
                {
                    int matchingEndIndex = fullPath.GetRouteIndex(AvailablePathList[0].Path[0].TCSectionIndex, startSectionRouteIndex);
                    if (matchingEndIndex > 0)
                    {
                        matchingValue[0] = 2;
                        matchingValue[1] = matchingEndIndex;
                    }
                    else
                    {
                        matchingValue[0] = 3;
                        matchingValue[1] = startSectionRouteIndex + 1;
                    }
                    return (matchingValue);
                }
            }

            if (matchingPath >= 0)
            {
                matchingValue[0] = 0;
                matchingValue[1] = matchingPath;
            }
            else if (foundMatchingEndRouteIndex >= 0)
            {
                matchingValue[0] = 2;
                matchingValue[1] = foundMatchingEndRouteIndex;
            }
            else
            {
                matchingValue[0] = 3;
                matchingValue[1] = startSectionRouteIndex + 1;
            }

            return (matchingValue);
        }

    } // end DeadlockInfo class

    //================================================================================================//
    //
    // DeadlockPath Info Object
    //
    //================================================================================================//

    public class DeadlockPathInfo
    {
        public Train.TCSubpathRoute Path;      // actual path
        public string Name;                    // name of path
        public List<string> Groups;            // groups of which this path is a part
        public float UsefullLength;            // path usefull length
        public int EndSectionIndex;            // index of linked end section
        public int LastUsefullSectionIndex;    // Index in Path for last section which can be used before stop position
        public List<int> AllowedTrains;        // list of train for which path is valid (ref. is train/subpath index); -1 indicates public path

        //================================================================================================//
        //
        // Constructor
        //

        public DeadlockPathInfo(Train.TCSubpathRoute thisPath, int pathIndex)
        {
            Path = new Train.TCSubpathRoute(thisPath);
            Name = String.Empty;
            Groups = new List<string>();

            UsefullLength = 0.0f;
            EndSectionIndex = -1;
            LastUsefullSectionIndex = -1;
            AllowedTrains = new List<int>();

            Path[0].UsedAlternativePath = pathIndex;
        }

        //================================================================================================//
        //
        // Constructor for restore
        //

        public DeadlockPathInfo(BinaryReader inf)
        {
            Path = new Train.TCSubpathRoute(inf);
            Name = inf.ReadString();

            Groups = new List<string>();
            int totalGroups = inf.ReadInt32();
            for (int iGroup = 0; iGroup <= totalGroups - 1; iGroup++)
            {
                string thisGroup = inf.ReadString();
                Groups.Add(thisGroup);
            }

            UsefullLength = inf.ReadSingle();
            EndSectionIndex = inf.ReadInt32();
            LastUsefullSectionIndex = inf.ReadInt32();

            AllowedTrains = new List<int>();
            int totalIndex = inf.ReadInt32();
            for (int iIndex = 0; iIndex <= totalIndex - 1; iIndex++)
            {
                int thisIndex = inf.ReadInt32();
                AllowedTrains.Add(thisIndex);
            }
        }

        //================================================================================================//
        //
        // save
        //

        public void Save(BinaryWriter outf)
        {
            Path.Save(outf);
            outf.Write(Name);

            outf.Write(Groups.Count);
            foreach (string groupName in Groups)
            {
                outf.Write(groupName);
            }

            outf.Write(UsefullLength);
            outf.Write(EndSectionIndex);
            outf.Write(LastUsefullSectionIndex);

            outf.Write(AllowedTrains.Count);
            foreach (int thisIndex in AllowedTrains)
            {
                outf.Write(thisIndex);
            }
        }
    }

}
