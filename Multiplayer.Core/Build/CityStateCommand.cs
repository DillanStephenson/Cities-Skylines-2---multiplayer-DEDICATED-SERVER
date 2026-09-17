using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// City-wide settings that are changed through panels rather than tools: tax rates, service budgets,
    /// service fees, city policies. Each is a named number; senders broadcast what changed, receivers apply
    /// it through the game's own setters. Keys are stable strings such as "tax:area:Residential",
    /// "budget:ServicePrefab|Healthcare", "fee:Electricity", "policy:CityPolicyPrefab|Free Parking:active".
    /// </summary>
    public sealed class CityStateCommand
    {
        public const string Kind = "citystate";
        public const byte Version = 1;

        /// <summary>True when this carries the sender's whole state, not only what changed.</summary>
        public bool FullSnapshot;

        public List<StateEntry> Entries = new List<StateEntry>();

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write(FullSnapshot);
                writer.Write(Entries.Count);
                foreach (StateEntry entry in Entries)
                {
                    writer.WriteText(entry.Key);
                    writer.Write(entry.Value);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static CityStateCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported city state version " + version);
                    }

                    var command = new CityStateCommand { FullSnapshot = reader.ReadBoolean() };
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 100000)
                    {
                        throw new ProtocolException("Unreasonable entry count " + count);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        command.Entries.Add(new StateEntry(reader.ReadText(), reader.ReadSingle()));
                    }

                    return command;
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed city state: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return (FullSnapshot ? "city state snapshot" : "city state change") + " (" + Entries.Count + " entries)";
        }
    }

    public struct StateEntry
    {
        public string Key;
        public float Value;

        public StateEntry(string key, float value)
        {
            Key = key ?? string.Empty;
            Value = value;
        }

        public override string ToString()
        {
            return Key + "=" + Value;
        }
    }
}
