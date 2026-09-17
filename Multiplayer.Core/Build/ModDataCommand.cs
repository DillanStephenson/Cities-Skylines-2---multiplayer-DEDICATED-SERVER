using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>An entity reference stored inside a mirrored struct: where it sits in the bytes and what it points at.</summary>
    public sealed class EntityPatch
    {
        public int Offset;
        public EntityRef Ref = new EntityRef();
    }

    /// <summary>
    /// A component or buffer that another mod keeps on an entity (Traffic Tool Essentials' junction settings,
    /// for example), sent as the raw bytes of the struct. Both games run the same mod version, so the bytes
    /// mean the same on each side; only entity references inside are translated, through <see cref="Entities"/>.
    /// </summary>
    public sealed class ModDataCommand
    {
        public const string Kind = "moddata";
        public const byte Version = 1;
        public const int MaxDataBytes = 1024 * 1024;

        public EntityRef Target = new EntityRef();
        public string TypeName = string.Empty;
        public bool IsBuffer;
        public bool Remove;
        public int ElementSize;
        public byte[] Data = new byte[0];
        public List<EntityPatch> Entities = new List<EntityPatch>();

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                Target.Write(writer);
                writer.WriteText(TypeName);
                writer.Write(IsBuffer);
                writer.Write(Remove);
                writer.Write(ElementSize);
                writer.Write(Data.Length);
                writer.Write(Data);
                writer.Write(Entities.Count);
                foreach (EntityPatch patch in Entities)
                {
                    writer.Write(patch.Offset);
                    patch.Ref.Write(writer);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static ModDataCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported mod data version " + version);
                    }

                    var command = new ModDataCommand
                    {
                        Target = EntityRef.Read(reader),
                        TypeName = reader.ReadText(),
                        IsBuffer = reader.ReadBoolean(),
                        Remove = reader.ReadBoolean(),
                        ElementSize = reader.ReadInt32(),
                    };

                    int length = reader.ReadInt32();
                    if (length < 0 || length > MaxDataBytes)
                    {
                        throw new ProtocolException("Unreasonable mod data length " + length);
                    }

                    command.Data = reader.ReadBytes(length);
                    int patches = reader.ReadInt32();
                    if (patches < 0 || patches > 100000)
                    {
                        throw new ProtocolException("Unreasonable patch count " + patches);
                    }

                    for (int i = 0; i < patches; i++)
                    {
                        command.Entities.Add(new EntityPatch { Offset = reader.ReadInt32(), Ref = EntityRef.Read(reader) });
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
                throw new ProtocolException("Malformed mod data: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            string type = TypeName.Substring(TypeName.LastIndexOf('.') + 1);
            return (Remove ? "remove " : "set ") + type + (IsBuffer && !Remove ? "[" + (ElementSize > 0 ? Data.Length / ElementSize : 0) + "]" : "") + " on " + Target;
        }
    }
}
