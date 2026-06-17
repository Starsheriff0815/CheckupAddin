#if NET48
// Polyfills that allow C# 8 range (`[1..^1]`, `[..N]`) and index (`[^1]`) syntax
// to compile on .NET Framework 4.8, where these types are not shipped by the runtime.
// The compiler emits calls to the methods defined here when it sees range/index expressions.
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        private Index(int value) { _value = value; }

        public static Index Start => new Index(0);
        public static Index End   => new Index(~0);

        public bool IsFromEnd => _value < 0;
        public int  Value     => _value < 0 ? ~_value : _value;

        public int GetOffset(int length)
        {
            int offset = _value;
            if (IsFromEnd) offset += length + 1;
            return offset;
        }

        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object value) => value is Index i && _value == i._value;
        public override int  GetHashCode() => _value;
        public override string ToString() => IsFromEnd ? "^" + Value.ToString() : Value.ToString();

        public static implicit operator Index(int value) => new Index(value);
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End   { get; }

        public Range(Index start, Index end) { Start = start; End = end; }

        public static Range All              => new Range(Index.Start, Index.End);
        public static Range StartAt(Index s) => new Range(s, Index.End);
        public static Range EndAt(Index e)   => new Range(Index.Start, e);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end   = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object value) => value is Range r && Equals(r);
        public override int  GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => Start.ToString() + ".." + End.ToString();
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class RuntimeHelpers
    {
        // Called by the compiler for array[range] slicing expressions.
        public static T[] GetSubArray<T>(T[] array, System.Range range)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            var (offset, length) = range.GetOffsetAndLength(array.Length);
            if (typeof(T).IsValueType || typeof(T[]) == typeof(object[]))
            {
                if (length == 0) return System.Array.Empty<T>();
                var dest = new T[length];
                System.Array.Copy(array, offset, dest, 0, length);
                return dest;
            }
            {
                var dest = (T[])(object)new object[length];
                System.Array.Copy(array, offset, dest, 0, length);
                return dest;
            }
        }
    }
}
#endif
