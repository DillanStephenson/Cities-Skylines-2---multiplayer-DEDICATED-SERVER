using System;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// A named piece of configuration another mod created at runtime and the others need before they can
    /// replay anything that uses it: a Road Builder road, for one. Carried as the mod's own JSON so the
    /// receiving mod parses and registers it exactly as it would a file of its own.
    /// </summary>
    public sealed class ModConfigCommand
    {
        public const string Kind = "roadconfig";
        public const byte Version = 1;
        public const int MaxJsonLength = 256 * 1024;

        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Json = string.Empty;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.WriteText(Id);
                writer.WriteText(Name);
                writer.WriteText(Json ?? string.Empty);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static ModConfigCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported mod config version " + version);
                    }

                    var command = new ModConfigCommand { Id = reader.ReadText(), Name = reader.ReadText(), Json = reader.ReadText() };
                    if (command.Json.Length > MaxJsonLength)
                    {
                        throw new ProtocolException("Mod config too large: " + command.Json.Length + " characters");
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
                throw new ProtocolException("Malformed mod config: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return "road config '" + Name + "' (" + Id + ", " + (Json != null ? Json.Length : 0) + " chars)";
        }
    }
}
