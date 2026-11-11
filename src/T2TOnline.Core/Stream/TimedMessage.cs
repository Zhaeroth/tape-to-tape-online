using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>One non-snapshot stream message (an event, sound or HUD change), due at its time on the host's stream clock.</summary>
/// <param name="TimeMicros">When the host sent it, on the stream clock snapshots carry.</param>
/// <param name="Type">Its type.</param>
/// <param name="Payload">The bytes after the header.</param>
public readonly record struct TimedMessage(long TimeMicros, MessageType Type, byte[] Payload);
