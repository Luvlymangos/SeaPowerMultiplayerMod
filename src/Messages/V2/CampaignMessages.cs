using System.Collections.Generic;
using LiteNetLib.Utils;
using SeapowerMultiplayer.Campaign;

namespace SeapowerMultiplayer.Messages
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Co-op campaign layer (fork addition). Wire messages for sharing a campaign
    // run by a campaign mod (first target: Bungalow's Dynamic Campaign Mod).
    //
    // The model is the same one the battle sync uses: the HOST runs the only real
    // campaign. The guest's copy is a display + input surface - its actions travel
    // to the host as CampaignCommand, the host validates ownership and executes,
    // then streams the resulting state back as CampaignSnapshot chunks.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Client → host: "the guest tried to do this on the campaign map".
    /// The guest's own campaign code never ran it - the host decides.</summary>
    public class CampaignCommandMessage : INetMessage
    {
        /// <summary>Guest-local sequence number, echoed in the result so the guest
        /// can match verdicts to the actions that caused them.</summary>
        public uint Seq;
        public CampaignCommand Command = new();

        public MessageType Type => MessageType.CampaignCommand;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Seq);
            Command.Serialize(writer);
        }

        public static CampaignCommandMessage Deserialize(NetDataReader reader) => new()
        {
            Seq     = reader.GetUInt(),
            Command = CampaignCommand.Deserialize(reader),
        };
    }

    /// <summary>Host → client: verdict on one CampaignCommand.</summary>
    public class CampaignCommandResultMessage : INetMessage
    {
        public uint   Seq;
        public bool   Accepted;
        public string Reason = "";
        /// <summary>Snapshot revision that will contain the command's effect (0 when
        /// rejected). Lets the guest show "pending" until that revision arrives.</summary>
        public uint   AppliedInRevision;

        public MessageType Type => MessageType.CampaignCommandResult;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Seq);
            writer.Put(Accepted);
            writer.Put(Reason ?? "");
            writer.Put(AppliedInRevision);
        }

        public static CampaignCommandResultMessage Deserialize(NetDataReader reader) => new()
        {
            Seq               = reader.GetUInt(),
            Accepted          = reader.GetBool(),
            Reason            = reader.GetString(),
            AppliedInRevision = reader.GetUInt(),
        };
    }

    /// <summary>
    /// Host → client: one piece of a full campaign state snapshot.
    ///
    /// CHUNKED ON PURPOSE. Reliable packets above the ~1000-byte MTU floor are
    /// fragmented by LiteNetLib and have been seen arriving corrupted in the game's
    /// Mono runtime (see FlightDeckStreamer). A campaign state is easily tens of KB,
    /// so it is gzipped and split into sub-MTU chunks the client reassembles.
    /// </summary>
    public class CampaignSnapshotChunkMessage : INetMessage
    {
        /// <summary>Monotonic campaign revision on the host. A newer revision
        /// arriving mid-reassembly discards the older partial one.</summary>
        public uint   Revision;
        public ushort ChunkIdx;
        public ushort ChunkCount;
        /// <summary>Phase the host's campaign is in (map / battle / ended).</summary>
        public CampaignPhase Phase;
        /// <summary>Adapter id + campaign mod version, so a mismatch is named
        /// instead of producing a garbled map.</summary>
        public string AdapterId  = "";
        public string ModVersion = "";
        /// <summary>Host campaign clock at capture (adapter's unit, e.g. campaign
        /// seconds) and its time compression. The campaign runs on a continuous
        /// clock, so the guest needs both to carry fleets forward between snapshots
        /// instead of showing them jump once a second.</summary>
        public double HostClock;
        public float  HostTimeScale;
        public byte[] Payload    = System.Array.Empty<byte>();

        public MessageType Type => MessageType.CampaignSnapshotChunk;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Revision);
            writer.Put(ChunkIdx);
            writer.Put(ChunkCount);
            writer.Put((byte)Phase);
            // Header strings ride only on chunk 0 to keep the others small.
            if (ChunkIdx == 0)
            {
                writer.Put(AdapterId ?? "");
                writer.Put(ModVersion ?? "");
                writer.Put(HostClock);
                writer.Put(HostTimeScale);
            }
            writer.PutBytesWithLength(Payload);
        }

        public static CampaignSnapshotChunkMessage Deserialize(NetDataReader reader)
        {
            var msg = new CampaignSnapshotChunkMessage
            {
                Revision   = reader.GetUInt(),
                ChunkIdx   = reader.GetUShort(),
                ChunkCount = reader.GetUShort(),
                Phase      = (CampaignPhase)reader.GetByte(),
            };
            if (msg.ChunkIdx == 0)
            {
                msg.AdapterId     = reader.GetString();
                msg.ModVersion    = reader.GetString();
                msg.HostClock     = reader.GetDouble();
                msg.HostTimeScale = reader.GetFloat();
            }
            msg.Payload = reader.GetBytesWithLength();
            return msg;
        }
    }

    /// <summary>Client → host: "send me a full snapshot" (join, reassembly
    /// failure, or after returning from a battle).</summary>
    public class CampaignResyncRequestMessage : INetMessage
    {
        /// <summary>Newest revision the client has applied (0 = none).</summary>
        public uint HaveRevision;
        public string Reason = "";

        public MessageType Type => MessageType.CampaignResyncRequest;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(HaveRevision);
            writer.Put(Reason ?? "");
        }

        public static CampaignResyncRequestMessage Deserialize(NetDataReader reader) => new()
        {
            HaveRevision = reader.GetUInt(),
            Reason       = reader.GetString(),
        };
    }

    /// <summary>
    /// Host → client: who commands what on the campaign map. Whole table every
    /// time - it is small (one row per fleet/theatre/command post) and changes
    /// rarely, so a diff protocol would be complexity for nothing.
    /// </summary>
    public class CampaignOwnershipMessage : INetMessage
    {
        public readonly List<(string assetKey, CampaignSeat owner)> Rows = new();
        /// <summary>Owner of anything not listed.</summary>
        public CampaignSeat DefaultOwner = CampaignSeat.Shared;

        public MessageType Type => MessageType.CampaignOwnership;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)DefaultOwner);
            writer.Put((ushort)Rows.Count);
            for (int i = 0; i < Rows.Count; i++)
            {
                writer.Put(Rows[i].assetKey);
                writer.Put((byte)Rows[i].owner);
            }
        }

        public static CampaignOwnershipMessage Deserialize(NetDataReader reader)
        {
            var msg = new CampaignOwnershipMessage { DefaultOwner = (CampaignSeat)reader.GetByte() };
            int n = reader.GetUShort();
            for (int i = 0; i < n; i++)
                msg.Rows.Add((reader.GetString(), (CampaignSeat)reader.GetByte()));
            return msg;
        }
    }
}
