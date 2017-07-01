using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace A17FilePawnGC
{
	public class WorldPawnsDetour : WorldPawns
	{
		private static readonly Action<WorldPawns, Pawn> DiscardPawn = Utils.GetMethodInvoker<WorldPawns, Pawn>("DiscardPawn");

		private static readonly HashSet<Pawn> markedPawns = new HashSet<Pawn>();

		private static readonly List<Pawn> tmpPawnsToTick = new List<Pawn>();


		private static readonly Func<WorldPawns, HashSet<Pawn>> pawnsAliveGet = Utils.GetFieldAccessor<WorldPawns, HashSet<Pawn>>("pawnsAlive");

		private static readonly FieldInfo pawnsAliveInfo = typeof(WorldPawns).GetField("pawnsAlive", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);

		private HashSet<Pawn> pawnsAlive { get { return pawnsAliveGet(this); } set { pawnsAliveInfo.SetValue(this, value); } }


		private static readonly Func<WorldPawns, HashSet<Pawn>> pawnsDeadGet = Utils.GetFieldAccessor<WorldPawns, HashSet<Pawn>>("pawnsDead");

		private static readonly FieldInfo pawnsDeadInfo = typeof(WorldPawns).GetField("pawnsDead", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);

		private HashSet<Pawn> pawnsDead { get { return pawnsDeadGet(this); } set { pawnsDeadInfo.SetValue(this, value); } }

		private static readonly Func<Pawn_RelationsTracker, HashSet<Pawn>> pawnsWithDirectRelationsWithMeGet =
			Utils.GetFieldAccessor<Pawn_RelationsTracker, HashSet<Pawn>>("pawnsWithDirectRelationsWithMe");

		[DetourMember]
		public new void WorldPawnsTick()
		{
			tmpPawnsToTick.Clear();
			foreach (Pawn current in pawnsAlive)
			{
				if (!current.Dead && !current.Destroyed)
				{
					tmpPawnsToTick.Add(current);
				}
			}
			for (int i = 0; i < tmpPawnsToTick.Count; i++)
			{
				tmpPawnsToTick[i].Tick();
				if (!tmpPawnsToTick[i].Dead && !tmpPawnsToTick[i].Destroyed && tmpPawnsToTick[i].IsHashIntervalTick(7500) && !tmpPawnsToTick[i].IsCaravanMember() && !PawnUtility.IsTravelingInTransportPodWorldObject(tmpPawnsToTick[i]))
				{
					TendUtility.DoTend(null, tmpPawnsToTick[i], null);
				}
			}
			tmpPawnsToTick.Clear();
			foreach (Pawn current2 in pawnsAlive)
			{
				if (current2.Dead || current2.Destroyed)
				{
					if (current2.Discarded)
					{
						Log.Error("World pawn " + current2 + " has been discarded while still being a world pawn. This should never happen, because discard destroy mode means that the pawn is no longer managed by anything. Pawn should have been removed from the world first.");
					}
					else
					{
						pawnsDead.Add(current2);
					}
				}
			}
			pawnsAlive.RemoveWhere(x => x.Dead || x.Destroyed);

			if (Find.TickManager.TicksGame % 15000 == 0)
			{
				var sw = Stopwatch.StartNew();
				DoPawnGC();
				sw.Stop();
#if DEBUG
				Log.Error("Pawn GC elapsed time: " + sw.ElapsedTicks + " ( " + sw.ElapsedMilliseconds + "ms)");
#endif
			}
		}

	#if DEBUG
		private static readonly List<string> PawnNodes = new List<string>();

		private static readonly Dictionary<int, HashSet<int>> PawnRelationsPrinted = new Dictionary<int, HashSet<int>>();
	#endif
		private void DoPawnGC()
		{
			markedPawns.Clear();

#if DEBUG
			PawnRelationsPrinted.Clear();
			PawnNodes.Clear();
			PawnNodes.Add("digraph { rankdir=LR;");
			PawnNodes.Add("subgraph cluster_0 {");
			PawnNodes.Add("label=\"Important\";");
#endif

			foreach (var pawn in PawnsFinder.AllMapsAndWorld_AliveOrDead)
			{
				if (!IsImportantKeepPawn(pawn)) { continue; }
#if DEBUG
				PawnNodes.Add(pawn.ThingID + " [ color=\"red\"];");
#endif
				markedPawns.Add(pawn);
				MarkPawnRelationDescendants(pawn, true);
			}

			foreach (var pawn in markedPawns.ToArray())
			{
				if (IsImportantKeepPawn(pawn))
				{
					MarkPawnThoughtDescendants(pawn, false);
				}
			}
#if DEBUG
			PawnNodes.Add("}\nsubgraph cluster_1 {");
			PawnNodes.Add("label=\"Unimportant\";");
#endif
			foreach (var pawn in PawnsFinder.AllMapsAndWorld_AliveOrDead)
			{
				if (markedPawns.Contains(pawn) || !IsUnimportantKeepPawn(pawn)) { continue; }
#if DEBUG
				PawnNodes.Add(pawn.ThingID + ";");
#endif
				markedPawns.Add(pawn);
				MarkPawnRelationDescendants(pawn, false);
			}



#if DEBUG
			PawnNodes.Add("}");
			PawnNodes.Add("}");
			Log.Message(string.Join("\n", PawnNodes.ToArray()));
#endif

			//Now, we've marked every pawn that we want to keep...
			var whatWeTossed = new List<string>();

			foreach (var pawn in this.AllPawnsAliveOrDead.ToArray())
			{
				if (markedPawns.Contains(pawn))
				{
					var memories = pawn.needs?.mood?.thoughts.memories.Memories;
					if (memories != null)
					{
						for (int i = memories.Count - 1; i >= 0; i--)
						{
							if (!markedPawns.Contains(memories[i].otherPawn))
							{
								memories[i] = memories[memories.Count - 1];
								memories.RemoveLast();
							}
						}
					}
					continue;
				}

				whatWeTossed.Add("Tossing " + pawn + " (shouldKeep reason: " + KeepReason(pawn) + ")");

				if (Contains(pawn))
				{
					RemovePawn(pawn);
					DiscardPawn(this, pawn);
				}
			}

			if (Enumerable.Any(whatWeTossed)) { Log.Message(string.Join("\n", whatWeTossed.ToArray())); }

			markedPawns.Clear();
		}

#region Marking

		private static void MarkPawnRelationDescendants(Pawn pawn, bool keepHiddenRelations)
		{
			if (!pawn.RaceProps.IsFlesh) return;
			if (!pawn.RaceProps.Humanlike && pawn.Name == null || pawn.Name.Numerical) return;
			foreach (var relatedPawn in pawn.relations.DirectRelations.Select(r => r.otherPawn).Concat(pawnsWithDirectRelationsWithMeGet(pawn.relations)).Distinct())
			{
				PrintPawnRelationship(pawn, relatedPawn);
				if (markedPawns.Contains(relatedPawn)) continue;
				markedPawns.Add(relatedPawn);
				//Treating pawns with no corpse as never seen lets us prune branches connected by raiders whose corpses have been destroyed
				//This compensates for the relationships generated in raids, so things don't grow out of hand over time
				if (keepHiddenRelations || (relatedPawn.relations.everSeenByPlayer && PawnExistsInWorld(relatedPawn)))
				{
#if DEBUG
					if (!relatedPawn.relations.everSeenByPlayer)
					{
						PawnNodes.Add(relatedPawn.ThingID + " [ color=\"green\"];");
					}
#endif
					MarkPawnRelationDescendants(relatedPawn, false);
				}
			}
		}

		private static bool PawnExistsInWorld(Pawn pawn) => !pawn.Dead || pawn.Corpse != null;

		//Took out the recursion here, it kept far too many pawns rooted.
		private static void MarkPawnThoughtDescendants(Pawn pawn, bool checkRelations)
		{
			//if (checkRelations && pawn.RaceProps.IsFlesh)
			//{
			//	if (pawn.RaceProps.Humanlike || pawn.Name != null && !pawn.Name.Numerical)
			//	{
			//		foreach (var relatedPawn in pawn.relations.RelatedPawns)
			//		{
			//			PrintPawnRelationship(pawn, relatedPawn, true);
			//			if (markedPawns.Contains(relatedPawn)) continue;
			//			markedPawns.Add(relatedPawn);
			//			if (relatedPawn.relations.everSeenByPlayer)
			//			{
			//				MarkPawnThoughtDescendants(relatedPawn, true);
			//			}
			//		}
			//	}
			//}
			if (pawn.needs?.mood != null)
			{
				foreach (var memory in pawn.needs.mood.thoughts.memories.Memories)
				{

					if (memory.otherPawn != null && !markedPawns.Contains(memory.otherPawn))
					{
#if DEBUG
						PawnNodes.Add(pawn.ThingID + "->" + memory.otherPawn.ThingID + " [color=grey];");
#endif
						markedPawns.Add(memory.otherPawn);
						//MarkPawnThoughtDescendants(memory.otherPawn, true);
					}
				}
			}
		}
		
		static void PrintPawnRelationship(Pawn pawn, Pawn relatedPawn)
		{
#if DEBUG
			if(PawnRelationsPrinted.ContainsKey(relatedPawn.thingIDNumber) && PawnRelationsPrinted[relatedPawn.thingIDNumber].Contains(pawn.thingIDNumber))
			{
				return;
			}
			if (!PawnRelationsPrinted.ContainsKey(pawn.thingIDNumber))
			{
				PawnRelationsPrinted[pawn.thingIDNumber] = new HashSet<int>();
			}
			else if(PawnRelationsPrinted[pawn.thingIDNumber].Contains(relatedPawn.thingIDNumber))
			{
				return;
			}
			PawnNodes.Add(pawn.ThingID + "->" + relatedPawn.ThingID + "[ label=\"" + pawn.GetMostImportantRelation(relatedPawn).GetGenderSpecificLabel(relatedPawn) + (pawn.Dead ? "\" color=orange ];" : "\" ];"));

			PawnRelationsPrinted[pawn.thingIDNumber].Add(relatedPawn.thingIDNumber);
#endif
		}

		//The KeepPawns functions implement most of the checks in WorldPawns.ShouldKeep
		//the remainder should be covered by the marking logic

		//"Important" pawns means we want to keep relationships even if the player has never seen them
		private static bool IsImportantKeepPawn(Pawn pawn, bool skipHumanlike = false)
		{
			if (pawn.Discarded) { return false; }
			//Don't ever care about hidden animal relationships (don't think they should exist anyway)
			if (!skipHumanlike && !pawn.RaceProps.Humanlike) { return false; }
			//Colonists
			if (pawn.records.GetAsInt(RecordDefOf.TimeAsColonistOrColonyAnimal) > 0)
			{
				return true;
			}

			if (pawn.records.GetAsInt(RecordDefOf.TimeAsPrisoner) > 0)
			{
				return true;
			}

			if (PawnGenerator.IsBeingGenerated(pawn))
			{
				return true;
			}

			if (PawnUtility.IsFactionLeader(pawn))
			{
				return true;
			}
			if (PawnUtility.IsKidnappedPawn(pawn))
			{
				return true;
			}
			if (pawn.IsCaravanMember())
			{
				return true;
			}
			if (PawnUtility.IsTravelingInTransportPodWorldObject(pawn))
			{
				return true;
			}
            // TODO (KA): No idea what this should resolve to in A17...
            //if (PawnUtility.IsNonPlayerFactionBasePrisoner(pawn))
            //{
            //	return true;
            //}

            return false;
		}

		//"Unimportant" keep pawns means we don't care about keeping relatives the player has never seen
		private static bool IsUnimportantKeepPawn(Pawn pawn)
		{
			if (pawn.Discarded) { return false; }

			//Check for animals that meet important reasons
			if (IsImportantKeepPawn(pawn, true)) { return true; }

			//Colony animals
			if (pawn.records.GetAsInt(RecordDefOf.TimeAsColonistOrColonyAnimal) > 0)
			{
				if (pawn.Name != null && !pawn.Name.Numerical)
				{
					return true;
				}
				using (new RandSeed(pawn.thingIDNumber * 153))
				{
					if (Rand.Chance(0.05f)) { return true; }
				}
			}

			//Corpse still exists
			if (pawn.Corpse != null)
			{
				return true;
			}

			if (!pawn.Dead && !pawn.Destroyed && pawn.RaceProps.Humanlike)
			{
				using (new RandSeed(pawn.thingIDNumber * 681))
				{
					if (Rand.Chance(0.1f)) { return true; }
				}
			}

			if (Current.ProgramState == ProgramState.Playing)
			{
				if (Find.PlayLog.AnyEntryConcerns(pawn))
				{
					return true;
				}
				if (Find.TaleManager.AnyTaleConcerns(pawn))
				{
					return true;
				}
			}

			return false;
		}

#endregion

		[DetourMember]
		public new void LogWorldPawns()
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("======= World Pawns =======");
			stringBuilder.AppendLine("Count: " + AllPawnsAliveOrDead.Count());
			WorldPawnSituation[] array = (WorldPawnSituation[])Enum.GetValues(typeof(WorldPawnSituation));
			for (int i = 0; i < array.Length; i++)
			{
				WorldPawnSituation worldPawnSituation = array[i];
				if (worldPawnSituation != WorldPawnSituation.None)
				{
					stringBuilder.AppendLine();
					stringBuilder.AppendLine("== " + worldPawnSituation + " ==");
					foreach (Pawn current in from x in GetPawnsBySituation(worldPawnSituation)
											 orderby x?.Faction?.loadID ?? -1
											 select x)
					{
						stringBuilder.AppendLine(string.Concat(current.Name?.ToStringFull, ", ", current.KindLabel, ", ", current.Faction, ", ", KeepReason(current)));
					}
				}
			}
			stringBuilder.AppendLine("===========================");
			Log.Message(stringBuilder.ToString());
		}

		private string KeepReason(Pawn pawn)
		{
			if (pawn.Discarded)
			{
				return "";
			}
			if (pawn.records.GetAsInt(RecordDefOf.TimeAsColonistOrColonyAnimal) > 0)
			{
				if (pawn.RaceProps.Humanlike || (pawn.Name != null && !pawn.Name.Numerical))
				{
					return "NamedColonyPawn";
				}
				Rand.PushState();
				Rand.Seed = pawn.thingIDNumber * 153;
				bool flag = Rand.Chance(0.05f);
				Rand.PopState();
				if (flag)
				{
					return "RandColonyPawn";
				}
			}
			if (pawn.records.GetAsInt(RecordDefOf.TimeAsPrisoner) > 0)
			{
				return "Prisoner";
			}
			if (pawn.Corpse != null)
			{
				return "Corpse";
			}
			if (PawnGenerator.IsBeingGenerated(pawn))
			{
				return "BeingGenerated";
			}
			if (pawnsForcefullyKeptAsWorldPawns.Contains(pawn))
			{
				return "ForcefullyKept";
			}
			if (!pawn.Dead && !pawn.Destroyed && pawn.RaceProps.Humanlike)
			{
				Rand.PushState();
				Rand.Seed = pawn.thingIDNumber * 681;
				bool flag2 = Rand.Chance(0.1f);
				Rand.PopState();
				if (flag2)
				{
					return "RandOutsider";
				}
			}
			if (PawnUtility.IsFactionLeader(pawn))
			{
				return "FactionLeader";
			}
			if (PawnUtility.IsKidnappedPawn(pawn))
			{
				return "Kidnapped";
			}
			if (pawn.IsCaravanMember())
			{
				return "Caravan";
			}
			if (PawnUtility.IsTravelingInTransportPodWorldObject(pawn))
			{
				return "TransportPod";
			}
            // TODO (KA): No idea what this should resolve to in A17...
			//if (PawnUtility.IsNonPlayerFactionBasePrisoner(pawn))
			//{
			//	return "NonPlayerPrisoner";
			//}
			if (Current.ProgramState == ProgramState.Playing)
			{
				if (Find.PlayLog.AnyEntryConcerns(pawn))
				{
					return "PlayLog";
				}
				if (Find.TaleManager.AnyTaleConcerns(pawn))
				{
					return "Tale";
				}
			}
			foreach (Pawn current in PawnsFinder.AllMapsAndWorld_Alive)
			{
				if (current.needs.mood != null && current.needs.mood.thoughts.memories.AnyMemoryConcerns(pawn))
				{
					return "Memories";
				}
			}
			if (pawn.RaceProps.IsFlesh)
			{
				if (pawn.relations.RelatedPawns.Any(x => x.relations.everSeenByPlayer))
				{
					return "Relative";
				}
			}
			return "";
		}

		private static readonly Func<WorldPawns, HashSet<Pawn>> pawnsForcefullyKeptAsWorldPawnsGet = Utils.GetFieldAccessor<WorldPawns, HashSet<Pawn>>("pawnsForcefullyKeptAsWorldPawns");

		private static readonly FieldInfo pawnsForcefullyKeptAsWorldPawnsInfo = typeof(WorldPawns).GetField("pawnsForcefullyKeptAsWorldPawns", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);

		private HashSet<Pawn> pawnsForcefullyKeptAsWorldPawns { get { return pawnsForcefullyKeptAsWorldPawnsGet(this); } set { pawnsForcefullyKeptAsWorldPawnsInfo.SetValue(this, value); } }

	}


	public class RandSeed : IDisposable
	{
		private bool hasPopped = false;

		public RandSeed(int seed)
		{
			Rand.PushState();
			Rand.Seed = seed;
		}

		public void Dispose()
		{
			if (hasPopped) { return; }
            
            Rand.PopState();
			hasPopped = true;
		}

	}


	public class FactionBase_TraderTrackerDetour : Settlement_TraderTracker
    {
		public FactionBase_TraderTrackerDetour() : base(null) { }


		private static readonly Func<Settlement_TraderTracker, ThingOwner<Thing>> stockGet = Utils.GetFieldAccessor<Settlement_TraderTracker, ThingOwner<Thing>>("stock");

		private static readonly FieldInfo stockInfo = typeof(FactionBase_TraderTracker).GetField("stock", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);

		private ThingOwner<Thing> stock { get { return stockGet(this); } set { stockInfo.SetValue(this, value); } }


		private static readonly Func<Settlement_TraderTracker, int> lastStockGenerationTicksGet = Utils.GetFieldAccessor<Settlement_TraderTracker, int>("lastStockGenerationTicks");

		private static readonly FieldInfo lastStockGenerationTicksInfo = typeof(FactionBase_TraderTracker).GetField("lastStockGenerationTicks", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);

		private int lastStockGenerationTicks { get { return lastStockGenerationTicksGet(this); } set { lastStockGenerationTicksInfo.SetValue(this, value); } }


        public override TraderKindDef TraderKind
        {
            get
            {
                // TODO (KA): This override is now required, but I have no idea what to do here...s
                return null;
            }
        }

        [DetourMember]
		public new void TraderTrackerTick()
		{
			if (Find.TickManager.TicksGame - lastStockGenerationTicks > 600000) { TryDestroyStock(); }

			if (stock != null)
			{
				for (int i = stock.Count - 1; i >= 0; i--)
				{
					Pawn pawn = stock[i] as Pawn;
					if (pawn != null && pawn.Destroyed)
					{
						stock.RemoveAt(i);
						Find.WorldPawns.DiscardIfUnimportant(pawn);
					}
				}
				for (int j = stock.Count - 1; j >= 0; j--)
				{
					Pawn pawn2 = stock[j] as Pawn;
					if (pawn2 != null && !pawn2.IsWorldPawn())
					{
						Log.Error("Faction base has non-world-pawns in its stock. Removing...");
						stock.RemoveAt(j);
					}
				}
			}
		}
		
	}


}
