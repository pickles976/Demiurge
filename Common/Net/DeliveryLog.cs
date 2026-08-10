using System.Text;

namespace Demiurge.Net
{
    public enum DeliveryVerdict : byte
    {
        Delivered,
        Dropped,
        Duplicated,
    }

    public readonly record struct DeliveryRecord(
        long Sequence,
        ushort MessageId,
        MessageSendMode Mode,
        DeliveryVerdict Verdict,
        bool ToServer)
    {
        public override string ToString()
            => $"#{Sequence} {(ToServer ? "C->S" : "S->C")} id={MessageId} {Mode} {Verdict}";
    }

    /// <summary>
    /// Fixed-size ring of the most recent delivery decisions.
    /// </summary>
    /// <remarks>
    /// This exists because a seed alone cannot reproduce a live session — see
    /// <see cref="TransportHostility.DeliveryLogCapacity"/>. When something glitches, the seed tells you
    /// which policy was in force and this tells you what it actually did.
    /// </remarks>
    public sealed class DeliveryLog
    {
        private readonly DeliveryRecord[] entries;
        private readonly object gate = new();
        private long written;

        public DeliveryLog(int capacity = TransportHostility.DeliveryLogCapacity)
            => entries = new DeliveryRecord[capacity];

        public void Record(in DeliveryRecord record)
        {
            lock (gate)
            {
                entries[(int)(written % entries.Length)] = record;
                written++;
            }
        }

        /// <summary>Most recent first.</summary>
        public IReadOnlyList<DeliveryRecord> Snapshot()
        {
            lock (gate)
            {
                int count = (int)Math.Min(written, entries.Length);
                var result = new DeliveryRecord[count];
                for (int i = 0; i < count; i++)
                    result[i] = entries[(int)((written - 1 - i) % entries.Length)];
                return result;
            }
        }

        public string Dump()
        {
            var builder = new StringBuilder();
            foreach (DeliveryRecord record in Snapshot())
                builder.AppendLine(record.ToString());
            return builder.Length == 0 ? "(no deliveries recorded)" : builder.ToString();
        }
    }
}
