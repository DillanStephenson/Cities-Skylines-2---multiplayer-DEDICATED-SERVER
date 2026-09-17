using System;
using System.IO;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// A policy toggled or adjusted on one building, district or transport line through its panel
    /// ("switch off", "paid parking", district rules, ticket prices ...). City-wide policies travel as city
    /// state instead. The target is referenced the same way build commands reference entities: by prefab
    /// and position.
    /// </summary>
    public sealed class PolicyCommand
    {
        public const string Kind = "policy";
        public const byte Version = 1;

        public EntityRef Target = new EntityRef();
        public PrefabKey Policy = new PrefabKey();
        public bool Active;
        public float Adjustment;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                Target.Write(writer);
                Policy.Write(writer);
                writer.Write(Active);
                writer.Write(Adjustment);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static PolicyCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported policy command version " + version);
                    }

                    return new PolicyCommand
                    {
                        Target = EntityRef.Read(reader),
                        Policy = PrefabKey.Read(reader),
                        Active = reader.ReadBoolean(),
                        Adjustment = reader.ReadSingle(),
                    };
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed policy command: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return "policy " + Policy + (Active ? " on" : " off") + (Adjustment != 0f ? " (" + Adjustment + ")" : "") + " for " + Target;
        }
    }
}
